using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Application.Platform.Dtos;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The real Trendyol GO client against one packages page shaped like the official "Sipariş Paketlerini Çekme" Model 1
/// response (developers.tgoapps.com, checked 2026-10-16): the same field names and nesting, including fields Wasla does
/// not read (<c>pickupEtaState</c>, <c>userInformation</c>, <c>coupon</c>, <c>promotions</c>, a 5-character
/// <c>orderCode</c> and a masked <c>apartmentNumber</c> from the Uber Eats transition). Every value is synthetic. This
/// proves the request Wasla sends and how it maps that shape, not the real provider's behaviour.
/// <para>
/// <c>lines[].items[].lineItemId</c> is a JSON number in the official model (<c>packageItemId</c> is a string). A page
/// whose packages carry line items failed to deserialize while the DTO declared it as a string (WAS-126); empty pages
/// never exercised it. One value here is above <see cref="int.MaxValue"/>, as real ids are.
/// </para>
/// </summary>
public sealed class TrendyolGoPackageContractTests
{
    private const string Supplier = "900001";
    private const string Store = "900002";
    private const string AgentName = "WaslaTest";
    private const string ApiKey = "fake-contract-api-key";
    private const string ApiSecret = "fake-contract-api-secret";
    private const string ExecutorEmail = "executor-contract@example.invalid";
    private const string PackageId = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static readonly DateTime WindowEnd = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string Page = $$"""
        {
          "page": 0, "size": 50, "totalPages": 1, "totalCount": 1,
          "content": [
            {
              "id": "{{PackageId}}",
              "supplierId": {{Supplier}},
              "storeId": {{Store}},
              "orderCode": "4D5AC",
              "storePickupSelected": false,
              "deliveryType": "STORE",
              "packageCreationDate": 1792141200000,
              "packageModificationDate": 1792141260000,
              "preparationTime": 0,
              "orderId": "1009876543210",
              "orderNumber": "9876543210",
              "totalPrice": 320,
              "callCenterPhone": "0212 000 00 00",
              "customer": { "id": 1, "firstName": "Deneme", "lastName": "Musteri", "email": "contract@example.invalid" },
              "payment": { "paymentType": "PAY_WITH_ON_DELIVERY", "mealCard": null, "onDelivery": { "paymentType": "CASH" } },
              "address": {
                "firstName": "Deneme", "lastName": "Musteri", "company": "",
                "address1": "Kontrat Sokak 1, 9 kat 2 daire", "address2": "",
                "city": "İstanbul", "cityCode": 34, "cityId": 34, "district": "Kadıköy", "districtId": 1,
                "neighborhoodId": 1, "neighborhood": "Moda",
                "apartmentNumber": "TGO Yemek", "floor": "TGO Yemek", "doorNumber": "TGO Yemek",
                "addressDescription": "", "postalCode": "34710", "countryCode": "TR",
                "latitude": "40.98", "longitude": "29.03", "phone": "5000000000", "countryPhoneCode": "+90",
                "pinCode": "19876543210"
              },
              "packageStatus": "Created",
              "lines": [
                {
                  "price": 150, "unitSellingPrice": 150,
                  "items": [
                    { "packageItemId": "1", "lineItemId": 123456789, "isCancelled": false, "coupon": null, "promotions": [] },
                    { "packageItemId": "2", "lineItemId": 123456790, "isCancelled": true, "coupon": null, "promotions": [] },
                    { "packageItemId": "3", "lineItemId": 9000000000013, "isCancelled": false, "coupon": null, "promotions": [] }
                  ],
                  "productId": 1001, "name": "Kontrat Menü", "description": "Synthetic menu", "isClaimRateExceeded": false,
                  "modifierProducts": [
                    {
                      "name": "Burger", "price": 0, "productId": 2001, "modifierGroupId": 3001,
                      "modifierProducts": [
                        { "name": "Acılı", "price": 0, "productId": 2002, "modifierGroupId": 3002,
                          "modifierProducts": [], "extraIngredients": [], "removedIngredients": [], "isClaimRateExceeded": false }
                      ],
                      "extraIngredients": [ { "id": 4001, "name": "Cheddar", "price": 10 } ],
                      "removedIngredients": [ { "id": 4002, "name": "Soğan" } ],
                      "isClaimRateExceeded": false
                    },
                    { "name": "Kola", "price": 0, "productId": 2003, "modifierGroupId": 3003,
                      "modifierProducts": [], "extraIngredients": [], "removedIngredients": [], "isClaimRateExceeded": false }
                  ],
                  "extraIngredients": [], "removedIngredients": []
                },
                {
                  "price": 20, "unitSellingPrice": 20,
                  "items": [ { "packageItemId": "4", "lineItemId": 9000000000014, "isCancelled": false, "coupon": null, "promotions": [] } ],
                  "productId": 1002, "name": "Ayran", "description": "", "isClaimRateExceeded": false,
                  "modifierProducts": [], "extraIngredients": [], "removedIngredients": []
                }
              ],
              "customerNote": "Zile basmayın",
              "lastModifiedDate": 1792141260000,
              "isCourierNearby": false,
              "cancelInfo": null,
              "eta": "15 - 36 dk",
              "testPackage": true,
              "pickupEtaState": "",
              "estimatedPickupTimeMin": 0,
              "estimatedPickupTimeMax": 0,
              "coupon": null,
              "promotions": [],
              "userInformation": { "appName": "UberEats" },
              "totalDeliveryPrice": null
            }
          ]
        }
        """;

    private readonly CollectingLogger<TrendyolGoFoodPlatformClient> _clientLog = new();
    private readonly List<HttpRequestMessage> _requests = [];

    [Fact]
    public async Task Fetch_SendsTheDocumentedRequest()
    {
        await FetchAsync();

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("stageapi.tgoapis.com", request.RequestUri!.Host);
        Assert.Equal($"/integrator/order/meal/suppliers/{Supplier}/packages", request.RequestUri.AbsolutePath);
        var query = request.RequestUri.Query;
        Assert.Contains($"storeId={Store}", query, StringComparison.Ordinal);
        Assert.Contains("size=50", query, StringComparison.Ordinal);
        Assert.Contains("page=0", query, StringComparison.Ordinal);
        Assert.Contains($"packageModificationEndDate={new DateTimeOffset(WindowEnd).ToUnixTimeMilliseconds()}", query, StringComparison.Ordinal);

        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKey}:{ApiSecret}")), request.Headers.Authorization?.Parameter);
        Assert.Equal(AgentName, Assert.Single(request.Headers.GetValues("x-agentname")));
        Assert.Equal(ExecutorEmail, Assert.Single(request.Headers.GetValues("x-executor-user")));
        Assert.Equal($"{Supplier} - {AgentName}", string.Join(' ', request.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task Fetch_PackageWithNumericLineItemIds_Deserializes()
    {
        var orders = await FetchAsync();

        var order = Assert.Single(orders);
        Assert.Equal(PackageId, order.ExternalOrderId);
        Assert.Equal([2, 1], order.Items.Select(i => i.Quantity));
    }

    [Fact]
    public async Task Fetch_MapsTheDocumentedPackageShape()
    {
        var order = Assert.Single(await FetchAsync());

        Assert.Equal(FoodPlatform.TrendyolYemek, order.Platform);
        Assert.Equal(PackageId, order.ExternalOrderId);
        Assert.Equal("9876543210", order.ExternalOrderCode);
        Assert.Equal("Created", order.ExternalStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792141200000).UtcDateTime, order.OrderedAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792141260000).UtcDateTime, order.ModifiedAtUtc);
        Assert.Equal("Deneme Musteri", order.CustomerName);
        Assert.Equal("5000000000", order.CustomerPhone);
        Assert.Equal("Kontrat Sokak 1, 9 kat 2 daire, Moda, İstanbul", order.CustomerAddress);
        Assert.Equal("Zile basmayın", order.CustomerNote);
        Assert.Equal(PaymentMethod.Cash, order.PaymentMethod);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(320m, order.Total);
        Assert.Equal(320m, order.Subtotal);

        Assert.Collection(
            order.Items,
            menu =>
            {
                Assert.Equal("Kontrat Menü", menu.ProductName);
                Assert.Equal(2, menu.Quantity); // the cancelled item is not counted
                Assert.Equal(150m, menu.UnitPrice);
                Assert.Equal(300m, menu.TotalPrice);
                Assert.Equal(
                    [
                        new ExternalOrderItemOptionDto("Burger > Acılı", 0m),
                        new ExternalOrderItemOptionDto("Burger > Cheddar", 10m),
                        new ExternalOrderItemOptionDto("Burger > Soğan (çıkarıldı)", 0m),
                        new ExternalOrderItemOptionDto("Kola", 0m)
                    ],
                    menu.Options);
            },
            ayran =>
            {
                Assert.Equal("Ayran", ayran.ProductName);
                Assert.Equal(1, ayran.Quantity);
                Assert.Equal(20m, ayran.TotalPrice);
                Assert.Empty(ayran.Options);
            });
    }

    [Fact]
    public async Task Fetch_ReadsTheReportedSupplierAndStoreAsText()
    {
        var order = Assert.Single(await FetchAsync());

        // Numbers in the documented model; ingestion compares them with the connection (WAS-18).
        Assert.Equal(Supplier, order.ProviderSupplierId);
        Assert.Equal(Store, order.ProviderStoreId);
    }

    [Theory]
    [InlineData("\"900001\"", "\" 900002 \"", Supplier, Store)]
    [InlineData("null", "{ \"id\": 900002 }", null, null)]
    [InlineData("[900001]", "\"\"", null, null)]
    public async Task Fetch_IdentifiersOfAnotherJsonShape_DoNotFailThePage(string supplierJson, string storeJson, string? supplier, string? store)
    {
        var page = Page
            .Replace($"\"supplierId\": {Supplier},", $"\"supplierId\": {supplierJson},", StringComparison.Ordinal)
            .Replace($"\"storeId\": {Store},", $"\"storeId\": {storeJson},", StringComparison.Ordinal);
        Assert.NotEqual(Page, page);

        var order = Assert.Single(await FetchAsync(page));

        Assert.Equal(PackageId, order.ExternalOrderId);
        Assert.Equal(supplier, order.ProviderSupplierId);
        Assert.Equal(store, order.ProviderStoreId);
    }

    [Fact]
    public async Task Fetch_PackageWithoutSupplierOrStore_LeavesThemUnknown()
    {
        var page = Page
            .Replace($"\"supplierId\": {Supplier},", "", StringComparison.Ordinal)
            .Replace($"\"storeId\": {Store},", "", StringComparison.Ordinal);

        var order = Assert.Single(await FetchAsync(page));

        Assert.Null(order.ProviderSupplierId);
        Assert.Null(order.ProviderStoreId);
    }

    [Fact]
    public async Task Fetch_LogsNoCredentialsOrCustomerData()
    {
        await FetchAsync();

        string[] forbidden = [ApiKey, ApiSecret, ExecutorEmail, "Deneme", "Musteri", "5000000000", "Kontrat Sokak", "Zile basmayın"];
        foreach (var entry in _clientLog.Entries)
        {
            var text = entry.Message + "\n" + entry.Exception;
            foreach (var value in forbidden)
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    private async Task<IReadOnlyCollection<ExternalOrderDto>> FetchAsync(string? page = null)
    {
        var handler = new DelegateHandler(request =>
        {
            _requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page ?? Page, Encoding.UTF8, "application/json") };
        });
        var options = new TrendyolGoOptions { AgentName = AgentName, BaseUrl = "https://stageapi.tgoapis.com" };
        var client = new TrendyolGoFoodPlatformClient(
            new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(new CollectingLogger<DefaultOrderStatusMapper>()),
            Options.Create(options),
            _clientLog,
            new LimiterClock().Limiter(),
            TimeProvider.System);

        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            SupplierId = Supplier,
            StoreId = Store,
            ExecutorEmail = ExecutorEmail,
            EncryptedApiKey = ApiKey,
            EncryptedApiSecret = ApiSecret
        };

        return await client.FetchOrdersAsync(
            connection,
            new OrderFetchWindow(WindowEnd.AddHours(-1), WindowEnd),
            TestContext.Current.CancellationToken);
    }
}
