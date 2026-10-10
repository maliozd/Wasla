using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-95: provider HTTP handlers are pooled by <see cref="IHttpClientFactory"/> and shared by every tenant (the
/// Yemeksepeti client is a singleton), so a cookie one connection's response sets must never be sent with another
/// connection's request. These tests resolve the clients from the production registration
/// (<see cref="ServiceCollectionExtensions.AddWaslaInfrastructure"/> in Real mode), so the real primary handler is
/// used, and point both providers at a fake provider on 127.0.0.1 that sets cookies on every response. Every
/// credential is fake and no request leaves the machine.
/// </summary>
public sealed class ProviderCookieIsolationTests
{
    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a", "c")]
    public async Task TrendyolGo_CookieSetForOneConnection_IsNeverSentForAnother(params string[] tenants)
    {
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);

        foreach (var tenant in tenants)
        {
            // A new scope per tenant, as the Worker and Web resolve clients.
            await using var scope = services.CreateAsyncScope();
            var orders = await Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None);
            Assert.Equal([$"package-supplier-{tenant}"], orders.Select(o => o.ExternalOrderId));
        }

        Assert.Equal(tenants.Select(t => $"supplier-{t}"), server.Requests.Select(r => r.Tag));
        AssertNoCookieWasSent(server);
    }

    [Fact]
    public async Task TrendyolGo_CredentialHeaders_StayWithTheirOwnConnection()
    {
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);

        foreach (var tenant in new[] { "a", "b", "a" })
        {
            await using var scope = services.CreateAsyncScope();
            await Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None);
        }

        Assert.Equal(3, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            var tenant = request.Tag["supplier-".Length..];
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"api-key-{tenant}:api-secret-{tenant}"));
            Assert.Equal($"Basic {basic}", request.Authorization);
            Assert.Equal($"executor-{tenant}@wasla.test", request.ExecutorUser);
            Assert.Null(request.Cookie);
        }
    }

    [Fact]
    public async Task TrendyolGo_FailedResponseThatSetsCookies_DoesNotSeedLaterConnections()
    {
        await using var server = await FakeProviderServer.StartAsync();
        server.FailingTags.Add("supplier-a");
        await using var services = ProductionServices(server);

        await using (var scopeA = services.CreateAsyncScope())
        {
            var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
                Trendyol(scopeA).FetchOrdersAsync(TrendyolConnection("a"), Window, CancellationToken.None));
            Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        }

        await using (var scopeB = services.CreateAsyncScope())
            await Trendyol(scopeB).FetchOrdersAsync(TrendyolConnection("b"), Window, CancellationToken.None);

        Assert.Equal(["supplier-a", "supplier-b"], server.Requests.Select(r => r.Tag));
        AssertNoCookieWasSent(server);
    }

    [Fact]
    public async Task TrendyolGo_LifecycleResponseCookies_AreNotSentWithAnotherConnectionsFetch()
    {
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);

        // The Web operator path: an accept for tenant A, then a Worker fetch for tenant B.
        await using (var scopeA = services.CreateAsyncScope())
            await Trendyol(scopeA).AcceptOrderAsync(TrendyolConnection("a"), "package-1", 15, CancellationToken.None);

        await using (var scopeB = services.CreateAsyncScope())
            await Trendyol(scopeB).FetchOrdersAsync(TrendyolConnection("b"), Window, CancellationToken.None);

        Assert.Equal(["PUT", "GET"], server.Requests.Select(r => r.Method));
        AssertNoCookieWasSent(server);
    }

    [Fact]
    public async Task TrendyolGo_ConcurrentConnections_NeverShareCookies()
    {
        const int tenantCount = 6;
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);
        var tenants = Enumerable.Range(0, tenantCount).Select(i => $"t{i}").ToArray();

        // Two waves. In each, the fake provider holds every response until all tenants' requests have arrived, so the
        // requests overlap on the pooled handler; the first wave's responses set every tenant's cookies.
        for (var wave = 0; wave < 2; wave++)
        {
            server.HoldUntil(tenantCount);
            await Task.WhenAll(tenants.Select(async tenant =>
            {
                await using var scope = services.CreateAsyncScope();
                var orders = await Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None);
                Assert.Equal([$"package-supplier-{tenant}"], orders.Select(o => o.ExternalOrderId));
            }));
        }

        Assert.Equal(tenantCount * 2, server.Requests.Count);
        AssertNoCookieWasSent(server);
    }

    [Fact]
    public async Task Yemeksepeti_CookieSetForOneConnection_IsNeverSentForAnother_AndTokensStayIsolated()
    {
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);

        foreach (var tenant in new[] { "a", "b", "c", "a" })
        {
            await using var scope = services.CreateAsyncScope();
            var orders = await Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None);
            Assert.Equal([$"order-vendor-{tenant}"], orders.Select(o => o.ExternalOrderId));
        }

        // A token request per new connection (WAS-88); the last fetch reuses tenant A's token.
        Assert.Equal(
            ["client-a", "vendor-a", "client-b", "vendor-b", "client-c", "vendor-c", "vendor-a"],
            server.Requests.Select(r => r.Tag));
        foreach (var request in server.Requests.Where(r => r.Tag.StartsWith("vendor-", StringComparison.Ordinal)))
            Assert.Equal($"Bearer token-for-client-{request.Tag["vendor-".Length..]}", request.Authorization);

        AssertNoCookieWasSent(server);
    }

    [Fact]
    public async Task Yemeksepeti_ConcurrentConnections_NeverShareCookies()
    {
        const int tenantCount = 4;
        await using var server = await FakeProviderServer.StartAsync();
        await using var services = ProductionServices(server);
        var tenants = Enumerable.Range(0, tenantCount).Select(i => $"t{i}").ToArray();

        // First wave: the token requests overlap. Second wave: the tokens are cached and the orders requests overlap.
        server.HoldUntil(tenantCount);
        await Task.WhenAll(tenants.Select(async tenant =>
        {
            await using var scope = services.CreateAsyncScope();
            await Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None);
        }));

        server.HoldUntil(tenantCount);
        await Task.WhenAll(tenants.Select(async tenant =>
        {
            await using var scope = services.CreateAsyncScope();
            await Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None);
        }));

        Assert.Equal(tenantCount * 3, server.Requests.Count);
        AssertNoCookieWasSent(server);
    }

    [Fact]
    public void RealMode_EveryProviderPrimaryHandler_HasCookiesDisabled()
    {
        var observed = new ConcurrentDictionary<string, HttpMessageHandler>();
        var configuration = RealModeConfiguration(new Uri("http://127.0.0.1:9/"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(configuration);
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new PrimaryHandlerObserver(observed));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        // Resolving the clients builds the handler of every provider client the factory serves.
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IFoodPlatformClient>());

        // The Yemeksepeti named client and the Trendyol GO typed client.
        Assert.Equal(2, observed.Count);
        Assert.Contains(YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName, observed.Keys);
        foreach (var (name, handler) in observed)
        {
            var primary = Assert.IsType<HttpClientHandler>(handler);
            Assert.False(primary.UseCookies, $"Provider client '{name}' stores and replays cookies.");
        }
    }

    private static void AssertNoCookieWasSent(FakeProviderServer server)
    {
        Assert.NotEmpty(server.Requests);
        var withCookies = server.Requests
            .Where(r => r.Cookie is not null)
            .Select(r => $"{r.Method} {r.Path} for {r.Tag} sent Cookie: {r.Cookie}")
            .ToArray();
        Assert.True(withCookies.Length == 0, string.Join(Environment.NewLine, withCookies));
    }

    private static TrendyolGoFoodPlatformClient Trendyol(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<TrendyolGoFoodPlatformClient>().Single();

    private static YemeksepetiFoodPlatformClient Yemeksepeti(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<YemeksepetiFoodPlatformClient>().Single();

    private static ServiceProvider ProductionServices(FakeProviderServer server)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(RealModeConfiguration(server.BaseAddress));
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        return services.BuildServiceProvider();
    }

    // Both providers point at the local fake provider; nothing is sent anywhere else.
    private static IConfiguration RealModeConfiguration(Uri baseAddress) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Real",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused",
                ["Platform:TrendyolGo:BaseUrl"] = baseAddress.AbsoluteUri,
                ["Platform:Yemeksepeti:BaseUrl"] = baseAddress.AbsoluteUri,
                ["Platform:Yemeksepeti:TokenPath"] = "/v2/oauth/token"
            })
            .Build();

    // PassthroughSecrets returns the stored value as the decrypted one, so these fields hold the fake credentials.
    private static PlatformConnection TrendyolConnection(string tenant) => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.TrendyolYemek,
        SupplierId = $"supplier-{tenant}",
        EncryptedApiKey = $"api-key-{tenant}",
        EncryptedApiSecret = $"api-secret-{tenant}",
        ExecutorEmail = $"executor-{tenant}@wasla.test",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private static readonly ConcurrentDictionary<string, Guid> YemeksepetiConnectionIds = new(StringComparer.Ordinal);

    // One stable connection id per tenant name, so a repeated fetch of a tenant may reuse its cached token.
    private static PlatformConnection YemeksepetiConnection(string tenant) => new()
    {
        Id = YemeksepetiConnectionIds.GetOrAdd(tenant, _ => Guid.NewGuid()),
        Platform = FoodPlatform.Yemeksepeti,
        SupplierId = $"chain-{tenant}",
        StoreId = $"vendor-{tenant}",
        EncryptedApiKey = $"client-{tenant}",
        EncryptedApiSecret = $"client-secret-{tenant}",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private sealed class PrimaryHandlerObserver(ConcurrentDictionary<string, HttpMessageHandler> observed)
        : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            // Every registered configuration has run; this is the handler the client will use.
            observed[builder.Name ?? string.Empty] = builder.PrimaryHandler;
        };
    }

    internal sealed record RecordedRequest(
        string Method,
        string Path,
        string Tag,
        string? Cookie,
        string? Authorization,
        string? ExecutorUser);

    /// <summary>
    /// A Trendyol GO and Yemeksepeti stand-in on 127.0.0.1. Every response, including a failure, sets two cookies
    /// named after the connection it answers, so a cookie replayed for another connection is recognisable.
    /// </summary>
    private sealed class FakeProviderServer : IAsyncDisposable
    {
        private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private Hold? _hold;

        private FakeProviderServer(WebApplication app) => _app = app;

        public Uri BaseAddress { get; private set; } = null!;

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        /// <summary>Tags whose responses fail with HTTP 500 after setting their cookies.</summary>
        public ConcurrentBag<string> FailingTags { get; } = [];

        /// <summary>Holds the next <paramref name="count"/> requests until all of them have arrived.</summary>
        public void HoldUntil(int count) => Volatile.Write(ref _hold, new Hold(count));

        public static async Task<FakeProviderServer> StartAsync()
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new FakeProviderServer(app);
            app.Run(server.HandleAsync);
            await app.StartAsync();
            server.BaseAddress = new Uri(app.Urls.Single());
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            var path = request.Path.Value ?? string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string tag;
            string body;

            if (path == "/v2/oauth/token")
            {
                var form = await request.ReadFormAsync(context.RequestAborted);
                tag = form["client_id"].ToString();
                body = $$"""{"access_token":"token-for-{{tag}}","token_type":"bearer","expires_in":7200}""";
            }
            else if (segments is ["v2", "chains", _, "vendors", var vendor, "orders"])
            {
                tag = vendor;
                body = $$"""{"data":[{"id":"order-{{vendor}}","status":"NEW"}],"page":0,"total_pages":1,"total_count":1}""";
            }
            else if (segments is ["integrator", "order", "meal", "suppliers", var supplier, "packages"])
            {
                tag = supplier;
                body = $$"""{"page":0,"size":50,"totalPages":1,"totalCount":1,"content":[{"id":"package-{{supplier}}","packageStatus":"Created","packageCreationDate":1700000000000,"totalPrice":10}]}""";
            }
            else if (segments is ["integrator", "order", "meal", "suppliers", var lifecycleSupplier, "packages", _])
            {
                tag = lifecycleSupplier;
                body = "{}";
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            _requests.Enqueue(new RecordedRequest(
                request.Method,
                path,
                tag,
                request.Headers.Cookie.Count == 0 ? null : request.Headers.Cookie.ToString(),
                request.Headers.Authorization.Count == 0 ? null : request.Headers.Authorization.ToString(),
                request.Headers["x-executor-user"].Count == 0 ? null : request.Headers["x-executor-user"].ToString()));

            if (Volatile.Read(ref _hold) is { } hold)
                await hold.ArriveAndWaitAsync(context.RequestAborted);

            context.Response.Headers.Append("Set-Cookie", $"wasla_fake_session={tag}; Path=/");
            context.Response.Headers.Append("Set-Cookie", $"wasla_fake_affinity={tag}-node; Path=/; HttpOnly");

            if (FailingTags.Contains(tag))
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class Hold(int count)
        {
            private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _remaining = count;

            public async Task ArriveAndWaitAsync(CancellationToken ct)
            {
                var left = Interlocked.Decrement(ref _remaining);
                if (left == 0)
                    _allArrived.TrySetResult();
                if (left >= 0)
                    await _allArrived.Task.WaitAsync(HoldTimeout, ct);
            }
        }
    }
}
