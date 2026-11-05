using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Realtime;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// A logout reaches open live connections through the existing periodic session check (WAS-114): the real Web pipeline
/// (<see cref="TenantSessionWebHost"/>), real SignalR clients over WebSockets, and the real
/// <see cref="RealtimeSessionRevalidator"/>, whose interval timers alone run on <see cref="ManualTimers"/>, so each check
/// runs when the test fires it instead of after a wall-clock wait. Results are read from the hub's own metrics.
/// </summary>
public sealed class RealtimeLogoutTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_ClosesThatSessionsConnectionAtTheNextCheck_RefusesItsReconnect_AndKeepsAnotherSession()
    {
        var timers = new ManualTimers();
        await using var host = await TenantSessionWebHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<RealtimeSessionRevalidator>();
            services.AddSingleton(sp => new RealtimeSessionRevalidator(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<RealtimeOptions>(),
                timers,
                sp.GetRequiredService<ILogger<RealtimeSessionRevalidator>>(),
                sp.GetRequiredService<RealtimeMetrics>()));
        });
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        var user = host.SeedUser(host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        using var loggedOut = host.Client(TenantSessionWebHost.AlphaHost);
        await loggedOut.LoginAsync(user.Email);
        using var kept = host.Client(TenantSessionWebHost.AlphaHost);
        await kept.LoginAsync(user.Email);
        using var copy = loggedOut.ReplayTo(host.BaseAddress, TenantSessionWebHost.AlphaHost);
        await using var closing = await LiveHubClient.ConnectAsync(host.BaseAddress, loggedOut, Ct);
        await using var staying = await LiveHubClient.ConnectAsync(host.BaseAddress, kept, Ct);
        Assert.True(await timers.WaitForArmedAsync(2, Wait, Ct), "Both connections should wait for their first session check.");

        var token = await loggedOut.AntiforgeryTokenAsync("/auth/access-denied");
        var logout = await loggedOut.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        // No new transport: an open connection learns of the logout only from its next check.
        Assert.False(closing.Closed.IsCompleted);

        Assert.Equal(2, timers.FireAll());

        Assert.Same(closing.Closed, await Task.WhenAny(closing.Closed, Task.Delay(Wait, Ct)));
        Assert.True(await metrics.WaitForAsync(
            m => m.Sum("wasla.realtime.session_checks", ("wasla.realtime.result", "session_invalid")) == 1
                 && m.Sum("wasla.realtime.session_checks", ("wasla.realtime.result", "valid")) == 1,
            Wait, Ct), metrics.Describe());
        Assert.True(await metrics.WaitForAsync(
            m => m.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "session_invalid")) == 1, Wait, Ct),
            metrics.Describe());
        // The other session passed its check and waits for the next one, still open.
        Assert.True(await timers.WaitForArmedAsync(1, Wait, Ct), "The kept connection should wait for its next check.");
        Assert.False(staying.Closed.IsCompleted);

        // A reconnect with a copy of the logged-out cookie is refused at negotiate; the kept session still negotiates.
        Assert.NotEqual(HttpStatusCode.OK, (await NegotiateAsync(copy)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(kept)).StatusCode);
    }

    private static Task<HttpResponseMessage> NegotiateAsync(TenantSessionClient client) =>
        client.SendAsync(HttpMethod.Post, $"{TenantEventsHub.Path}/negotiate?negotiateVersion=1", new StringContent(""));
}

/// <summary>
/// The system clock, with timers that fire only when the test calls <see cref="FireAll"/>. <c>Task.Delay</c> with a
/// time provider creates one such timer per wait, so a periodic loop is advanced one step per call.
/// </summary>
internal sealed class ManualTimers : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _armed = [];

    public int Armed
    {
        get
        {
            lock (_gate)
                return _armed.Count;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Fires every armed timer once; returns how many fired.</summary>
    public int FireAll()
    {
        List<Timer> due;
        lock (_gate)
        {
            due = [.. _armed];
            _armed.Clear();
        }

        foreach (var timer in due)
            timer.Fire();
        return due.Count;
    }

    /// <summary>Waits until exactly <paramref name="count"/> timers are armed; false after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForArmedAsync(int count, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Armed != count)
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(20, ct);
        }

        return true;
    }

    private sealed class Timer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._armed.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                    owner._armed.Add(this);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
                owner._armed.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
