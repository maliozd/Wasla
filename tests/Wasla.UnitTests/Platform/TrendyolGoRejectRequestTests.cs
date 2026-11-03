using System.Net;
using System.Text;
using System.Text.Json;
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
/// The Trendyol GO reject request against the official "Sipariş İptali" and "Paket Modelleri" documentation
/// (developers.tgoapps.com, checked 2026-11-03): a full cancellation sends the package's <c>packageItemId</c> values in
/// <c>itemIdList</c> and a restaurant-side <c>reasonId</c>. The stage API refused the earlier body (no item ids,
/// <c>reasonId</c> 0) with HTTP 400. The package comes from a documented-shape page fetched by the real client, so the
/// item ids are read from the raw payload exactly as sync stores it. Every value is synthetic; this proves the request
/// Wasla sends, not the provider's acceptance of it.
/// </summary>
public sealed class TrendyolGoRejectRequestTests
{
    private const string Supplier = "900001";
    private const string PackageId = "f0e1d2c3b4a5968778695a4b3c2d1e0ff0e1d2c3b4a5968778695a4b3c2d1e0f";

    private static readonly DateTime WindowEnd = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string Page = $$"""
        {
          "page": 0, "size": 50, "totalPages": 1, "totalCount": 1,
          "content": [
            {
              "id": "{{PackageId}}",
              "supplierId": {{Supplier}},
              "storeId": 900002,
              "orderCode": "7XK2Q",
              "orderNumber": "9876543211",
              "packageCreationDate": 1793696400000,
              "packageModificationDate": 1793696460000,
              "packageStatus": "Created",
              "totalPrice": 365,
              "lines": [
                {
                  "price": 145, "unitSellingPrice": 145,
                  "items": [
                    { "packageItemId": "1000008723596", "lineItemId": 1000008181985, "isCancelled": false, "coupon": null, "promotions": [] },
                    { "packageItemId": "1000008723597", "lineItemId": 1000008181986, "isCancelled": true, "coupon": null, "promotions": [] },
                    { "packageItemId": "1000008723598", "lineItemId": 1000008181987, "isCancelled": false, "coupon": null, "promotions": [] }
                  ],
                  "productId": 203695, "name": "Synthetic Menu",
                  "modifierProducts": [], "extraIngredients": [], "removedIngredients": []
                },
                {
                  "price": 75, "unitSellingPrice": 75,
                  "items": [
                    { "packageItemId": "1000008723599", "lineItemId": 1000008181988, "isCancelled": false, "coupon": null, "promotions": [] }
                  ],
                  "productId": 6850, "name": "Synthetic Drink",
                  "modifierProducts": [], "extraIngredients": [], "removedIngredients": []
                }
              ]
            }
          ]
        }
        """;

    private readonly List<(HttpMethod Method, string Path, string? Body)> _requests = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reject_SendsEveryItemStillInTheStoredPackage_WithTheDocumentedReason()
    {
        var client = Client();
        var package = Assert.Single(await client.FetchOrdersAsync(Connection(), new OrderFetchWindow(WindowEnd.AddHours(-1), WindowEnd), Ct));
        _requests.Clear();

        await client.RejectOrderAsync(Connection(), StoredOrder(package.ExternalOrderId, package.RawPayloadJson), Ct);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/integrator/order/meal/suppliers/{Supplier}/packages/unsupplied", request.Path);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(["packageId", "itemIdList", "reasonId"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(PackageId, body.RootElement.GetProperty("packageId").GetString());
        // packageItemId is a string in the documented model; the item already cancelled is not sent again.
        Assert.Equal(
            ["1000008723596", "1000008723598", "1000008723599"],
            body.RootElement.GetProperty("itemIdList").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("reasonId").ValueKind);
        Assert.Equal(623, body.RootElement.GetProperty("reasonId").GetInt32());
    }

    [Fact]
    public void RejectReason_IsADocumentedRestaurantReasonForPlatformCouriers()
    {
        // 621, 622, 623 and 627 are open to every restaurant; 624 and 626 only to sellers with their own couriers.
        Assert.Contains(TrendyolGoFoodPlatformClient.RejectReasonId, new[] { 621, 622, 623, 627 });
    }

    public static TheoryData<string, string> UnusableStoredPackages => new()
    {
        { "empty", "" },
        { "malformed", "{\"id\":" },
        { "json null", "null" },
        { "another package", $$"""{"id":"another-package","lines":[{"items":[{"packageItemId":"1","isCancelled":false}]}]}""" },
        { "no lines", $$"""{"id":"{{PackageId}}"}""" },
        { "no items", $$"""{"id":"{{PackageId}}","lines":[{"items":[]}]}""" },
        { "every item cancelled", $$"""{"id":"{{PackageId}}","lines":[{"items":[{"packageItemId":"1","isCancelled":true}]}]}""" },
        { "item without id", $$"""{"id":"{{PackageId}}","lines":[{"items":[{"packageItemId":"1","isCancelled":false},{"packageItemId":" ","isCancelled":false}]}]}""" }
    };

    /// <summary>A request that could only cancel part of the order, or none of it, is never sent.</summary>
    [Theory]
    [MemberData(nameof(UnusableStoredPackages))]
    public async Task Reject_WithoutACompleteStoredPackage_FailsBeforeAnyRequest(string reason, string rawPayloadJson)
    {
        var client = Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RejectOrderAsync(Connection(), StoredOrder(PackageId, rawPayloadJson), Ct));

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.StartsWith("TrendyolGo RejectOrder:", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_requests);
    }

    /// <summary>
    /// An order holding a stored Trendyol GO package with the given items, none cancelled: for tests that need a reject
    /// the client is able to send.
    /// </summary>
    internal static Order StoredPackageOrder(string packageId, params string[] packageItemIds) =>
        StoredOrder(
            packageId,
            JsonSerializer.Serialize(new
            {
                id = packageId,
                lines = new[] { new { items = packageItemIds.Select(id => new { packageItemId = id, isCancelled = false }) } }
            }));

    private static Order StoredOrder(string packageId, string rawPayloadJson) => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = packageId,
        InternalStatus = OrderStatus.New,
        RawPayloadJson = rawPayloadJson
    };

    private static PlatformConnection Connection() => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        SupplierId = Supplier,
        ExecutorEmail = "executor-reject@example.invalid",
        EncryptedApiKey = "fake-reject-api-key",
        EncryptedApiSecret = "fake-reject-api-secret"
    };

    private TrendyolGoFoodPlatformClient Client()
    {
        var options = new TrendyolGoOptions { AgentName = "WaslaTest", BaseUrl = "https://stageapi.tgoapis.com" };
        return new TrendyolGoFoodPlatformClient(
            new HttpClient(new RecordingHandler(_requests)) { BaseAddress = new Uri(options.BaseUrl) },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(options),
            Options.Create(new TrendyolGoMealWebhookPilotOptions()),
            NullLogger<TrendyolGoFoodPlatformClient>.Instance,
            new LimiterClock().Limiter(),
            TimeProvider.System);
    }

    // Reads the body while the request is alive: the client disposes it after sending.
    private sealed class RecordingHandler(List<(HttpMethod Method, string Path, string? Body)> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            var content = request.Method == HttpMethod.Get ? Page : "";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
