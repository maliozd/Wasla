using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Realtime;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-25: the <c>Wasla.Realtime</c> metrics against the real Web pipeline (<see cref="TenantSessionWebHost"/>), real
/// hub clients and the real internal channel. Each instrument is recorded with its documented result or reason; the
/// active connection count goes back to zero; and no tag carries an identifier, a host, a message or a secret.
/// </summary>
public sealed class RealtimeMetricsTests
{
    private const string Secret = "test-only-metrics-secret-0123456789abcdef";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every tag key a Wasla.Realtime instrument may carry, with every value it may take.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTags = new Dictionary<string, IReadOnlySet<string>>
    {
        [RealtimeMetrics.ChannelTag] = new HashSet<string> { "hub", "internal" },
        [RealtimeMetrics.StreamTag] = new HashSet<string> { "print_status", "live_screen", "other" },
        [RealtimeMetrics.StreamsTag] = new HashSet<string> { "print_status", "live_screen", "live_screen+print_status" },
        [RealtimeMetrics.ResultTag] = new HashSet<string>
        {
            "sent", "timeout", "cancelled", "rejected", "unreachable", "failed", "paused", "unsupported",
            "accepted", "unauthorized", "invalid", "not_found", "method_not_allowed",
            "admitted", "refused",
            "valid", "session_invalid", "role_changed", "tenant_unresolved", "check_unavailable", "check_failed"
        },
        [RealtimeMetrics.ReasonTag] = new HashSet<string>
        {
            "timeout", "cancelled", "rejected", "unreachable", "failed",
            "closed", "error", "session_invalid", "role_changed", "tenant_unresolved", "check_unavailable", "check_failed"
        },
        [RealtimeMetrics.AttemptTag] = new HashSet<string> { "initial", "reconnect", "unknown" }
    };

    /// <summary>Fails when any Wasla.Realtime measurement has a tag outside <see cref="AllowedTags"/>.</summary>
    internal static void AssertBoundedTags(RealtimeMetricsRecorder recorder, params string[] forbiddenValues)
    {
        foreach (var measurement in recorder.Wasla)
        {
            foreach (var (key, value) in measurement.Tags)
            {
                Assert.True(AllowedTags.TryGetValue(key, out var allowed), $"{measurement.Instrument} has an undocumented tag {key}.");
                Assert.True(allowed!.Contains(value), $"{measurement.Instrument} has {key}={value}, which is not a documented value.");
                Assert.False(Guid.TryParse(value, out _), $"{measurement.Instrument} carries an identifier in {key}.");
                foreach (var forbidden in forbiddenValues)
                    Assert.DoesNotContain(forbidden, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task HubConnections_AreCountedByResultAndDeclaredAttempt_AndTheActiveCountReturnsToZero()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        using var owner = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        using var kitchen = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Kitchen);

        var clients = new List<LiveHubClient>
        {
            await LiveHubClient.ConnectAsync(host.BaseAddress, owner, Ct, attempt: "initial"),
            await LiveHubClient.ConnectAsync(host.BaseAddress, owner, Ct, attempt: "reconnect"),
            // Anything the client declares besides the two known values, or nothing, is "unknown".
            await LiveHubClient.ConnectAsync(host.BaseAddress, kitchen, Ct, attempt: $"{host.BetaId}"),
            await LiveHubClient.ConnectAsync(host.BaseAddress, kitchen, Ct)
        };

        const string attempts = "wasla.realtime.connection.attempts";
        Assert.Equal(1, metrics.Sum(attempts, ("wasla.realtime.result", "admitted"), ("wasla.realtime.attempt", "initial")));
        Assert.Equal(1, metrics.Sum(attempts, ("wasla.realtime.result", "admitted"), ("wasla.realtime.attempt", "reconnect")));
        Assert.Equal(2, metrics.Sum(attempts, ("wasla.realtime.result", "admitted"), ("wasla.realtime.attempt", "unknown")));
        Assert.Equal(4, metrics.ActiveConnections);
        Assert.Equal(2, metrics.Sum("wasla.realtime.connections.active", ("wasla.realtime.streams", "live_screen+print_status")));
        Assert.Equal(2, metrics.Sum("wasla.realtime.connections.active", ("wasla.realtime.streams", "live_screen")));

        foreach (var client in clients)
            await client.DisposeAsync();

        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 0, Wait, Ct), metrics.Describe());
        Assert.Equal(4, metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "closed")));
        // ASP.NET Core's own transport-level count agrees once the connections are gone.
        Assert.True(await metrics.WaitForAsync(m => m.Sum("signalr.server.active_connections") == 0, Wait, Ct));
        AssertBoundedTags(metrics, host.AlphaId.ToString("N"), TenantSessionWebHost.AlphaHost);
    }

    [Fact]
    public async Task AConnectionClosedByTheSessionCheck_IsCountedWithItsReason_AndForeignOriginsAreCounted()
    {
        await using var host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Realtime:SessionRevalidationSeconds"] = "1"
        });
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        var user = host.SeedUser(host.AlphaId, $"{Guid.NewGuid():N}@alpha.test", UserRole.Manager);
        using var session = host.Client(TenantSessionWebHost.AlphaHost);
        await session.LoginAsync(user.Email);
        await using var live = await LiveHubClient.ConnectAsync(host.BaseAddress, session, Ct);

        Assert.True(await metrics.WaitForAsync(m => m.Sum("wasla.realtime.session_checks", ("wasla.realtime.result", "valid")) >= 1, Wait, Ct));
        host.UpdateUser(host.AlphaId, user.Id, u => u.IsActive = false);

        Assert.Same(live.Closed, await Task.WhenAny(live.Closed, Task.Delay(Wait, Ct)));
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 0, Wait, Ct), metrics.Describe());
        Assert.Equal(1, metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "session_invalid")));
        Assert.Equal(0, metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "closed")));
        Assert.Equal(1, metrics.Sum("wasla.realtime.session_checks", ("wasla.realtime.result", "session_invalid")));
        Assert.True(metrics.Count("wasla.realtime.session_check.duration") >= 2);

        // A hub request from another tenant's page is refused before tenant resolution, and counted.
        var refused = await session.SendAsync(
            HttpMethod.Post, $"{TenantEventsHub.Path}/negotiate?negotiateVersion=1", new StringContent(""),
            [new("Origin", $"http://{TenantSessionWebHost.BetaHost}")]);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(1, metrics.Sum("wasla.realtime.origin_refusals"));
        AssertBoundedTags(metrics, host.AlphaId.ToString("N"), user.Id.ToString("N"), user.Email);
    }

    [Fact]
    public async Task HubNotifications_AreCountedByStreamAndResult_WithTheirSendDuration()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();

        await notifier.NotifyAsync(host.AlphaId, new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1), Ct);
        await notifier.NotifyAsync(host.AlphaId, new OrderChanged(Guid.NewGuid(), 1), Ct);
        await notifier.NotifyAsync(host.AlphaId, new OrderChanged(Guid.NewGuid(), 2), Ct);
        await notifier.NotifyAsync(host.AlphaId, new UnknownChange(), Ct);

        const string notifications = "wasla.realtime.notifications";
        Assert.Equal(1, metrics.Sum(notifications, ("wasla.realtime.channel", "hub"), ("wasla.realtime.stream", "print_status"), ("wasla.realtime.result", "sent")));
        Assert.Equal(2, metrics.Sum(notifications, ("wasla.realtime.channel", "hub"), ("wasla.realtime.stream", "live_screen"), ("wasla.realtime.result", "sent")));
        Assert.Equal(1, metrics.Sum(notifications, ("wasla.realtime.channel", "hub"), ("wasla.realtime.stream", "other"), ("wasla.realtime.result", "unsupported")));
        // A duration for every send that was attempted; none for the unsupported change.
        Assert.Equal(3, metrics.Count("wasla.realtime.notification.duration"));
        Assert.All(metrics.Measurements.Where(m => m.Instrument == "wasla.realtime.notification.duration"), m => Assert.InRange(m.Value, 0, 2.5));
        AssertBoundedTags(metrics, host.AlphaId.ToString("N"));
    }

    private sealed record UnknownChange : TenantChange;

    [Fact]
    public async Task TheInternalChannel_CountsSendsRefusalsPausesAndListenerAnswers_WithoutExposingTheSecret()
    {
        var port = InternalTenantChangeChannelTests.FreeLoopbackPort();
        var channel = Channel(port, Secret);
        await using var host = await TenantSessionWebHost.StartAsync(configuration: channel);
        using var web = new RealtimeMetricsRecorder(host.Services);

        // A Worker with the right secret: sent, and the listener accepted it (and handed it to the hub).
        var (worker, workerLogs) = Sender(channel);
        using var workerMetrics = new RealtimeMetricsRecorder(worker);
        var notifier = worker.GetRequiredService<ITenantChangeNotifier>();
        await notifier.NotifyAsync(host.AlphaId, new OrderChanged(Guid.NewGuid(), 3), Ct);
        await notifier.NotifyAsync(host.AlphaId, new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printed, 4), Ct);
        Assert.Equal(1, workerMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.channel", "internal"), ("wasla.realtime.stream", "live_screen"), ("wasla.realtime.result", "sent")));
        Assert.Equal(1, workerMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.channel", "internal"), ("wasla.realtime.stream", "print_status"), ("wasla.realtime.result", "sent")));
        Assert.Equal(2, workerMetrics.Count("wasla.realtime.notification.duration"));
        Assert.Equal(2, web.Sum("wasla.realtime.internal.requests", ("wasla.realtime.result", "accepted")));
        Assert.Equal(2, web.Sum("wasla.realtime.notifications", ("wasla.realtime.channel", "hub"), ("wasla.realtime.result", "sent")));

        // A process with another secret: refused by Web, one pause, and later notifications dropped while paused.
        const string wrongSecret = "test-only-WRONG-secret-zyxwvutsrqponmlkji";
        var (stale, staleLogs) = Sender(Channel(port, wrongSecret));
        using var staleMetrics = new RealtimeMetricsRecorder(stale);
        var staleNotifier = stale.GetRequiredService<ITenantChangeNotifier>();
        for (var i = 0; i < 3; i++)
            await staleNotifier.NotifyAsync(host.AlphaId, new OrderChanged(Guid.NewGuid(), 1), Ct);
        Assert.Equal(1, staleMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "rejected")));
        Assert.Equal(2, staleMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "paused")));
        Assert.Equal(1, staleMetrics.Sum("wasla.realtime.internal.pauses", ("wasla.realtime.reason", "rejected")));
        Assert.Equal(1, web.Sum("wasla.realtime.internal.requests", ("wasla.realtime.result", "unauthorized")));

        // Malformed and misrouted requests to the listener.
        using var http = new HttpClient();
        var bad = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{InternalRealtimeOptions.Path}") { Content = JsonContent.Create(new { type = "OrderChanged" }) };
        bad.Headers.Add(InternalRealtimeOptions.SecretHeaderName, Secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(bad, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"http://127.0.0.1:{port}/elsewhere", new StringContent(""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync($"http://127.0.0.1:{port}{InternalRealtimeOptions.Path}", Ct)).StatusCode);
        Assert.Equal(1, web.Sum("wasla.realtime.internal.requests", ("wasla.realtime.result", "invalid")));
        Assert.Equal(1, web.Sum("wasla.realtime.internal.requests", ("wasla.realtime.result", "not_found")));
        Assert.Equal(1, web.Sum("wasla.realtime.internal.requests", ("wasla.realtime.result", "method_not_allowed")));

        // Neither secret appears in any tag or log line of either side.
        foreach (var recorder in new[] { web, workerMetrics, staleMetrics })
            AssertBoundedTags(recorder, Secret, wrongSecret, host.AlphaId.ToString("N"));
        foreach (var line in host.Logs.Entries.Concat(workerLogs.Entries).Concat(staleLogs.Entries))
        {
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongSecret, line, StringComparison.Ordinal);
        }

        Assert.Contains(staleLogs.Entries, l => l.Contains("HTTP 401", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableOrSilentWeb_IsCountedAsUnreachableOrTimeout_AndPausesTheChannel()
    {
        // Nothing listens on this port: refused at once.
        var (unreachable, _) = Sender(Channel(InternalTenantChangeChannelTests.FreeLoopbackPort(), Secret));
        using var unreachableMetrics = new RealtimeMetricsRecorder(unreachable);
        await unreachable.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(Guid.NewGuid(), new OrderChanged(Guid.NewGuid(), 1), Ct);
        Assert.Equal(1, unreachableMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "unreachable")));
        Assert.Equal(1, unreachableMetrics.Sum("wasla.realtime.internal.pauses", ("wasla.realtime.reason", "unreachable")));

        // A listener that accepts the TCP connection and never answers: the sender's own timeout.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
            var (timingOut, _) = Sender(Channel(silentPort, Secret, timeoutSeconds: 0.3));
            using var timeoutMetrics = new RealtimeMetricsRecorder(timingOut);
            var started = DateTime.UtcNow;
            await timingOut.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(Guid.NewGuid(), new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1), Ct);
            Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            Assert.Equal(1, timeoutMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "timeout")));
            Assert.Equal(1, timeoutMetrics.Sum("wasla.realtime.internal.pauses", ("wasla.realtime.reason", "timeout")));
            var duration = Assert.Single(timeoutMetrics.Measurements, m => m.Instrument == "wasla.realtime.notification.duration");
            Assert.InRange(duration.Value, 0.2, 5);
            AssertBoundedTags(timeoutMetrics);
        }
        finally
        {
            silent.Stop();
        }

        AssertBoundedTags(unreachableMetrics);
    }

    internal static Dictionary<string, string?> Channel(int port, string secret, double timeoutSeconds = 3) => new()
    {
        ["Realtime:Internal:Port"] = port.ToString(CultureInfo.InvariantCulture),
        ["Realtime:Internal:Secret"] = secret,
        ["Realtime:Internal:TimeoutSeconds"] = timeoutSeconds.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>What Wasla.Worker registers for the internal channel, with its own metrics and captured logs.</summary>
    internal static (ServiceProvider Services, CapturingLoggerProvider Logs) Sender(IReadOnlyDictionary<string, string?> channel)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug))
            .AddSingleton(TimeProvider.System)
            .AddWaslaWebTenantChangeNotifier(new ConfigurationBuilder().AddInMemoryCollection(channel).Build())
            .BuildServiceProvider();
        return (services, logs);
    }

    internal static async Task<TenantSessionClient> SignInAsync(TenantSessionWebHost host, string hostName, Guid tenantId, UserRole role)
    {
        var user = host.SeedUser(tenantId, $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local", role);
        var client = host.Client(hostName);
        await client.LoginAsync(user.Email);
        return client;
    }
}
