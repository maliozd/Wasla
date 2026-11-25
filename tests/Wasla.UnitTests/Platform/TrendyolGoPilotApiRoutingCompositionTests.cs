using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Security;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Dtos;
using Wasla.Integrations.Platform.TrendyolGo;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The webhook pilot's settings and validation stay in Orders, and the Trendyol GO client takes its API from the provider
/// library's narrow <see cref="ITrendyolGoApiRouter"/> (WAS-233). These tests compose the client as the hosts do
/// (<c>AddWaslaInfrastructure</c> in Real mode, the pilot bound from configuration) and answer every request with a
/// handler added after the provider registration, so nothing leaves the process. They show that Orders' rule replaces
/// the library's default, that the stage routing holds while the pilot is off, and that a connection that no longer
/// matches the pilot is refused before its credentials are read and before any request is sent.
/// </summary>
public sealed class TrendyolGoPilotApiRoutingCompositionTests
{
    private const string Production = "api.tgoapis.com";
    private const string Stage = "stageapi.tgoapis.com";
    private const string PilotSeller = "107385";
    private const string PilotStore = "1331";

    private static readonly Guid PilotId = Guid.NewGuid();
    private static readonly OrderFetchWindow Window = new(
        new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc));

    private static readonly string[] AllOperations = ["fetch", "accept", "invoiced", "shipped", "delivered", "reject"];

    private readonly ConcurrentQueue<Uri> _requests = new();

    public static TheoryData<string> Operations => new(AllOperations);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdersComposition_GivesTheClientThePilotRule_InsteadOfTheLibraryDefault()
    {
        await using var provider = Compose(enabled: false);

        var router = Assert.Single(provider.GetServices<ITrendyolGoApiRouter>());
        Assert.IsType<TrendyolGoMealWebhookPilotApiRouter>(router);
        Assert.Equal(new Uri("https://" + Stage + "/"), router.ApiFor(PilotConnection(new CountingCredentials())));
        Assert.Null(router.ApiFor(Connection(Guid.NewGuid(), "200412", "9423", active: true, new CountingCredentials())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PilotConnection_GoesToTheStageApi_WhetherOrNotThePilotIsEnabled(bool enabled)
    {
        await using var provider = Compose(enabled);
        await using var scope = provider.CreateAsyncScope();
        var client = Trendyol(scope);

        foreach (var operation in AllOperations)
            await RunAsync(client, operation, PilotConnection(new CountingCredentials()));
        await RunAsync(client, "fetch", Connection(Guid.NewGuid(), "200412", "9423", active: true, new CountingCredentials()));

        Assert.Equal([.. AllOperations.Select(_ => Stage), Production], _requests.Select(r => r.Host));
    }

    public static TheoryData<string, string?, string, bool, string> PilotIdThatNoLongerMatches()
    {
        var data = new TheoryData<string, string?, string, bool, string>();
        foreach (var operation in AllOperations)
        {
            data.Add("other seller", "107386", PilotStore, true, operation);
            data.Add("other store", PilotSeller, "1332", true, operation);
            data.Add("no seller or store", null, "", true, operation);
            data.Add("inactive", PilotSeller, PilotStore, false, operation);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PilotIdThatNoLongerMatches))]
    public async Task PilotConnectionThatNoLongerMatches_IsRefused_BeforeCredentialsAreRead_AndSendsNoRequest(
        string reason, string? supplier, string store, bool active, string operation)
    {
        _ = reason;
        await using var provider = Compose(enabled: false);
        await using var scope = provider.CreateAsyncScope();
        var credentials = new CountingCredentials();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(Trendyol(scope), operation, Connection(PilotId, supplier, store, active, credentials)));

        Assert.Equal(0, credentials.Calls);
        Assert.Empty(_requests);
    }

    private ServiceProvider Compose(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Real",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused",
                ["Platform:TrendyolGo:BaseUrl"] = "https://" + Production,
                ["TrendyolGoMealWebhookPilot:Enabled"] = enabled ? "true" : "false",
                ["TrendyolGoMealWebhookPilot:TenantId"] = Guid.NewGuid().ToString(),
                ["TrendyolGoMealWebhookPilot:PlatformConnectionId"] = PilotId.ToString(),
                ["TrendyolGoMealWebhookPilot:SellerId"] = PilotSeller,
                ["TrendyolGoMealWebhookPilot:StoreId"] = PilotStore,
                ["TrendyolGoMealWebhookPilot:SecretSha256"] = new string('a', 64),
                ["TrendyolGoMealWebhookPilot:ReceiverBaseUrl"] = "https://wasla.example/api/webhooks/trendyol-go/meal",
                ["TrendyolGoMealWebhookPilot:ProviderApiBaseUrl"] = "https://" + Stage
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(configuration);
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        services.AddHttpClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName)
            .AddHttpMessageHandler(() => new AnsweringHandler(_requests));
        return services.BuildServiceProvider();
    }

    private static TrendyolGoFoodPlatformClient Trendyol(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<TrendyolGoFoodPlatformClient>().Single();

    private static ProviderConnectionContext PilotConnection(IProviderCredentialSource credentials) =>
        Connection(PilotId, PilotSeller, PilotStore, active: true, credentials);

    private static ProviderConnectionContext Connection(
        Guid id, string? supplier, string store, bool active, IProviderCredentialSource credentials) =>
        new(id, ProviderPlatform.TrendyolYemek, store, supplier, "executor@example.invalid", active, credentials);

    private static Task RunAsync(TrendyolGoFoodPlatformClient client, string operation, ProviderConnectionContext connection) =>
        operation switch
        {
            "fetch" => client.FetchOrdersAsync(connection, Window, Ct),
            "accept" => client.AcceptOrderAsync(connection, "pkg-1", 15, Ct),
            "invoiced" => client.MarkInvoicedAsync(connection, "pkg-1", Ct),
            "shipped" => client.MarkShippedAsync(connection, "pkg-1", Ct),
            "delivered" => client.MarkDeliveredAsync(connection, "pkg-1", Ct),
            "reject" => client.RejectOrderAsync(connection, TrendyolGoRejectRequestTests.StoredPackageOrder("pkg-1", "1").AsProviderReference(), 621, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private sealed class CountingCredentials : IProviderCredentialSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProviderCredentials> GetCredentialsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProviderCredentials("key", "secret"));
        }
    }

    /// <summary>Answers every request itself, so nothing reaches the primary handler or the network.</summary>
    private sealed class AnsweringHandler(ConcurrentQueue<Uri> requests) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"page":0,"size":50,"totalPages":0,"totalCount":0,"content":[]}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
