using System.Net;
using System.Text;
using System.Text.Json;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The stage pilot's integrator client against a fake Trendyol GO stage API, in the documented shape of "Webhook Meal
/// Integration" (checked 2026-10-26). Credentials, tokens and secrets are synthetic.
/// </summary>
public sealed class TrendyolGoMealIntegratorClientTests
{
    private const string ApiKey = "fake-seller-key";
    private const string ApiSecret = "fake-seller-secret";
    private const string Token = "fake-integrator-token";
    private const string HeaderSecret = "fake-header-secret";

    private readonly List<(HttpRequestMessage Request, string? Body)> _requests = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TrendyolGoMealIntegratorClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string baseUrl = "https://stageapi.tgoapis.com")
    {
        var handler = new DelegateHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            _requests.Add((request, body));
            return respond(request);
        });
        return new TrendyolGoMealIntegratorClient(
            new HttpClient(handler) { BaseAddress = new Uri(baseUrl) },
            "WaslaTest",
            "executor@example.invalid",
            "107385",
            ApiKey,
            ApiSecret);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("https://api.tgoapis.com")]
    [InlineData("http://stageapi.tgoapis.com")]
    [InlineData("https://stageapi.tgoapis.com/integrator")]
    [InlineData("https://stageapi.tgoapis.com:8443")]
    [InlineData("https://user:pass@stageapi.tgoapis.com")]
    [InlineData("https://stageapi.tgoapis.com/?x=1")]
    [InlineData("https://stageapi.tgoapis.com/#x")]
    public void AnythingButExactlyTheStageApi_IsRefused(string baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => Client(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl));
    }

    [Fact]
    public async Task Create_PostsTheDocumentedBody_WithTheSellersBasicCredentials_AndReturnsTheToken()
    {
        var client = Client(_ => Json(HttpStatusCode.Created, $$"""{ "token": "{{Token}}" }"""));

        var token = await client.CreateAsync(
            "WaslaStagePilot",
            new Uri("https://hooks.example.invalid/api/webhooks/trendyol-go/meal/"),
            5000,
            TrendyolGoMealWebhookEvents.Documented,
            "X-Wasla-Webhook-Key",
            HeaderSecret,
            Ct);

        Assert.Equal(Token, token);
        var (request, body) = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/integrator/order/meal/webhook/create", request.RequestUri!.AbsolutePath);
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKey}:{ApiSecret}")), request.Headers.Authorization?.Parameter);
        Assert.False(request.Headers.Contains("Authorization-int"));
        Assert.Equal("WaslaTest", Assert.Single(request.Headers.GetValues("x-agentname")));
        Assert.Equal("executor@example.invalid", Assert.Single(request.Headers.GetValues("x-executor-user")));
        Assert.Equal("107385 - WaslaTest", string.Join(' ', request.Headers.GetValues("User-Agent")));

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("WaslaStagePilot", root.GetProperty("name").GetString());
        Assert.Equal("https://hooks.example.invalid/api/webhooks/trendyol-go/meal", root.GetProperty("httpConfigs").GetProperty("baseUrl").GetString());
        Assert.Equal(5000, root.GetProperty("httpConfigs").GetProperty("timeout").GetInt32());
        Assert.Equal(107385, root.GetProperty("sellerId").GetInt64());
        var events = root.GetProperty("eventTypes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("destinationUrl").GetString());
        Assert.Equal(TrendyolGoMealWebhookEvents.Documented.Order(), events.Keys.Order());
        Assert.All(events, e => Assert.Equal("/" + e.Key, e.Value));
        Assert.Equal(HeaderSecret, root.GetProperty("secrets").GetProperty("httpHeaders").GetProperty("X-Wasla-Webhook-Key").GetString());
        Assert.False(root.GetProperty("secrets").TryGetProperty("httpQueryParams", out _));
    }

    [Fact]
    public async Task Get_UsesTheIntegratorToken_AndNeverReadsTheIntegratorsSecrets()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, $$"""
            {
              "httpConfigs": { "baseUrl": "https://hooks.example.invalid/api/webhooks/trendyol-go/meal", "timeout": 5000 },
              "sellerIds": [107385],
              "eventTypes": { "created": { "destinationUrl": "/created" } },
              "secrets": { "httpHeaders": { "X-Wasla-Webhook-Key": "{{HeaderSecret}}" } },
              "lastModifiedDate": 1734951413945,
              "enabled": false
            }
            """));

        var view = await client.GetAsync("WaslaStagePilot", Token, Ct);

        var (request, _) = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/integrator/order/meal/webhook/WaslaStagePilot", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer " + Token, Assert.Single(request.Headers.GetValues("Authorization-int")));
        Assert.Null(request.Headers.Authorization);
        Assert.Equal([107385L], view.SellerIds);
        Assert.False(view.Enabled);
        Assert.Equal("/created", view.EventDestinations["created"]);
        Assert.DoesNotContain(HeaderSecret, view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableDisableAndTestOrder_UseTheDocumentedPaths()
    {
        var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/order/test", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Created, """{ "orderNumber": "123456" }""")
            : new HttpResponseMessage(HttpStatusCode.OK));

        await client.EnableAsync("WaslaStagePilot", Token, Ct);
        await client.DisableAsync("WaslaStagePilot", Token, Ct);
        var orderNumber = await client.CreateTestOrderAsync("WaslaStagePilot", Token, "1331", Ct);

        Assert.Equal("123456", orderNumber);
        Assert.Equal(
            ["PATCH /integrator/order/meal/webhook/WaslaStagePilot/enable", "PATCH /integrator/order/meal/webhook/WaslaStagePilot/disable", "POST /integrator/order/meal/webhook/WaslaStagePilot/order/test"],
            _requests.Select(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}"));
        var (testOrder, body) = _requests[2];
        Assert.Equal("Basic", testOrder.Headers.Authorization?.Scheme);
        Assert.Equal("Bearer " + Token, Assert.Single(testOrder.Headers.GetValues("Authorization-int")));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(1331, json.RootElement.GetProperty("storeId").GetInt64());
        Assert.Equal(107385, json.RootElement.GetProperty("sellerId").GetInt64());
    }

    [Fact]
    public async Task RefusedCall_ReportsTheStatus_WithCredentialsAndSecretsRemoved()
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKey}:{ApiSecret}"));
        var client = Client(_ => Json(HttpStatusCode.BadRequest, $$"""{ "errors": [ "echo {{ApiKey}} {{ApiSecret}} {{basic}} {{HeaderSecret}}" ] }"""));

        var error = await Assert.ThrowsAsync<TrendyolGoMealIntegratorException>(() => client.CreateAsync(
            "WaslaStagePilot", new Uri("https://hooks.example.invalid/x"), 5000, ["created"], "X-Key", HeaderSecret, Ct));

        Assert.Equal(400, error.StatusCode);
        foreach (var secret in new[] { ApiKey, ApiSecret, basic, HeaderSecret })
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }
}
