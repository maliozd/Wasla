using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The Trendyol GO Meal webhook stage pilot's receiver through the real Wasla.Api pipeline (tenant resolution, Print
/// Bridge middleware, authentication and authorization), over SQLite tenant databases. Every value is synthetic; the
/// payload follows the documented "created" sample.
/// </summary>
public sealed class TrendyolGoMealWebhookReceiverTests : IAsyncLifetime
{
    private const string Secret = "synthetic-integrator-secret-for-tests-0123456789";
    private const string HeaderName = "X-Wasla-Webhook-Key";
    private const string Seller = "107385";
    private const string Store = "1331";
    private const string Path = "/api/webhooks/trendyol-go/meal/";

    private DirectoryInfo _keys = null!;
    private TenantSessionWebHost _web = null!;
    private TenantSessionApiHost _api = null!;
    private Guid _connectionId;

    public ValueTask InitializeAsync()
    {
        _keys = Directory.CreateTempSubdirectory("wasla-meal-webhook-keys-");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
        if (_web is not null)
            await _web.DisposeAsync();
        try { _keys.Delete(recursive: true); } catch (IOException) { }
    }

    private async Task StartAsync(bool enabled)
    {
        _web = await TenantSessionWebHost.StartAsync(_keys);
        _connectionId = Guid.NewGuid();
        var configuration = new Dictionary<string, string?>();
        if (enabled)
        {
            configuration["TrendyolGoMealWebhookPilot:Enabled"] = "true";
            configuration["TrendyolGoMealWebhookPilot:TenantId"] = _web.AlphaId.ToString();
            configuration["TrendyolGoMealWebhookPilot:PlatformConnectionId"] = _connectionId.ToString();
            configuration["TrendyolGoMealWebhookPilot:SellerId"] = Seller;
            configuration["TrendyolGoMealWebhookPilot:StoreId"] = Store;
            configuration["TrendyolGoMealWebhookPilot:HeaderName"] = HeaderName;
            configuration["TrendyolGoMealWebhookPilot:SecretSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Secret)));
            configuration["TrendyolGoMealWebhookPilot:MaxBodyBytes"] = "4096";
            configuration["TrendyolGoMealWebhookPilot:ReceiverBaseUrl"] = "https://app.wasla.test/api/webhooks/trendyol-go/meal";
            configuration["TrendyolGoMealWebhookPilot:ProviderApiBaseUrl"] = "https://stageapi.tgoapis.com";
            // The deployment keeps the production API; only the pilot connection is routed to the stage API.
            configuration["Platform:TrendyolGo:BaseUrl"] = "https://api.tgoapis.com";
        }

        _api = await TenantSessionApiHost.StartAsync(_web, _keys, configuration);
    }

    [Fact]
    public async Task PilotOff_AnswersNotFound_AndStoresNothing()
    {
        await StartAsync(enabled: false);

        var response = await PostAsync("created", Created("pkg-off"), Secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await EventsAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    public async Task WithoutTheIntegratorSecret_AnswersUnauthorized_AndStoresNothing(string? secret)
    {
        await StartAsync(enabled: true);

        var response = await PostAsync("created", Created("pkg-auth"), secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await EventsAsync());
        Assert.DoesNotContain(_api.Logs.Entries, e => e.Contains(Secret, StringComparison.Ordinal) || e.Contains("wrong-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonJsonOrOversizedOrMalformedBodies_AreRefused_AndNothingIsStored()
    {
        await StartAsync(enabled: true);

        var text = await PostAsync("created", Created("pkg-type"), Secret, "text/plain");
        var tooLarge = await PostAsync("created", "{\"id\":\"pkg-large\",\"pad\":\"" + new string('x', 5000) + "\"}", Secret);
        var noId = await PostAsync("created", "{\"orderNumber\":\"1\"}", Secret);
        var notJson = await PostAsync("created", "{not json", Secret);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task CreatedEvent_IsStoredBeforeTheAnswer_OnAnyHost_AndRepeatedDeliveriesShareOneEvent()
    {
        await StartAsync(enabled: true);

        // The pilot does not resolve a tenant from the host; a host that is no tenant still reaches the receiver.
        var first = await PostAsync("created", Created("pkg-created"), Secret, host: "hooks.example.invalid");
        var stored = Assert.Single(await EventsAsync());
        var second = await PostAsync("created", Created("pkg-created"), Secret);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var again = Assert.Single(await EventsAsync());
        Assert.Equal(stored.Id, again.Id);
        Assert.Equal(2, again.DeliveryCount);
        Assert.Equal("created", again.EventType);
        Assert.Equal("pkg-created", again.ExternalOrderId);
        Assert.Equal(ProviderWebhookEventStatus.Pending, again.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1734951413945).UtcDateTime, again.EventTimestampUtc);
        // The payload stays in the tenant database; no log carries it.
        Assert.DoesNotContain(_api.Logs.Entries, e => e.Contains("Gündoğdu", StringComparison.Ordinal) || e.Contains("Oms", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("storeChanged", "UndocumentedEventType")]
    [InlineData("courierNearby", "NoOrderChange")]
    [InlineData("pickupEtaCalculated", "NoOrderChange")]
    public async Task EventsWaslaDoesNotActOn_AreStoredAsIgnored(string eventType, string outcome)
    {
        await StartAsync(enabled: true);

        var response = await PostAsync(eventType, "{\"id\":\"pkg-info\",\"orderNumber\":\"1\",\"timestamp\":1734952681000}", Secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await EventsAsync());
        Assert.Equal(ProviderWebhookEventStatus.Ignored, stored.Status);
        Assert.Equal(outcome, stored.Outcome);
    }

    [Theory]
    [InlineData("999999", Store, "SellerMismatch")]
    [InlineData(Seller, "4242", "StoreMismatch")]
    public async Task CreatedEventForAnotherSellerOrStore_IsStoredAsRejected(string seller, string store, string outcome)
    {
        await StartAsync(enabled: true);

        var response = await PostAsync("created", Created("pkg-foreign", seller, store), Secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await EventsAsync());
        Assert.Equal(ProviderWebhookEventStatus.Rejected, stored.Status);
        Assert.Equal(outcome, stored.Outcome);
    }

    [Theory]
    [InlineData("9223372036854775807")]   // long.MaxValue: past what a date can hold
    [InlineData("253402300800000")]       // one millisecond past 9999-12-31T23:59:59.999Z
    [InlineData("253402300799999")]       // the last representable instant: far in the future
    [InlineData("32503680000000")]        // the year 3000
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("946684799999")]          // just before 2000
    [InlineData("1e30")]                  // not a 64-bit integer
    [InlineData("1734951413945.5")]
    [InlineData("\"1734951413945\"")]     // a string, not a number
    [InlineData("null")]
    public async Task InvalidOrImplausibleTimestamp_IsStoredWithoutAnEventTime_NeverAServerError(string timestamp)
    {
        await StartAsync(enabled: true);
        var body = Created("pkg-time").Replace("\"timestamp\": 1734951413945", $"\"timestamp\": {timestamp}", StringComparison.Ordinal);

        var first = await PostAsync("created", body, Secret);
        var retry = await PostAsync("created", body, Secret);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        // Still one event for every delivery of it, and processed with its receipt time instead.
        var stored = Assert.Single(await EventsAsync());
        Assert.Equal(2, stored.DeliveryCount);
        Assert.Null(stored.EventTimestampUtc);
        Assert.Equal(ProviderWebhookEventStatus.Pending, stored.Status);
    }

    [Fact]
    public void EventTime_AcceptsOnlyInstantsFrom2000UntilTheProviderClockTolerance()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var tolerance = (long)Wasla.Infrastructure.Sync.OrderIngestion.ProviderClockTolerance.TotalMilliseconds;

        Assert.Equal(now, EventTime(nowMs));
        Assert.Equal(now.AddMilliseconds(tolerance), EventTime(nowMs + tolerance));
        Assert.Null(EventTime(nowMs + tolerance + 1));
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), EventTime(946684800000));
        Assert.Null(EventTime(946684799999));
        Assert.Null(EventTime(null));
        Assert.Null(EventTime(0));
        Assert.Null(EventTime(-1));
        Assert.Null(EventTime(long.MinValue));
        Assert.Null(EventTime(long.MaxValue));
        Assert.Null(EventTime(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
        Assert.Null(EventTime(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1));

        DateTime? EventTime(long? ms) => Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookInbox.EventTime(ms, now);
    }

    private async Task<List<ProviderWebhookEvent>> EventsAsync()
    {
        await using var db = await _web.Tenants.CreateAsync(_web.AlphaId, TestContext.Current.CancellationToken);
        return await db.ProviderWebhookEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string eventType,
        string body,
        string? secret,
        string contentType = "application/json",
        string host = "alpha.app.wasla.test")
    {
        using var http = new HttpClient { BaseAddress = _api.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, Path + eventType)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        request.Headers.Host = host;
        if (secret is not null)
            request.Headers.TryAddWithoutValidation(HeaderName, secret);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The documented "created" sample's shape, with synthetic values.</summary>
    internal static string Created(string packageId, string seller = Seller, string store = Store) => $$"""
        {
          "id": "{{packageId}}",
          "supplierId": {{seller}},
          "storeId": {{store}},
          "orderCode": "047",
          "orderId": "1000946511491",
          "orderNumber": "946511491",
          "deliveryType": "STORE",
          "timestamp": 1734951413945,
          "totalPrice": 173.93,
          "packageStatus": "Created",
          "customer": { "id": 1, "firstName": "Oms", "lastName": "M.", "email": "synthetic@example.invalid" },
          "payment": { "paymentType": "PAY_WITH_CARD" },
          "address": { "address1": "Gündoğdu Koleji Yanı", "city": "İstanbul", "phone": "0000000000" },
          "lines": [ { "price": 9.99, "unitSellingPrice": 9.99, "name": "Synthetic drink", "quantity": 1,
                       "items": [ { "packageItemId": "1", "lineItemId": 1000004632216, "isCancelled": false } ],
                       "modifierProducts": [], "extraIngredients": [], "removedIngredients": [] } ],
          "testPackage": true
        }
        """;
}
