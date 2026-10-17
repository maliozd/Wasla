using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.Mock;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-125: each provider client reports the order's creation time exactly as the provider sent it, or null. None of
/// them substitutes the server's clock, which would turn an import time into an order time. Synthetic payloads only.
/// </summary>
public sealed class ProviderOrderTimeMappingTests
{
    private const long CreatedMs = 1792172400000; // 2026-10-16T17:40:00Z: 20:40 in Turkey
    private const long ModifiedMs = 1792175400000; // 2026-10-16T18:30:00Z

    [Fact]
    public async Task TrendyolGo_MapsPackageCreationAndModificationDates()
    {
        var order = Assert.Single(await FetchTrendyolAsync($$"""{ "packageCreationDate": {{CreatedMs}}, "packageModificationDate": {{ModifiedMs}} }"""));

        Assert.Equal(new DateTime(2026, 10, 16, 17, 40, 0, DateTimeKind.Utc), order.OrderedAtUtc);
        Assert.Equal(DateTimeKind.Utc, order.OrderedAtUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 16, 18, 30, 0, DateTimeKind.Utc), order.ModifiedAtUtc);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "packageCreationDate": null, "packageModificationDate": null }""")]
    [InlineData("""{ "packageCreationDate": 0, "packageModificationDate": -1 }""")]
    public async Task TrendyolGo_MissingOrNonPositiveDates_StayNull(string dates)
    {
        var order = Assert.Single(await FetchTrendyolAsync(dates));

        Assert.Null(order.OrderedAtUtc);
        Assert.Null(order.ModifiedAtUtc);
    }

    [Theory]
    [InlineData("1792172400000", "2026-10-16T17:40:00Z")]
    [InlineData("2026-10-16T20:40:00+03:00", "2026-10-16T17:40:00Z")]
    [InlineData("2026-10-16T17:40:00Z", "2026-10-16T17:40:00Z")]
    public void Yemeksepeti_ReadsInstantsOnly(string raw, string expectedUtc)
    {
        var parsed = YemeksepetiFoodPlatformClient.TryParseDateTime(raw);

        Assert.Equal(DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("not a date")]
    // No offset: reading it in the server's zone would shift it, so it is not taken as an instant.
    [InlineData("2026-10-16 20:40:00")]
    public void Yemeksepeti_MissingOrAmbiguousDates_StayNull_NeverTheServerClock(string? raw)
    {
        Assert.Null(YemeksepetiFoodPlatformClient.TryParseDateTime(raw));
    }

    [Fact]
    public void MockOrders_NewOrdersWerePlacedMomentsAgo_UpdatesSendNoCreationTime()
    {
        var connection = new PlatformConnection { Platform = FoodPlatform.TrendyolYemek, StoreId = "mock-store" };
        var before = DateTime.UtcNow;

        var orders = MockOrders.CreateOrders(FoodPlatform.TrendyolYemek, connection, 60, new Random(125));

        Assert.Contains(orders, o => o.OrderedAtUtc is not null);
        foreach (var order in orders.Where(o => o.OrderedAtUtc is not null))
            Assert.InRange(order.OrderedAtUtc!.Value, before.AddMinutes(-2).AddSeconds(-1), DateTime.UtcNow);
    }

    private static async Task<IReadOnlyCollection<ExternalOrderDto>> FetchTrendyolAsync(string dates)
    {
        var dateFields = dates.Trim().TrimStart('{').TrimEnd('}').Trim();
        var package = $$"""
            {
              "id": "pkg-1",
              "orderNumber": "1234567890",
              "packageStatus": "Delivered",
              "totalPrice": 100,
              "customer": { "firstName": "Deneme", "lastName": "Musteri" },
              "lines": []{{(dateFields.Length > 0 ? ", " + dateFields : string.Empty)}}
            }
            """;
        var page = $$"""{ "page": 0, "size": 50, "totalPages": 1, "totalCount": 1, "content": [ {{package}} ] }""";

        var client = new TrendyolGoFoodPlatformClient(
            new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            }))
            {
                BaseAddress = new Uri("https://trendyol.example/")
            },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            NullLogger<TrendyolGoFoodPlatformClient>.Instance,
            new LimiterClock().Limiter(),
            TimeProvider.System);

        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            SupplierId = "107385",
            StoreId = "153",
            ExecutorEmail = "executor@example.invalid",
            EncryptedApiKey = "fake-key",
            EncryptedApiSecret = "fake-secret"
        };
        var end = new DateTime(2026, 10, 16, 19, 0, 0, DateTimeKind.Utc);
        return await client.FetchOrdersAsync(connection, new OrderFetchWindow(end.AddHours(-1), end), TestContext.Current.CancellationToken);
    }
}
