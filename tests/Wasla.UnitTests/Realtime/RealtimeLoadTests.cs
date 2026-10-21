using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-25: the live hub under bounded load, against the real Web pipeline (<see cref="TenantSessionWebHost"/>: real tenant
/// resolution, tenant cookie with its per-request check, role policies, hub, notifier and loopback listener) with many
/// lightweight .NET SignalR clients over WebSockets, each one a "tab" of a signed-in user. Every test has explicit upper
/// bounds and timeouts; it waits for conditions, never for a fixed time, except where a delay is the scenario. Sizes
/// come from <see cref="RealtimeLoad"/> (defaults for every pull request; a heavier run sets
/// <c>WASLA_REALTIME_LOAD_*</c>, docs/operations/testing.md#realtime-load-tests). Diagnostics (sizes, durations,
/// send-time percentiles, memory and threads of the test process, which hosts both the server and the clients) go to
/// the test output.
/// </summary>
public sealed class RealtimeLoadTests
{
    private const string Secret = "test-only-load-secret-0123456789abcdefghij";
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(60);
    private static readonly UserRole[] Roles = [UserRole.Owner, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact]
    public async Task ManyTabsAcrossTenantsAndRoles_EachReceiveExactlyTheirOwnTenantsStreams_UnderConcurrentBursts()
    {
        var internalPort = InternalTenantChangeChannelTests.FreeLoopbackPort();
        var channel = RealtimeMetricsTests.Channel(internalPort, Secret);
        await using var fleet = await Fleet.StartAsync(RealtimeLoad.Tenants, RealtimeLoad.Connections, channel);
        var (worker, workerLogs) = RealtimeMetricsTests.Sender(channel);
        await using var _ = worker;
        using var workerMetrics = new RealtimeMetricsRecorder(worker);
        var hub = fleet.Host.Services.GetRequiredService<ITenantChangeNotifier>();
        var internalChannel = worker.GetRequiredService<ITenantChangeNotifier>();

        // Every tenant at once; within a tenant four senders at a time; half the events come from Web itself, half from
        // the "Worker" over the loopback channel.
        var events = RealtimeLoad.EventsPerTenant;
        var watch = Stopwatch.StartNew();
        var sent = await fleet.BurstAsync(events, i => i % 2 == 0 ? hub : internalChannel);
        var burstTime = watch.Elapsed;
        watch.Restart();
        await fleet.AssertEachTabReceivedExactlyAsync(sent);
        var deliveryTime = watch.Elapsed;

        var total = sent.Values.Sum(e => e.Count);
        var web = fleet.Metrics;
        Assert.Equal(0, web.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "timeout")) + web.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "failed")));
        Assert.Equal(0, workerMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "paused")));
        Assert.Equal(total / 2, workerMetrics.Sum("wasla.realtime.notifications", ("wasla.realtime.channel", "internal"), ("wasla.realtime.result", "sent")));
        Assert.Equal(fleet.Tabs.Count, web.ActiveConnections);
        RealtimeMetricsTests.AssertBoundedTags(web, [.. fleet.Tenants.Select(t => t.Id.ToString("N")), Secret]);
        RealtimeMetricsTests.AssertBoundedTags(workerMetrics, Secret);
        Assert.DoesNotContain(fleet.Host.Logs.Entries.Concat(workerLogs.Entries), l => l.Contains(Secret, StringComparison.Ordinal));

        Report($"tenants={fleet.Tenants.Count} tabs={fleet.Tabs.Count} events/tenant={events} total={total} " +
               $"connect={fleet.ConnectTime.TotalMilliseconds:F0}ms burst={burstTime.TotalMilliseconds:F0}ms delivery-after-burst={deliveryTime.TotalMilliseconds:F0}ms");
        Report("hub send " + Percentiles(web, "hub") + "; internal send " + Percentiles(workerMetrics, "internal"));
        Report(Resources.Describe());
    }

    [Fact]
    public async Task ReconnectStorms_ReadmitEveryTab_WithoutDuplicateMembershipOrLeftoverConnections()
    {
        await using var fleet = await Fleet.StartAsync(RealtimeLoad.Tenants, RealtimeLoad.Connections);
        var hub = fleet.Host.Services.GetRequiredService<ITenantChangeNotifier>();
        var before = Resources.Snapshot();
        const int cycles = 3;

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            // Every tab drops and comes back at the same moment, as after a Web restart or a network blip at the venue.
            var watch = Stopwatch.StartNew();
            await fleet.ReconnectAllAsync();
            var stormTime = watch.Elapsed;

            Assert.True(await fleet.Metrics.WaitForAsync(m => m.ActiveConnections == fleet.Tabs.Count, Delivery, Ct), fleet.Metrics.Describe());
            Assert.True(await fleet.Metrics.WaitForAsync(m => m.Sum("signalr.server.active_connections") == fleet.Tabs.Count, Delivery, Ct),
                $"transport connections: {fleet.Metrics.Sum("signalr.server.active_connections")}");
            Assert.Equal(cycle * fleet.Tabs.Count, fleet.Metrics.Sum("wasla.realtime.connection.attempts", ("wasla.realtime.attempt", "reconnect")));

            // Each tab is in its groups exactly once: one notification each, delivered once.
            var sent = await fleet.BurstAsync(4, _ => hub);
            await fleet.AssertEachTabReceivedExactlyAsync(sent);
            Report($"storm {cycle}: {fleet.Tabs.Count} tabs reconnected in {stormTime.TotalMilliseconds:F0}ms; {Resources.Describe()}");
        }

        Assert.Equal(cycles * fleet.Tabs.Count, fleet.Metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "closed")));
        // Bounded: after three storms the server holds the same number of connections, and the test process has not
        // grown without bound (a generous ceiling; the figures are in the output).
        var after = Resources.Snapshot();
        Assert.InRange(after.Threads - before.Threads, -1000, 200);
        Report($"threads {before.Threads}->{after.Threads}, managed {before.ManagedMb:F0}->{after.ManagedMb:F0} MB, working set {before.WorkingSetMb:F0}->{after.WorkingSetMb:F0} MB");
    }

    [Fact]
    public async Task RevokingAndDemotingUsersDuringAReconnectStorm_KeepsThemOut_WithinTheSessionCheckInterval()
    {
        var connections = Math.Min(RealtimeLoad.Connections, 80);
        await using var fleet = await Fleet.StartAsync(Math.Max(2, Math.Min(RealtimeLoad.Tenants, 3)), connections, new Dictionary<string, string?>
        {
            ["Realtime:SessionRevalidationSeconds"] = "1"
        });
        var hub = fleet.Host.Services.GetRequiredService<ITenantChangeNotifier>();
        var deactivated = fleet.UserOf(fleet.Tenants[0].Id, UserRole.Cashier);
        var demoted = fleet.UserOf(fleet.Tenants[1].Id, UserRole.Owner);
        bool IsVictim(Tab tab) => tab.User == deactivated || tab.User == demoted;

        // Every tab reconnects while, at the same time, one user is deactivated and another demoted to Kitchen.
        var storm = fleet.ReconnectAllAsync(tolerateRefusals: true);
        fleet.Host.UpdateUser(deactivated.TenantId, deactivated.UserId, u => u.IsActive = false);
        fleet.Host.UpdateUser(demoted.TenantId, demoted.UserId, u => u.Role = UserRole.Kitchen);
        var revokedAt = Stopwatch.StartNew();
        var refused = await storm;

        // Whatever got through before the change is closed by the session check within its interval (1 s here; 60 s by
        // default), plus scheduling slack.
        var bound = TimeSpan.FromSeconds(1) * 3 + TimeSpan.FromSeconds(5);
        foreach (var tab in fleet.Tabs.Where(IsVictim))
        {
            if (tab.Client is null)
                continue;
            var remaining = bound - revokedAt.Elapsed;
            Assert.True(remaining > TimeSpan.Zero && await Task.WhenAny(tab.Client.Closed, Task.Delay(remaining, Ct)) == tab.Client.Closed,
                $"A revoked tab stayed open longer than {bound.TotalSeconds:F0}s. {fleet.Metrics.Describe()}");
        }

        var victims = fleet.Tabs.Count(IsVictim);
        Assert.True(victims >= 2);
        Assert.All(fleet.Tabs.Where(t => !IsVictim(t)), t => Assert.False(t.Client!.Closed.IsCompleted, "A valid tab was closed."));

        // A revoked user cannot connect again; everyone else keeps receiving exactly their own tenant's notifications.
        await Assert.ThrowsAnyAsync<Exception>(() => LiveHubClient.ConnectAsync(fleet.Host.BaseAddress, deactivated.Session, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => LiveHubClient.ConnectAsync(fleet.Host.BaseAddress, demoted.Session, Ct));
        var sent = await fleet.BurstAsync(6, _ => hub);
        await fleet.AssertEachTabReceivedExactlyAsync(sent, skip: IsVictim);
        foreach (var tab in fleet.Tabs.Where(t => IsVictim(t) && t.Client is not null))
            Assert.DoesNotContain(tab.Client!.Drain(), m => sent.Values.SelectMany(e => e).Any(e => e.OrderId == m.GetProperty("orderId").GetGuid()));

        Assert.True(await fleet.Metrics.WaitForAsync(m => m.ActiveConnections == fleet.Tabs.Count(t => !IsVictim(t)), Delivery, Ct), fleet.Metrics.Describe());
        Report($"victim tabs={victims}, refused during the storm={refused}, closed by the check=" +
               $"{fleet.Metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "session_invalid"))}");
    }

    [Fact]
    public async Task AClientThatStopsReading_CostsEachSendAtMostTheSendBound_AndIsClosedByTheClientTimeout()
    {
        // The real defaults are a 15 s keep-alive and a 30 s client timeout; shortened so the test ends quickly.
        var clientTimeout = TimeSpan.FromSeconds(8);
        await using var host = await TenantSessionWebHost.StartAsync(configureServices: services =>
            services.Configure<HubOptions>(o =>
            {
                o.ClientTimeoutInterval = clientTimeout;
                o.KeepAliveInterval = TimeSpan.FromSeconds(2);
            }));
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        using var kitchen = await RealtimeMetricsTests.SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Kitchen);
        await using var healthy = await LiveHubClient.ConnectAsync(host.BaseAddress, kitchen, Ct, keepAlive: TimeSpan.FromSeconds(2));
        using var stuck = await StuckClient.ConnectAsync(host, kitchen);
        Assert.Equal(2, metrics.ActiveConnections);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();
        var memoryBefore = Resources.Snapshot();

        // Flood the tenant's Live Screen group until the stuck client is closed (or a bounded number or time runs out).
        var slowest = TimeSpan.Zero;
        var sent = 0;
        var sendTimes = new List<double>();
        var deadline = Stopwatch.StartNew();
        var payloadOrders = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        while (deadline.Elapsed < clientTimeout + TimeSpan.FromSeconds(12) && sent < 200_000 && metrics.ActiveConnections > 1)
        {
            var one = Stopwatch.StartNew();
            await notifier.NotifyAsync(host.AlphaId, new OrderChanged(payloadOrders[sent % payloadOrders.Length], sent + 1), Ct);
            sendTimes.Add(one.Elapsed.TotalMilliseconds);
            if (one.Elapsed > slowest)
                slowest = one.Elapsed;
            sent++;
        }

        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 1, TimeSpan.FromSeconds(15), Ct),
            $"The stuck client was not closed. {metrics.Describe()}");
        // Each send waited at most the notifier's 2-second bound (plus scheduling slack), never longer.
        Assert.InRange(slowest, TimeSpan.Zero, SignalRTenantChangeNotifier.SendTimeout + TimeSpan.FromSeconds(1.5));
        // SignalR reports a client timeout to the hub without an exception, so it is counted as "closed".
        Assert.True(1 == metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "closed")),
            $"sent={sent} slowest={slowest.TotalMilliseconds:F0}ms elapsed={deadline.Elapsed.TotalSeconds:F1}s {metrics.Describe()}");

        // The healthy tab received every notification of the flood, in order, and keeps receiving.
        var sentinel = new OrderChanged(Guid.NewGuid(), 1);
        await notifier.NotifyAsync(host.AlphaId, sentinel, Ct);
        var received = 0;
        while (true)
        {
            var message = await healthy.NextAsync(Delivery, Ct);
            if (message.GetProperty("orderId").GetGuid() == sentinel.OrderId)
                break;
            received++;
        }

        Assert.Equal(sent, received);
        // After the close, sends are fast again.
        var after = Stopwatch.StartNew();
        await notifier.NotifyAsync(host.AlphaId, new OrderChanged(Guid.NewGuid(), 2), Ct);
        Assert.InRange(after.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        sendTimes.Sort();
        var timeouts = metrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "timeout"));
        Report($"flood: {sent} sends until the stuck client closed after {deadline.Elapsed.TotalSeconds:F1}s; " +
               $"p50={sendTimes[sendTimes.Count / 2]:F2}ms p99={sendTimes[(int)(sendTimes.Count * 0.99)]:F2}ms max={slowest.TotalMilliseconds:F0}ms; " +
               $"sends that hit the 2 s bound={timeouts}; memory {memoryBefore.ManagedMb:F0}->{Resources.Snapshot().ManagedMb:F0} MB managed");
    }

    [Fact]
    public async Task TabsClosedDuringABurst_LeaveNoConnections_AndTheBurstAndTheOtherTabsAreUnaffected()
    {
        await using var fleet = await Fleet.StartAsync(RealtimeLoad.Tenants, RealtimeLoad.Connections);
        var hub = fleet.Host.Services.GetRequiredService<ITenantChangeNotifier>();
        var closing = fleet.Tabs.Where((_, i) => i % 2 == 0).ToList();

        var burst = fleet.BurstAsync(RealtimeLoad.EventsPerTenant, _ => hub);
        // Close half of the tabs while the burst is being delivered.
        await Parallel.ForEachAsync(closing, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (tab, _) =>
        {
            await tab.Client!.DisposeAsync();
            tab.Client = null;
        });
        var sent = await burst;

        await fleet.AssertEachTabReceivedExactlyAsync(sent, skip: t => t.Client is null);
        Assert.True(await fleet.Metrics.WaitForAsync(m => m.ActiveConnections == fleet.Tabs.Count - closing.Count, Delivery, Ct), fleet.Metrics.Describe());
        Assert.True(await fleet.Metrics.WaitForAsync(m => m.Sum("signalr.server.active_connections") == fleet.Tabs.Count - closing.Count, Delivery, Ct));
        Assert.Equal(0, fleet.Metrics.Sum("wasla.realtime.notifications", ("wasla.realtime.result", "failed")));
        Assert.Equal(closing.Count, fleet.Metrics.Sum("wasla.realtime.connection.closures"));
        Report($"closed {closing.Count} of {fleet.Tabs.Count} tabs during a burst of {sent.Values.Sum(e => e.Count)}; hub send {Percentiles(fleet.Metrics, "hub")}");
    }

    private static string Percentiles(RealtimeMetricsRecorder recorder, string channel)
    {
        var values = recorder.Measurements
            .Where(m => m.Instrument == "wasla.realtime.notification.duration" && m.Tags.GetValueOrDefault("wasla.realtime.channel") == channel)
            .Select(m => m.Value * 1000)
            .Order()
            .ToList();
        if (values.Count == 0)
            return "n=0";
        return string.Create(CultureInfo.InvariantCulture,
            $"n={values.Count} p50={values[values.Count / 2]:F2}ms p95={values[(int)(values.Count * 0.95)]:F2}ms max={values[^1]:F2}ms");
    }

    /// <summary>One notification of a burst, as the tab that receives it must see it.</summary>
    internal sealed record Sent(string Type, Guid OrderId, Guid? PrintJobId, long Version);

    internal sealed record User(Guid TenantId, Guid UserId, UserRole Role, TenantSessionClient Session);

    internal sealed class Tab(User user)
    {
        public User User { get; } = user;
        public LiveHubClient? Client { get; set; }
        public bool ReceivesPrintStatus => WaslaTenantPolicies.AllowedRoles[WaslaTenantPolicies.CanManualPrint].Contains(User.Role);
    }

    /// <summary>Tenants, one signed-in user per role and tenant, and many tabs spread over them.</summary>
    internal sealed class Fleet : IAsyncDisposable
    {
        private const int ConnectParallelism = 32;

        private Fleet(TenantSessionWebHost host, RealtimeMetricsRecorder metrics)
        {
            Host = host;
            Metrics = metrics;
        }

        public TenantSessionWebHost Host { get; }
        public RealtimeMetricsRecorder Metrics { get; }
        public List<(Guid Id, string HostName)> Tenants { get; } = [];
        public List<User> Users { get; } = [];
        public List<Tab> Tabs { get; } = [];
        public TimeSpan ConnectTime { get; private set; }

        public User UserOf(Guid tenantId, UserRole role) => Users.Single(u => u.TenantId == tenantId && u.Role == role);

        public static async Task<Fleet> StartAsync(int tenants, int connections, IReadOnlyDictionary<string, string?>? configuration = null)
        {
            var host = await TenantSessionWebHost.StartAsync(configuration: configuration);
            var fleet = new Fleet(host, new RealtimeMetricsRecorder(host.Services));
            try
            {
                fleet.Tenants.Add((host.AlphaId, TenantSessionWebHost.AlphaHost));
                fleet.Tenants.Add((host.BetaId, TenantSessionWebHost.BetaHost));
                for (var i = 2; i < tenants; i++)
                {
                    var slug = $"load{i}";
                    var tenant = host.Central.AddTenant(slug);
                    host.Tenants.CreateTenantDatabase(tenant.Id);
                    fleet.Tenants.Add((tenant.Id, $"{slug}.app.wasla.local"));
                }

                foreach (var (id, hostName) in fleet.Tenants)
                {
                    foreach (var role in Roles)
                    {
                        var seeded = host.SeedUser(id, $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@load.test", role);
                        var session = host.Client(hostName);
                        await session.LoginAsync(seeded.Email);
                        fleet.Users.Add(new User(id, seeded.Id, role, session));
                    }
                }

                for (var i = 0; i < Math.Max(connections, fleet.Users.Count); i++)
                    fleet.Tabs.Add(new Tab(fleet.Users[i % fleet.Users.Count]));

                var watch = Stopwatch.StartNew();
                await Parallel.ForEachAsync(fleet.Tabs, new ParallelOptions { MaxDegreeOfParallelism = ConnectParallelism, CancellationToken = Ct }, async (tab, ct) =>
                    tab.Client = await LiveHubClient.ConnectAsync(host.BaseAddress, tab.User.Session, ct, attempt: "initial"));
                fleet.ConnectTime = watch.Elapsed;
                Assert.Equal(fleet.Tabs.Count, fleet.Metrics.ActiveConnections);
                return fleet;
            }
            catch
            {
                await fleet.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Drops every tab and connects it again, all at once. With <paramref name="tolerateRefusals"/> a refused tab
        /// stays without a client; returns how many were refused.
        /// </summary>
        public async Task<int> ReconnectAllAsync(bool tolerateRefusals = false)
        {
            var refused = 0;
            await Parallel.ForEachAsync(Tabs, new ParallelOptions { MaxDegreeOfParallelism = ConnectParallelism * 2, CancellationToken = Ct }, async (tab, ct) =>
            {
                if (tab.Client is { } old)
                    await old.DisposeAsync();
                tab.Client = null;
                try
                {
                    tab.Client = await LiveHubClient.ConnectAsync(Host.BaseAddress, tab.User.Session, ct, attempt: "reconnect");
                }
                catch (Exception) when (tolerateRefusals)
                {
                    Interlocked.Increment(ref refused);
                }
            });
            return refused;
        }

        /// <summary>
        /// Sends <paramref name="eventsPerTenant"/> notifications to every tenant at once (half order changes, half print
        /// job changes, four at a time per tenant), each through the notifier <paramref name="via"/> picks for its index.
        /// </summary>
        public async Task<Dictionary<Guid, List<Sent>>> BurstAsync(int eventsPerTenant, Func<int, ITenantChangeNotifier> via)
        {
            var sent = Tenants.ToDictionary(t => t.Id, _ => new List<Sent>());
            foreach (var (tenantId, _) in Tenants)
            {
                for (var i = 0; i < eventsPerTenant; i++)
                {
                    sent[tenantId].Add(i % 2 == 0
                        ? new Sent(OrderChanged.TypeName, Guid.NewGuid(), null, i + 1)
                        : new Sent(PrintJobChanged.TypeName, Guid.NewGuid(), Guid.NewGuid(), i + 1));
                }
            }

            await Task.WhenAll(Tenants.Select(t => Parallel.ForEachAsync(
                sent[t.Id].Select((e, i) => (e, i)),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Ct },
                async (item, ct) =>
                {
                    TenantChange change = item.e.Type == OrderChanged.TypeName
                        ? new OrderChanged(item.e.OrderId, item.e.Version)
                        : new PrintJobChanged(item.e.OrderId, item.e.PrintJobId!.Value, PrintJobStatus.Printing, item.e.Version);
                    await via(item.i).NotifyAsync(t.Id, change, ct);
                })));
            return sent;
        }

        /// <summary>
        /// Sends one closing order change per tenant and reads every tab up to it. Delivery is in order per connection,
        /// so everything a tab will ever receive from the burst is in front of it: each tab must have received every
        /// notification of its own tenant's streams exactly once, and nothing else.
        /// </summary>
        public async Task AssertEachTabReceivedExactlyAsync(Dictionary<Guid, List<Sent>> sent, Func<Tab, bool>? skip = null)
        {
            var hub = Host.Services.GetRequiredService<ITenantChangeNotifier>();
            var sentinels = Tenants.ToDictionary(t => t.Id, _ => Guid.NewGuid());
            foreach (var (tenantId, _) in Tenants)
                await hub.NotifyAsync(tenantId, new OrderChanged(sentinels[tenantId], 1), Ct);

            var failures = new ConcurrentQueue<string>();
            await Parallel.ForEachAsync(Tabs.Where(t => skip is null || !skip(t)), new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = Ct }, async (tab, ct) =>
            {
                var received = new List<JsonElement>();
                var sentinel = sentinels[tab.User.TenantId];
                try
                {
                    while (true)
                    {
                        var message = await tab.Client!.NextAsync(Delivery, ct);
                        if (message.GetProperty("orderId").GetGuid() == sentinel)
                            break;
                        received.Add(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    failures.Enqueue($"{tab.User.Role} tab of a tenant got no closing message within {Delivery.TotalSeconds:F0}s after {received.Count} messages.");
                    return;
                }

                var expected = sent[tab.User.TenantId]
                    .Where(e => e.Type == OrderChanged.TypeName || tab.ReceivesPrintStatus)
                    .Select(e => (e.Type, e.OrderId, e.PrintJobId, e.Version))
                    .OrderBy(e => e.OrderId)
                    .ToList();
                var actual = received
                    .Select(m => (
                        m.GetProperty("type").GetString()!,
                        m.GetProperty("orderId").GetGuid(),
                        m.TryGetProperty("printJobId", out var job) ? job.GetGuid() : (Guid?)null,
                        m.GetProperty("version").GetInt64()))
                    .OrderBy(e => e.Item2)
                    .ToList();
                if (!expected.SequenceEqual(actual))
                {
                    var foreign = actual.Count(a => !sent[tab.User.TenantId].Any(e => e.OrderId == a.Item2));
                    failures.Enqueue($"{tab.User.Role} tab: expected {expected.Count}, received {actual.Count}, from other tenants or streams {foreign}.");
                }
            });

            Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)) + Environment.NewLine + Metrics.Describe());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var tab in Tabs)
            {
                if (tab.Client is { } client)
                    await client.DisposeAsync();
            }

            foreach (var user in Users)
                user.Session.Dispose();
            Metrics.Dispose();
            await Host.DisposeAsync();
        }
    }

    /// <summary>
    /// A hub client that completes the handshake, waits for the ready message and then never reads again or sends
    /// anything, like a frozen tablet whose network stopped: the server's writes pile up until its client timeout closes
    /// the connection.
    /// </summary>
    private sealed class StuckClient : IDisposable
    {
        private readonly ClientWebSocket _socket;

        private StuckClient(ClientWebSocket socket) => _socket = socket;

        public static async Task<StuckClient> ConnectAsync(TenantSessionWebHost host, TenantSessionClient session)
        {
            var negotiate = await session.SendAsync(HttpMethod.Post, $"{TenantEventsHub.Path}/negotiate?negotiateVersion=1", new StringContent(""));
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
            var token = (await negotiate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("connectionToken").GetString();

            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Host", session.Host);
            socket.Options.SetRequestHeader("Cookie", string.Join("; ", session.Cookies.Select(c => $"{c.Key}={c.Value}")));
            var url = new UriBuilder(host.BaseAddress) { Scheme = "ws", Path = TenantEventsHub.Path, Query = $"id={token}" }.Uri;
            await socket.ConnectAsync(url, Ct);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, Ct);

            // Read until the hub says the groups are joined, then stop reading for good.
            var buffer = new byte[4096];
            var text = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(LiveHubClient.ReadyTimeout);
            while (!text.ToString().Contains(TenantEventsHub.ReadyMethod, StringComparison.Ordinal))
            {
                var read = await socket.ReceiveAsync(buffer, timeout.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            }

            return new StuckClient(socket);
        }

        public void Dispose()
        {
            _socket.Abort();
            _socket.Dispose();
        }
    }

    /// <summary>Resource figures of the test process (server and clients together).</summary>
    private static class Resources
    {
        public sealed record Figures(int Threads, double ManagedMb, double WorkingSetMb, int Handles);

        public static Figures Snapshot()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new Figures(process.Threads.Count, GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, process.WorkingSet64 / 1048576.0, process.HandleCount);
        }

        public static string Describe()
        {
            var f = Snapshot();
            return string.Create(CultureInfo.InvariantCulture,
                $"process threads={f.Threads} handles={f.Handles} managed={f.ManagedMb:F0}MB working-set={f.WorkingSetMb:F0}MB thread-pool={ThreadPool.ThreadCount}");
        }
    }
}
