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
            configuration["Platform:TrendyolGo:BaseUrl"] = "https://stageapi.tgoapis.com";
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
