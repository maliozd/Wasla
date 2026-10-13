using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-23: the internal channel from Wasla.Worker and Wasla.Api to Wasla.Web. Its configuration is validated at
/// startup, its message accepts only complete known notifications, and the sender waits briefly, never throws, and
/// pauses after a failure so an unreachable Wasla.Web cannot slow the work it reports. The receiving end is a local
/// fake listener; the real Wasla.Web listener is covered by <see cref="TenantEventsHubTests"/>.
/// </summary>
public sealed class InternalTenantChangeChannelTests
{
    private const string Secret = "test-only-secret-0123456789abcdefghijklmn";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Configuration_IsOffWithoutAPort_AndRefusedWhenIncomplete()
    {
        Assert.Empty(new InternalRealtimeOptions().Validate());
        Assert.False(new InternalRealtimeOptions().IsEnabled);
        Assert.Empty(new InternalRealtimeOptions { Port = 5290, Secret = Secret }.Validate());

        Assert.NotEmpty(new InternalRealtimeOptions { Port = 5290 }.Validate());
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 5290, Secret = "short" }.Validate());
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 0, Secret = Secret }.Validate());
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 70000, Secret = Secret }.Validate());
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 5290, Secret = Secret, TimeoutSeconds = 30 }.Validate());
        // Sent as an HTTP header: a secret that cannot be one would silently disable the channel.
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 5290, Secret = Secret + " with spaces" }.Validate());
        Assert.NotEmpty(new InternalRealtimeOptions { Port = 5290, Secret = Secret + "ö" }.Validate());

        // Messages name the keys, never the value.
        var error = Assert.Throws<InvalidOperationException>(() => new InternalRealtimeOptions { Port = 5290, Secret = "short-secret-value" }.ThrowIfInvalid());
        Assert.Contains("Realtime:Internal:Secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("short-secret-value", error.Message, StringComparison.Ordinal);

        // Worker and Api refuse to start with an incomplete channel instead of silently dropping notifications.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Internal:Port"] = "5290" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWaslaWebTenantChangeNotifier(config));
    }

    [Fact]
    public void Message_RoundTrips_AndRefusesAnythingIncompleteOrUnknown()
    {
        var tenant = Guid.NewGuid();
        var change = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printing, 7);
        var message = InternalTenantChangeMessage.From(tenant, change);

        Assert.True(message.TryRead(out var readTenant, out var readChange));
        Assert.Equal(tenant, readTenant);
        Assert.Equal(change, readChange);

        Assert.False((message with { TenantId = Guid.Empty }).TryRead(out _, out _));
        Assert.False((message with { Type = "OrderChanged" }).TryRead(out _, out _));
        Assert.False((message with { OrderId = Guid.Empty }).TryRead(out _, out _));
        Assert.False((message with { PrintJobId = Guid.Empty }).TryRead(out _, out _));
        Assert.False((message with { Version = 0 }).TryRead(out _, out _));
        Assert.False((message with { Status = "3" }).TryRead(out _, out _));
        Assert.False((message with { Status = "Exploded" }).TryRead(out _, out _));
        Assert.False((message with { Status = "printing" }).TryRead(out _, out _));
    }

    [Fact]
    public async Task Sender_PostsTheNotificationWithTheSecret_ToTheLoopbackListener()
    {
        await using var listener = await FakeWebListener.StartAsync(HttpStatusCode.Accepted, delay: TimeSpan.Zero);
        var notifier = CreateSender(listener.Port, out _);
        var tenant = Guid.NewGuid();
        var change = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);

        await notifier.NotifyAsync(tenant, change, Ct);

        var request = Assert.Single(listener.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(InternalRealtimeOptions.Path, request.Path);
        Assert.Equal(Secret, request.Secret);
        var body = JsonSerializer.Deserialize<InternalTenantChangeMessage>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(InternalTenantChangeMessage.From(tenant, change), body);
        // Only identifiers, the status and the version travel.
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(
            ["orderId", "printJobId", "status", "tenantId", "type", "version"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Sender_DropsAChangeItCannotCarry_WithoutPausingTheChannel()
    {
        await using var listener = await FakeWebListener.StartAsync(HttpStatusCode.Accepted, delay: TimeSpan.Zero);
        var notifier = CreateSender(listener.Port, out _);

        await notifier.NotifyAsync(Guid.NewGuid(), new UnknownChange(), Ct);
        await notifier.NotifyAsync(Guid.NewGuid(), new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1), Ct);

        Assert.Single(listener.Requests);
    }

    private sealed record UnknownChange : TenantChange;

    [Fact]
    public async Task Sender_WaitsOnlyForItsTimeout_NeverThrows_AndPausesAfterAFailure()
    {
        await using var listener = await FakeWebListener.StartAsync(HttpStatusCode.Accepted, delay: TimeSpan.FromSeconds(10));
        var notifier = CreateSender(listener.Port, out var clock, timeoutSeconds: 0.5);
        var change = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);

        var watch = Stopwatch.StartNew();
        await notifier.NotifyAsync(Guid.NewGuid(), change, Ct);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Single(listener.Requests);

        // Paused: no further attempt for 30 seconds, and no waiting.
        watch.Restart();
        await notifier.NotifyAsync(Guid.NewGuid(), change, Ct);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Single(listener.Requests);

        clock.Advance(WebTenantChangeNotifier.SuspendAfterFailure + TimeSpan.FromSeconds(1));
        listener.Delay = TimeSpan.Zero;
        await notifier.NotifyAsync(Guid.NewGuid(), change, Ct);
        Assert.Equal(2, listener.Requests.Count);
    }

    [Fact]
    public async Task Sender_TreatsARefusalOrAnUnreachableWeb_AsAPausedChannel_NotAnError()
    {
        await using (var refusing = await FakeWebListener.StartAsync(HttpStatusCode.Unauthorized, delay: TimeSpan.Zero))
        {
            var notifier = CreateSender(refusing.Port, out _);
            await notifier.NotifyAsync(Guid.NewGuid(), new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printed, 3), Ct);
            await notifier.NotifyAsync(Guid.NewGuid(), new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printed, 3), Ct);
            Assert.Single(refusing.Requests);
        }

        var unreachable = CreateSender(FreeLoopbackPort(), out _);
        await unreachable.NotifyAsync(Guid.NewGuid(), new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printed, 3), Ct);
    }

    private static ITenantChangeNotifier CreateSender(int port, out ManualClock clock, double timeoutSeconds = 2)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = Secret,
            ["Realtime:Internal:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        clock = new ManualClock();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ITenantChangeNotifier>(NullTenantChangeNotifier.Instance);
        services.AddWaslaWebTenantChangeNotifier(configuration);
        var notifier = services.BuildServiceProvider().GetRequiredService<ITenantChangeNotifier>();
        Assert.IsType<WebTenantChangeNotifier>(notifier);
        return notifier;
    }

    internal static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeWebListener : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeWebListener(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public int Port { get; }

        public TimeSpan Delay { get; set; }

        public ConcurrentQueue<(string Method, string Path, string Secret, string Body)> RequestQueue { get; } = new();

        public IReadOnlyList<(string Method, string Path, string Secret, string Body)> Requests => RequestQueue.ToArray();

        public static async Task<FakeWebListener> StartAsync(HttpStatusCode status, TimeSpan delay)
        {
            var port = FreeLoopbackPort();
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            var listener = new FakeWebListener(app, port) { Delay = delay };
            app.Run(async http =>
            {
                using var reader = new StreamReader(http.Request.Body);
                listener.RequestQueue.Enqueue((http.Request.Method, http.Request.Path.Value ?? "", http.Request.Headers[InternalRealtimeOptions.SecretHeaderName].ToString(), await reader.ReadToEndAsync()));
                if (listener.Delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(listener.Delay, http.RequestAborted); } catch (OperationCanceledException) { return; }
                }
                http.Response.StatusCode = (int)status;
            });
            await app.StartAsync();
            return listener;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
