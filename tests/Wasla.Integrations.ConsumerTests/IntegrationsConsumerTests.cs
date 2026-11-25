using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Integrations.DependencyInjection;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Dtos;
using Wasla.Integrations.Platform.Mock;
using Wasla.Integrations.Platform.TrendyolGo;
using Wasla.Integrations.Platform.Yemeksepeti;

namespace Wasla.Integrations.ConsumerTests;

/// <summary>
/// A host that is not Orders composes the provider library through <see cref="WaslaIntegrationsServiceCollectionExtensions.AddWaslaIntegrations"/>
/// and uses its clients with its own credential source (WAS-233). Real mode runs against a stub handler: no request
/// leaves the process.
/// </summary>
public sealed class IntegrationsConsumerTests
{
    private static readonly OrderFetchWindow Window = new(
        new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MockMode_RegistersOnlyTheMockClients_AndTheyServeTheConsumer()
    {
        await using var provider = Compose("Mock");

        var clients = provider.GetServices<IFoodPlatformClient>().ToList();
        Assert.Equal(
            [ProviderPlatform.Yemeksepeti, ProviderPlatform.GetirYemek, ProviderPlatform.TrendyolYemek],
            clients.Select(c => c.Platform));
        Assert.All(clients, c => Assert.IsAssignableFrom<IMockFoodPlatformClient>(c));
        Assert.Null(provider.GetService<TrendyolRequestRateLimiter>());
        Assert.Null(provider.GetService<IHttpClientFactory>());

        var credentials = new CountingCredentialSource();
        var connection = Connection(ProviderPlatform.TrendyolYemek, credentials);
        var trendyol = clients.Single(c => c.Platform == ProviderPlatform.TrendyolYemek);

        // The mock fails one fetch in ten on purpose, like a provider; ten failures in a row are not expected.
        IReadOnlyCollection<ExternalOrderDto>? orders = null;
        for (var attempt = 0; attempt < 10 && orders is null; attempt++)
        {
            try
            {
                orders = await trendyol.FetchOrdersAsync(connection, Window, Ct);
            }
            catch (HttpRequestException)
            {
            }
        }

        Assert.NotNull(orders);
        Assert.All(orders, o => Assert.Equal(ProviderPlatform.TrendyolYemek, o.Platform));
        Assert.True(credentials.Calls >= 1);

        await trendyol.AcceptOrderAsync(connection, "consumer-order-1", 15, Ct);
    }

    [Fact]
    public async Task RealMode_TrendyolGo_FetchesThroughItsOwnHttpClient_WithTheConsumersCredentials()
    {
        var requests = new List<HttpRequestMessage>();
        await using var provider = Compose("Real", services =>
            services.AddHttpClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName)
                .AddHttpMessageHandler(() => new StubHandler(requests, Page)));

        var clients = provider.GetServices<IFoodPlatformClient>().ToList();
        Assert.IsType<YemeksepetiFoodPlatformClient>(clients.Single(c => c.Platform == ProviderPlatform.Yemeksepeti));
        Assert.IsAssignableFrom<IMockFoodPlatformClient>(clients.Single(c => c.Platform == ProviderPlatform.GetirYemek));
        var trendyol = Assert.IsType<TrendyolGoFoodPlatformClient>(clients.Single(c => c.Platform == ProviderPlatform.TrendyolYemek));
        Assert.NotNull(provider.GetService<TrendyolRequestRateLimiter>());
        Assert.IsType<TrendyolGoDeploymentApiRouter>(provider.GetRequiredService<ITrendyolGoApiRouter>());

        var credentials = new CountingCredentialSource();
        var order = Assert.Single(await trendyol.FetchOrdersAsync(Connection(ProviderPlatform.TrendyolYemek, credentials), Window, Ct));

        Assert.Equal(ProviderPlatform.TrendyolYemek, order.Platform);
        Assert.Equal("p1", order.ExternalOrderId);
        Assert.Equal("Created", order.ExternalStatus);
        Assert.Equal(ProviderPaymentMethod.Unknown, order.PaymentMethod);
        Assert.Equal(ProviderPaymentStatus.Unknown, order.PaymentStatus);

        var request = Assert.Single(requests);
        Assert.Equal("trendyol.consumer.test", request.RequestUri!.Host);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("consumer-key:consumer-secret")), request.Headers.Authorization.Parameter);
        Assert.Equal(1, credentials.Calls);
    }

    private const string Page = """
        {"page":0,"size":50,"totalPages":1,"totalCount":1,"content":[{"id":"p1","orderNumber":"p1","packageCreationDate":1792141200000,"packageStatus":"Created","totalPrice":10}]}
        """;

    private static ServiceProvider Compose(string mode, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = mode,
                ["Platform:TrendyolGo:BaseUrl"] = "https://trendyol.consumer.test/",
                ["Platform:TrendyolGo:AgentName"] = "ConsumerTests"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaIntegrations(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ProviderConnectionContext Connection(ProviderPlatform platform, IProviderCredentialSource credentials) =>
        new(Guid.NewGuid(), platform, "store-1", "supplier-1", "executor@example.invalid", isActive: true, credentials);

    private sealed class CountingCredentialSource : IProviderCredentialSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProviderCredentials> GetCredentialsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProviderCredentials("consumer-key", "consumer-secret"));
        }
    }

    /// <summary>Answers every request itself, so nothing reaches the primary handler or the network.</summary>
    private sealed class StubHandler(List<HttpRequestMessage> requests, string body) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests)
                requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
