using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-23 against the real Web pipeline (<see cref="TenantSessionWebHost"/>: real tenant resolution for
/// <c>alpha.wasla.local</c> and <c>beta.wasla.local</c>, the real tenant cookie with its per-request session check, the
/// tenant policies, <c>AddWaslaRealtime</c>/<c>MapWaslaRealtime</c> as in Program.cs) and a real SignalR client. A
/// tenant's connection receives its own tenant's print notifications and nothing else; no client can choose a group;
/// cross-origin hub requests are refused; a deactivated or demoted user's open connection is closed; notifications
/// from Wasla.Worker and Wasla.Api arrive through the loopback listener only with the secret.
/// </summary>
public sealed class TenantEventsHubTests
{
    private const string Secret = "test-only-secret-0123456789abcdefghijklmn";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachTenantsConnection_ReceivesOnlyItsOwnTenantsNotifications()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Cashier);
        using var beta = await SignInAsync(host, TenantSessionWebHost.BetaHost, host.BetaId, UserRole.Owner);
        await using var alphaLive = await ConnectAsync(host, alpha);
        await using var betaLive = await ConnectAsync(host, beta);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();
        Assert.IsType<SignalRTenantChangeNotifier>(notifier);

        var forBeta = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printing, 2);
        var forAlpha = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);
        await notifier.NotifyAsync(host.BetaId, forBeta, Ct);
        await notifier.NotifyAsync(host.AlphaId, forAlpha, Ct);

        // Delivery is in order per connection: alpha's first message being its own proves beta's never came.
        AssertMessage(forAlpha, await alphaLive.NextAsync());
        AssertMessage(forBeta, await betaLive.NextAsync());
        await notifier.NotifyAsync(host.BetaId, forBeta with { Version = 3, Status = PrintJobStatus.Printed }, Ct);
        await notifier.NotifyAsync(host.AlphaId, forAlpha with { Version = 2, Status = PrintJobStatus.Printing }, Ct);
        AssertMessage(forAlpha with { Version = 2, Status = PrintJobStatus.Printing }, await alphaLive.NextAsync());
    }

    [Fact]
    public async Task TheMessage_CarriesOnlyTheTypeIdentifiersStatusAndVersion()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        await using var live = await ConnectAsync(host, alpha);

        await host.Services.GetRequiredService<ITenantChangeNotifier>()
            .NotifyAsync(host.AlphaId, new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Failed, 4), Ct);

        var message = await live.NextAsync();
        Assert.Equal(["orderId", "printJobId", "status", "type", "version"], message.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("PrintJobChanged", message.GetProperty("type").GetString());
        Assert.Equal("Failed", message.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Viewer)]
    public async Task LiveScreenOnlyRoles_ReceiveOrderChanges_ButNeverPrintStatus(UserRole role)
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var client = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, role);
        await using var live = await ConnectAsync(host, client);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();

        // Print status (CanManualPrint) is not this role's stream; the order change that follows arrives first.
        await notifier.NotifyAsync(host.AlphaId, new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1), Ct);
        var order = new OrderChanged(Guid.NewGuid(), 1_760_000_000_000);
        await notifier.NotifyAsync(host.AlphaId, order, Ct);

        AssertOrderMessage(order, await live.NextAsync());
    }

    [Fact]
    public async Task ARoleWithBothStreams_ReceivesBoth_EachInItsOwnShape()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var cashier = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Cashier);
        await using var live = await ConnectAsync(host, cashier);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();
        var print = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printing, 2);
        var order = new OrderChanged(Guid.NewGuid(), 1_760_000_000_001);

        await notifier.NotifyAsync(host.AlphaId, print, Ct);
        await notifier.NotifyAsync(host.AlphaId, order, Ct);

        AssertMessage(print, await live.NextAsync());
        var message = await live.NextAsync();
        AssertOrderMessage(order, message);
        // An order change carries no status, customer, items or amounts.
        Assert.Equal(["orderId", "type", "version"], message.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OrderChanges_StayInTheirTenant()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Kitchen);
        using var beta = await SignInAsync(host, TenantSessionWebHost.BetaHost, host.BetaId, UserRole.Kitchen);
        await using var alphaLive = await ConnectAsync(host, alpha);
        await using var betaLive = await ConnectAsync(host, beta);
        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();
        var forAlpha = new OrderChanged(Guid.NewGuid(), 10);
        var forBeta = new OrderChanged(Guid.NewGuid(), 20);

        await notifier.NotifyAsync(host.BetaId, forBeta, Ct);
        await notifier.NotifyAsync(host.AlphaId, forAlpha, Ct);

        // In-order delivery per connection: each one's first message is its own tenant's.
        AssertOrderMessage(forAlpha, await alphaLive.NextAsync());
        AssertOrderMessage(forBeta, await betaLive.NextAsync());
    }

    [Fact]
    public async Task AnOrderIngestedByTheWorker_ReachesTheTenantsLiveScreens_ThroughTheLoopbackListener()
    {
        var internalPort = InternalTenantChangeChannelTests.FreeLoopbackPort();
        var channel = new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = internalPort.ToString(CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = Secret
        };
        await using var host = await TenantSessionWebHost.StartAsync(configuration: channel);
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Viewer);
        using var beta = await SignInAsync(host, TenantSessionWebHost.BetaHost, host.BetaId, UserRole.Viewer);
        await using var alphaLive = await ConnectAsync(host, alpha);
        await using var betaLive = await ConnectAsync(host, beta);

        // The Worker's composition: the real sync service with the internal channel notifier, over the tenant database.
        var workerServices = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddWaslaWebTenantChangeNotifier(new ConfigurationBuilder().AddInMemoryCollection(channel).Build())
            .BuildServiceProvider();
        OrderChangeFixtures.SeedSyncableTenant(host.Tenants, host.AlphaId);
        var provider = new ScriptedPlatformClient(FoodPlatform.TrendyolYemek);
        provider.Returns(OrderChangeFixtures.External("WORKER-1"));
        await OrderChangeFixtures.Sync(host.Tenants, provider, workerServices.GetRequiredService<ITenantChangeNotifier>())
            .SyncCustomerAsync(host.AlphaId, Ct);

        var order = await OrderChangeFixtures.ReadOrderAsync(host.Tenants, host.AlphaId, "WORKER-1");
        AssertOrderMessage(OrderChanged.For(order.Id, order.UpdatedAt), await alphaLive.NextAsync());
        var forBeta = new OrderChanged(Guid.NewGuid(), 5);
        await host.Services.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(host.BetaId, forBeta, Ct);
        AssertOrderMessage(forBeta, await betaLive.NextAsync());
    }

    [Fact]
    public async Task ASessionPresentedOnAnotherTenantsHost_CannotConnect_AndAnonymousCannotEither()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        using var replayed = alpha.ReplayTo(host.BaseAddress, TenantSessionWebHost.BetaHost);
        using var anonymous = host.Client(TenantSessionWebHost.AlphaHost);

        Assert.NotEqual(HttpStatusCode.OK, (await NegotiateAsync(replayed)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await NegotiateAsync(anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(alpha)).StatusCode);
    }

    [Fact]
    public async Task AClientCannotChooseATenantOrGroup()
    {
        // The hub declares nothing a client can call: only the server-side connect handler.
        var callable = typeof(TenantEventsHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);
        Assert.Equal([nameof(Hub.OnConnectedAsync)], callable);

        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        await using var live = await ConnectAsync(host, alpha);

        var betaGroup = TenantEventGroups.PrintStatus(host.BetaId);
        foreach (var method in new[] { "AddToGroupAsync", "JoinGroup", "Subscribe", "OnConnectedAsync" })
            await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync(method, betaGroup, Ct));

        var notifier = host.Services.GetRequiredService<ITenantChangeNotifier>();
        var forBeta = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);
        var forAlpha = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);
        await notifier.NotifyAsync(host.BetaId, forBeta, Ct);
        await notifier.NotifyAsync(host.AlphaId, forAlpha, Ct);
        AssertMessage(forAlpha, await live.NextAsync());
    }

    [Fact]
    public async Task HubRequestsFromAnotherOrigin_AreRefused_SameOriginIsAdmitted()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        var port = host.BaseAddress.Port.ToString(CultureInfo.InvariantCulture);

        // Another tenant's subdomain, another site, an opaque origin, and an origin with a path are all refused.
        foreach (var foreign in new[] { $"http://{TenantSessionWebHost.BetaHost}:{port}", "https://evil.example", "null", $"http://{TenantSessionWebHost.AlphaHost}/x" })
            Assert.Equal(HttpStatusCode.Forbidden, (await NegotiateAsync(alpha, foreign)).StatusCode);

        // The page's own host, with any scheme (TLS may end at the proxy).
        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(alpha, $"http://{TenantSessionWebHost.AlphaHost}:{port}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(alpha, $"https://{TenantSessionWebHost.AlphaHost}")).StatusCode);

        // Behind the proxy the Host header has no port; locally it has one, and then the port must match.
        static HostString Host(string value) => new(value);
        Assert.True(RealtimeOriginMiddleware.IsSameOrigin("https://alpha.app.wasla.tr", Host("alpha.app.wasla.tr")));
        Assert.True(RealtimeOriginMiddleware.IsSameOrigin("https://ALPHA.app.wasla.tr", Host("alpha.app.wasla.tr")));
        Assert.False(RealtimeOriginMiddleware.IsSameOrigin("https://beta.app.wasla.tr", Host("alpha.app.wasla.tr")));
        Assert.False(RealtimeOriginMiddleware.IsSameOrigin("https://alpha.app.wasla.tr.evil.example", Host("alpha.app.wasla.tr")));
        Assert.False(RealtimeOriginMiddleware.IsSameOrigin("https://user@alpha.app.wasla.tr", Host("alpha.app.wasla.tr")));
        Assert.True(RealtimeOriginMiddleware.IsSameOrigin("http://alpha.wasla.local:5200", Host("alpha.wasla.local:5200")));
        Assert.False(RealtimeOriginMiddleware.IsSameOrigin("http://alpha.wasla.local:5201", Host("alpha.wasla.local:5200")));
        Assert.False(RealtimeOriginMiddleware.IsSameOrigin("ws://alpha.wasla.local:5200", Host("alpha.wasla.local:5200")));
    }

    [Theory]
    [InlineData("deactivated", UserRole.Manager)]
    [InlineData("demoted", UserRole.Manager)]
    [InlineData("password reset", UserRole.Manager)]
    [InlineData("deactivated", UserRole.Kitchen)]
    [InlineData("demoted", UserRole.Kitchen)]
    public async Task AnOpenConnection_IsClosed_WhenTheSessionIsNoLongerValid(string change, UserRole role)
    {
        await using var host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Realtime:SessionRevalidationSeconds"] = "1"
        });
        var user = host.SeedUser(host.AlphaId, $"{Guid.NewGuid():N}@alpha.test", role);
        using var client = host.Client(TenantSessionWebHost.AlphaHost);
        await client.LoginAsync(user.Email);
        await using var live = await ConnectAsync(host, client);

        // Still valid after a few checks.
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.False(live.Closed.IsCompleted);

        host.UpdateUser(host.AlphaId, user.Id, u =>
        {
            switch (change)
            {
                case "deactivated": u.IsActive = false; break;
                case "demoted": u.Role = role == UserRole.Kitchen ? UserRole.Viewer : UserRole.Kitchen; break;
                default: u.SecurityStamp = Guid.NewGuid(); break;
            }
        });

        var closed = await Task.WhenAny(live.Closed, Task.Delay(Wait, Ct));
        Assert.Same(live.Closed, closed);
        Assert.NotEqual(HttpStatusCode.OK, (await NegotiateAsync(client)).StatusCode);
    }

    [Fact]
    public async Task AnOpenConnection_IsClosed_WhenItsTenantIsDeactivated()
    {
        await using var host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Realtime:SessionRevalidationSeconds"] = "1"
        });
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        await using var live = await ConnectAsync(host, alpha);

        using (var central = host.Central.CreateContext())
        {
            central.Tenants.Single(t => t.Id == host.AlphaId).IsActive = false;
            central.SaveChanges();
        }
        // The same 5-minute host cache HTTP requests use; expire it as time would.
        host.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Remove($"tenant:{TenantSessionWebHost.AlphaHost}");

        var closed = await Task.WhenAny(live.Closed, Task.Delay(Wait, Ct));
        Assert.Same(live.Closed, closed);
    }

    [Fact]
    public async Task AReceiptJobCreatedByTheWorker_ReachesTheTenantsConnection_ThroughTheLoopbackListener()
    {
        var internalPort = InternalTenantChangeChannelTests.FreeLoopbackPort();
        var channel = new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = internalPort.ToString(CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = Secret
        };
        await using var host = await TenantSessionWebHost.StartAsync(configuration: channel);
        var listener = host.Services.GetRequiredService<InternalTenantChangeListener>();
        Assert.Equal(IPAddress.Loopback.ToString(), listener.Address!.Host);
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Cashier);
        using var beta = await SignInAsync(host, TenantSessionWebHost.BetaHost, host.BetaId, UserRole.Owner);
        await using var alphaLive = await ConnectAsync(host, alpha);
        await using var betaLive = await ConnectAsync(host, beta);

        // What Wasla.Worker registers (Program.cs: AddWaslaWebTenantChangeNotifier), over the same tenant database.
        var workerServices = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddWaslaWebTenantChangeNotifier(new ConfigurationBuilder().AddInMemoryCollection(channel).Build())
            .BuildServiceProvider();
        var workerReceipts = new ReceiptPrintJobService(
            host.Tenants, new DefaultReceiptTemplates(), workerServices.GetRequiredService<ITenantChangeNotifier>(), NullLogger<ReceiptPrintJobService>.Instance);
        var orderId = PrintServices.SeedOrder(host.Tenants, host.AlphaId);

        Assert.True(await workerReceipts.TryCreateReceiptJobAsync(host.AlphaId, orderId, 1, "Alpha", Ct));

        var message = await alphaLive.NextAsync();
        Assert.Equal(orderId, message.GetProperty("orderId").GetGuid());
        Assert.Equal("Pending", message.GetProperty("status").GetString());
        Assert.Equal(1, message.GetProperty("version").GetInt64());

        // Beta's next message is its own, so alpha's never reached it.
        var forBeta = new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Pending, 1);
        await host.Services.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(host.BetaId, forBeta, Ct);
        AssertMessage(forBeta, await betaLive.NextAsync());
    }

    [Fact]
    public async Task TheLoopbackListener_RefusesAWrongSecretOrABadMessage_AndThePublicListenerDoesNotServeIt()
    {
        var internalPort = InternalTenantChangeChannelTests.FreeLoopbackPort();
        await using var host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = internalPort.ToString(CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = Secret
        });
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Owner);
        await using var live = await ConnectAsync(host, alpha);
        using var http = new HttpClient();
        var change = InternalTenantChangeMessage.From(host.AlphaId, new PrintJobChanged(Guid.NewGuid(), Guid.NewGuid(), PrintJobStatus.Printed, 9));
        var internalUrl = new Uri($"http://127.0.0.1:{internalPort}{InternalRealtimeOptions.Path}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(http, internalUrl, change, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(http, internalUrl, change, secret: Secret + "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(http, internalUrl, change with { Status = "3" }, Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(http, new Uri($"http://127.0.0.1:{internalPort}/other"), change, Secret)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Accepted, (await PostAsync(http, new Uri(host.BaseAddress, InternalRealtimeOptions.Path), change, Secret)).StatusCode);

        // None of those reached the connection; the correct one does.
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(http, internalUrl, change, Secret)).StatusCode);
        var message = await live.NextAsync();
        Assert.Equal(9, message.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task PrintState_IsTheTenantsCurrentReceiptState_ForManualPrintUsersOnly()
    {
        await using var host = await TenantSessionWebHost.StartAsync();
        using var alpha = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Cashier);
        using var alphaKitchen = await SignInAsync(host, TenantSessionWebHost.AlphaHost, host.AlphaId, UserRole.Kitchen);
        using var beta = await SignInAsync(host, TenantSessionWebHost.BetaHost, host.BetaId, UserRole.Owner);
        var orderId = PrintServices.SeedOrder(host.Tenants, host.AlphaId);
        var print = new PrintServices(host.Tenants, NullTenantChangeNotifier.Instance);

        await AssertStateAsync(host, alpha, orderId, version: 0, mode: "print", outcome: "none");

        await print.Receipts.TryCreateReceiptJobAsync(host.AlphaId, orderId, 1, "Alpha", Ct);
        var jobId = Assert.Single(await print.ReadJobsAsync(host.AlphaId, orderId)).Id;
        await AssertStateAsync(host, alpha, orderId, version: 1, mode: "queued", outcome: "none");
        await print.Bridge.TryMarkPrintingAsync(host.AlphaId, jobId, "Kitchen", null, Ct);
        await AssertStateAsync(host, alpha, orderId, version: 2, mode: "printing", outcome: "none");
        await print.Bridge.TryMarkFailedAsync(host.AlphaId, jobId, "offline", Ct);
        await AssertStateAsync(host, alpha, orderId, version: 3, mode: "reprint", outcome: "failed");

        // Another tenant cannot read it (not even learn that it exists), and Kitchen has no print action.
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/orders/{orderId}/print-state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/orders/{Guid.NewGuid()}/print-state")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await alphaKitchen.GetAsync($"/orders/{orderId}/print-state")).StatusCode);
    }

    private static async Task AssertStateAsync(TenantSessionWebHost host, TenantSessionClient client, Guid orderId, long version, string mode, string outcome)
    {
        var response = await client.GetAsync($"/orders/{orderId}/print-state");
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {string.Join(" | ", host.Logs.Entries.Where(e => e.Contains("xception", StringComparison.Ordinal)).Take(3))}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(orderId, body.GetProperty("orderId").GetGuid());
        Assert.Equal(version, body.GetProperty("version").GetInt64());
        Assert.Equal(mode, body.GetProperty("mode").GetString());
        Assert.Equal(outcome, body.GetProperty("outcome").GetString());
    }

    private static void AssertOrderMessage(OrderChanged expected, JsonElement message)
    {
        Assert.Equal("OrderChanged", message.GetProperty("type").GetString());
        Assert.Equal(expected.OrderId, message.GetProperty("orderId").GetGuid());
        Assert.Equal(expected.Version, message.GetProperty("version").GetInt64());
    }

    private static void AssertMessage(PrintJobChanged expected, JsonElement message)
    {
        Assert.Equal("PrintJobChanged", message.GetProperty("type").GetString());
        Assert.Equal(expected.OrderId, message.GetProperty("orderId").GetGuid());
        Assert.Equal(expected.PrintJobId, message.GetProperty("printJobId").GetGuid());
        Assert.Equal(expected.Status.ToString(), message.GetProperty("status").GetString());
        Assert.Equal(expected.Version, message.GetProperty("version").GetInt64());
    }

    private static async Task<TenantSessionClient> SignInAsync(TenantSessionWebHost host, string hostName, Guid tenantId, UserRole role)
    {
        var user = host.SeedUser(tenantId, $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local", role);
        var client = host.Client(hostName);
        await client.LoginAsync(user.Email);
        return client;
    }

    private static Task<HttpResponseMessage> NegotiateAsync(TenantSessionClient client, string? origin = null) =>
        client.SendAsync(
            HttpMethod.Post,
            $"{TenantEventsHub.Path}/negotiate?negotiateVersion=1",
            new StringContent(""),
            origin is null ? null : [new("Origin", origin)]);

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, Uri url, InternalTenantChangeMessage message, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(message) };
        if (secret is not null)
            request.Headers.Add(InternalRealtimeOptions.SecretHeaderName, secret);
        return http.SendAsync(request, Ct);
    }

    /// <summary>A real SignalR client over long polling, sending the tenant host and the session cookies.</summary>
    private static async Task<LiveConnection> ConnectAsync(TenantSessionWebHost host, TenantSessionClient client)
    {
        var received = Channel.CreateUnbounded<JsonElement>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.BaseAddress, TenantEventsHub.Path), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = inner => new TenantHostHandler(client) { InnerHandler = inner };
            })
            .Build();
        connection.On<JsonElement>(TenantEventsHub.ClientMethod, message => received.Writer.TryWrite(message));
        // Groups are joined after the handshake; the hub says when, and only then is the connection ready to receive.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On(TenantEventsHub.ReadyMethod, () => ready.TrySetResult());
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync(Ct);
            await ready.Task.WaitAsync(Wait, Ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new LiveConnection(connection, received.Reader, closed.Task);
    }

    private sealed class TenantHostHandler(TenantSessionClient client) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = client.Host;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", client.Cookies.Select(c => $"{c.Key}={c.Value}")));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record LiveConnection(HubConnection Connection, ChannelReader<JsonElement> Received, Task Closed) : IAsyncDisposable
    {
        public async Task<JsonElement> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Wait);
            return await Received.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
