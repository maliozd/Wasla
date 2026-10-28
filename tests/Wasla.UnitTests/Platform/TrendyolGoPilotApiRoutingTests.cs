using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The deployment's Trendyol GO API is production. Only the webhook pilot's one configured connection (its id, seller and
/// store) is routed to exactly the stage API, for every request it makes; every other connection, including a real
/// restaurant's and a look-alike with the pilot's seller and store, stays on production. A pilot connection that no
/// longer matches its configuration calls no API at all.
/// </summary>
public sealed class TrendyolGoPilotApiRoutingTests
{
    private const string Production = "api.tgoapis.com";
    private const string Stage = "stageapi.tgoapis.com";
    private const string PilotSeller = "107385";
    private const string PilotStore = "1331";

    private static readonly Guid PilotId = Guid.NewGuid();
    private static readonly OrderFetchWindow Window = new(new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc));

    private readonly ConcurrentQueue<Uri> _requests = new();

    private static readonly string[] AllOperations = ["fetch", "accept", "invoiced", "shipped", "delivered", "reject"];

    public static TheoryData<string> Operations => new(AllOperations);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PilotConnection_UsesExactlyTheStageApi(string operation)
    {
        await RunAsync(Client(Pilot()), operation, PilotConnection());

        var request = Assert.Single(_requests);
        Assert.Equal("https", request.Scheme);
        Assert.Equal(Stage, request.Host);
        Assert.Equal(443, request.Port);
        Assert.StartsWith($"/integrator/order/meal/suppliers/{PilotSeller}/packages", request.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task RestaurantConnection_StaysOnTheProductionApi(string operation)
    {
        var sushi = Connection(Guid.NewGuid(), supplier: "200412", store: "9423");

        await RunAsync(Client(Pilot()), operation, sushi);

        var request = Assert.Single(_requests);
        Assert.Equal(Production, request.Host);
        Assert.StartsWith("/integrator/order/meal/suppliers/200412/packages", request.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task LookAlikeConnection_WithThePilotsSellerAndStore_StaysOnTheProductionApi(string operation)
    {
        await RunAsync(Client(Pilot()), operation, Connection(Guid.NewGuid(), PilotSeller, PilotStore));

        Assert.Equal(Production, Assert.Single(_requests).Host);
    }

    [Fact]
    public async Task PilotConnection_StaysOnTheStageApi_WhileThePilotIsOff()
    {
        // Turning the receiver off must not send the stage seller's credentials to the production API.
        var pilot = Pilot();
        pilot.Enabled = false;

        await RunAsync(Client(pilot), "fetch", PilotConnection());
        await RunAsync(Client(pilot), "fetch", Connection(Guid.NewGuid(), "200412", "9423"));

        Assert.Equal([Stage, Production], _requests.Select(r => r.Host));
    }

    [Fact]
    public async Task NothingConfigured_EveryConnectionUsesTheDeploymentApi()
    {
        var unconfigured = new TrendyolGoMealWebhookPilotOptions();

        await RunAsync(Client(unconfigured), "fetch", PilotConnection());
        await RunAsync(Client(unconfigured), "accept", Connection(Guid.NewGuid(), "200412", "9423"));

        Assert.All(_requests, r => Assert.Equal(Production, r.Host));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task ConnectionIdAlone_WithoutTheOverride_IsNotRouted()
    {
        var pilot = Pilot();
        pilot.ProviderApiBaseUrl = null;

        await RunAsync(Client(pilot), "fetch", PilotConnection());

        Assert.Equal(Production, Assert.Single(_requests).Host);
    }

    public static TheoryData<string, string?, string?, bool> PilotIdThatNoLongerMatches => new()
    {
        { "other seller", "107386", PilotStore, true },
        { "other store", PilotSeller, "1332", true },
        { "no seller or store", null, "", true },
        { "inactive", PilotSeller, PilotStore, false }
    };

    [Theory]
    [MemberData(nameof(PilotIdThatNoLongerMatches))]
    public async Task PilotConnectionThatNoLongerMatches_CallsNoApi(string reason, string? supplier, string? store, bool active)
    {
        _ = reason;
        var connection = Connection(PilotId, supplier, store ?? string.Empty);
        connection.IsActive = active;

        foreach (var operation in AllOperations)
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Client(Pilot()), operation, connection));

        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("https://api.tgoapis.com")]
    [InlineData("https://stageapi.tgoapis.com/other")]
    [InlineData("http://stageapi.tgoapis.com")]
    public async Task UnvalidatedOverrideThatIsNotExactlyStage_CallsNoApiForThePilot_AndLeavesOthersOnProduction(string overrideUrl)
    {
        var pilot = Pilot();
        pilot.ProviderApiBaseUrl = overrideUrl;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Client(pilot), "fetch", PilotConnection()));
        await RunAsync(Client(pilot), "fetch", Connection(Guid.NewGuid(), "200412", "9423"));

        Assert.Equal(Production, Assert.Single(_requests).Host);
    }

    [Fact]
    public void OnlyTheConfiguredConnectionIsEverRouted()
    {
        var pilot = Pilot();

        Assert.Equal(new Uri("https://stageapi.tgoapis.com/"), pilot.ProviderApiFor(PilotConnection()));
        Assert.Null(pilot.ProviderApiFor(Connection(Guid.NewGuid(), PilotSeller, PilotStore)));
        Assert.Null(pilot.ProviderApiFor(Connection(Guid.Empty, PilotSeller, PilotStore)));
        Assert.Null(new TrendyolGoMealWebhookPilotOptions { ProviderApiBaseUrl = "https://stageapi.tgoapis.com" }
            .ProviderApiFor(Connection(Guid.Empty, PilotSeller, PilotStore)));
    }

    private static TrendyolGoMealWebhookPilotOptions Pilot() => new()
    {
        Enabled = true,
        TenantId = Guid.NewGuid(),
        PlatformConnectionId = PilotId,
        SellerId = PilotSeller,
        StoreId = PilotStore,
        ProviderApiBaseUrl = "https://stageapi.tgoapis.com"
    };

    private static PlatformConnection PilotConnection() => Connection(PilotId, PilotSeller, PilotStore);

    private static PlatformConnection Connection(Guid id, string? supplier, string store) => new()
    {
        Id = id,
        Platform = FoodPlatform.TrendyolYemek,
        StoreId = store,
        SupplierId = supplier,
        ExecutorEmail = "executor@example.invalid",
        EncryptedApiKey = "key",
        EncryptedApiSecret = "secret",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private TrendyolGoFoodPlatformClient Client(TrendyolGoMealWebhookPilotOptions pilot) =>
        new(
            new HttpClient(new DelegateHandler(request =>
            {
                _requests.Enqueue(request.RequestUri!.IsAbsoluteUri ? request.RequestUri : new Uri(new Uri("https://" + Production + "/"), request.RequestUri));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"page":0,"size":50,"totalPages":0,"totalCount":0,"content":[]}""") };
            }))
            {
                // The deployment's API, as wasla-preflight requires it.
                BaseAddress = new Uri("https://" + Production + "/")
            },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://" + Production }),
            Options.Create(pilot),
            NullLogger<TrendyolGoFoodPlatformClient>.Instance,
            new LimiterClock().Limiter(),
            TimeProvider.System);

    private static Task RunAsync(TrendyolGoFoodPlatformClient client, string operation, PlatformConnection connection) =>
        operation switch
        {
            "fetch" => client.FetchOrdersAsync(connection, Window, Ct),
            "accept" => client.AcceptOrderAsync(connection, "pkg-1", 15, Ct),
            "invoiced" => client.MarkInvoicedAsync(connection, "pkg-1", Ct),
            "shipped" => client.MarkShippedAsync(connection, "pkg-1", Ct),
            "delivered" => client.MarkDeliveredAsync(connection, "pkg-1", Ct),
            "reject" => client.RejectOrderAsync(connection, "pkg-1", ["1"], 621, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
