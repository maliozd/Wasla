using System.Net;
using System.Text;
using System.Text.Json;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The pilot's integrator expectations fail closed: exactly the stage API, exactly one receiver URL shape, and an
/// integrator enabled (or asked for a test order) only while Get Integrator reports exactly the expected registration
/// for exactly the configured seller. Every value is synthetic.
/// </summary>
public sealed class TrendyolGoMealIntegratorExpectationTests
{
    private const string Seller = "107385";
    private const string Receiver = "https://app.wasla.example/api/webhooks/trendyol-go/meal";
    private const string Token = "fake-integrator-token";
    private const string Name = "WaslaMealPilot";

    private static readonly TrendyolGoMealWebhookPilotOptions Pilot = new() { SellerId = Seller, StoreId = "1331" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://stageapi.tgoapis.com", true)]
    [InlineData("https://stageapi.tgoapis.com/", true)]
    [InlineData("HTTPS://StageAPI.tgoapis.com", true)]
    [InlineData("https://api.tgoapis.com", false)]
    [InlineData("http://stageapi.tgoapis.com", false)]
    [InlineData("https://stageapi.tgoapis.com:443", false)]
    [InlineData("https://stageapi.tgoapis.com:8443", false)]
    [InlineData("https://stageapi.tgoapis.com/integrator", false)]
    [InlineData("https://stageapi.tgoapis.com//", false)]
    [InlineData("https://stageapi.tgoapis.com?x=1", false)]
    [InlineData("https://stageapi.tgoapis.com/#fragment", false)]
    [InlineData("https://user:pass@stageapi.tgoapis.com", false)]
    [InlineData("https://stageapi.tgoapis.com.evil.example", false)]
    [InlineData(" https://stageapi.tgoapis.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProviderApi_MustBeExactlyTheStageApi(string? baseUrl, bool accepted)
    {
        Assert.Equal(accepted, TrendyolGoMealIntegratorExpectation.IsStageApiBaseUrl(baseUrl));
    }

    [Theory]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal", Receiver)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal/", Receiver)]
    [InlineData("https://APP.Wasla.Example/api/webhooks/trendyol-go/meal", Receiver)]
    [InlineData("https://hooks.app.wasla.example/api/webhooks/trendyol-go/meal", "https://hooks.app.wasla.example/api/webhooks/trendyol-go/meal")]
    [InlineData("http://app.wasla.example/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal#created", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal/#", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal?key=1", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal/?", null)]
    [InlineData("https://user@app.wasla.example/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://user:pass@app.wasla.example/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://app.wasla.example:8443/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://app.wasla.example:443/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://203.0.113.10/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://localhost/api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/Meal", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal/created", null)]
    [InlineData("https://app.wasla.example/x/../api/webhooks/trendyol-go/meal", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal//", null)]
    [InlineData("https://app.wasla.example/api/webhooks/trendyol-go/meal ", null)]
    [InlineData("app.wasla.example/api/webhooks/trendyol-go/meal", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReceiverUrl_MustBeExactlyHttpsHostPlusTheReceiverPath(string? value, string? canonical)
    {
        Assert.Equal(canonical, TrendyolGoMealIntegratorExpectation.CanonicalReceiverBaseUrl(value));
    }

    [Fact]
    public void ReceiverUrl_MustStayUnderTheProvidersHundredCharacters()
    {
        var host = new string('a', 60) + ".wasla.example";
        var url = $"https://{host}/api/webhooks/trendyol-go/meal";
        Assert.True(url.Length >= 100);

        Assert.Null(TrendyolGoMealIntegratorExpectation.CanonicalReceiverBaseUrl(url));
    }

    [Fact]
    public void ExactRegistration_HasNoDifferences()
    {
        Assert.Empty(TrendyolGoMealIntegratorExpectation.Differences(Parse(Body()), Receiver, Pilot));
    }

    public static TheoryData<string, string> RegistrationsThatDiffer => new()
    {
        { """{ "httpConfigs": { "baseUrl": "https://other.wasla.example/api/webhooks/trendyol-go/meal", "timeout": 5000 } }""", "is not the configured receiver" },
        { """{ "httpConfigs": { "baseUrl": "https://app.wasla.example/api/webhooks/trendyol-go/meal#x", "timeout": 5000 } }""", "without user info, port, query or fragment" },
        { """{ "httpConfigs": { "baseUrl": "https://app.wasla.example/api/webhooks/trendyol-go/meal?k=v", "timeout": 5000 } }""", "without user info, port, query or fragment" },
        { """{ "httpConfigs": { "baseUrl": "https://u:p@app.wasla.example/api/webhooks/trendyol-go/meal", "timeout": 5000 } }""", "without user info, port, query or fragment" },
        { """{ "httpConfigs": { "baseUrl": null, "timeout": 5000 } }""", "(none)" },
        { """{ "httpConfigs": { "baseUrl": "https://app.wasla.example/api/webhooks/trendyol-go/meal", "timeout": 3000 } }""", "timeout is 3000" },
        { """{ "sellerIds": [107385, 107386] }""", "exactly the configured seller" },
        { """{ "sellerIds": [999999] }""", "exactly the configured seller" },
        { """{ "sellerIds": [] }""", "exactly the configured seller" },
        { """{ "drop": "shipped" }""", "Event shipped is not registered" },
        { """{ "destination": ["cancelled", "/created"] }""", "Event cancelled goes to \"/created\"" },
        { """{ "destination": ["delivered", "/delivered?x=1"] }""", "Event delivered goes to" },
        { """{ "destination": ["created", "https://elsewhere.example/created"] }""", "Event created goes to" },
        { """{ "extra": "storeChanged" }""", "Event \"storeChanged\" is registered but is not a documented Meal event" }
    };

    [Theory]
    [MemberData(nameof(RegistrationsThatDiffer))]
    public void RegistrationThatDiffersInAnyWay_IsReported(string change, string expected)
    {
        var differences = TrendyolGoMealIntegratorExpectation.Differences(Parse(Body(change)), Receiver, Pilot);

        Assert.Contains(differences, d => d.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_WithTheExactRegistration_EnablesAndReadsItBack()
    {
        var api = new FakeIntegratorApi(Body());

        var (result, check) = await TrendyolGoMealIntegratorOperations.EnableAsync(api.Client, Name, Token, Receiver, Pilot, Ct);

        Assert.Equal(TrendyolGoMealIntegratorEnableResult.Enabled, result);
        Assert.True(check.View.Enabled);
        Assert.Equal(["GET", "PATCH enable", "GET"], api.Calls);
    }

    [Theory]
    [MemberData(nameof(RegistrationsThatDiffer))]
    public async Task Enable_WithADifferentRegistration_NeverEnables(string change, string expected)
    {
        var api = new FakeIntegratorApi(Body(change));

        var (result, check) = await TrendyolGoMealIntegratorOperations.EnableAsync(api.Client, Name, Token, Receiver, Pilot, Ct);

        Assert.Equal(TrendyolGoMealIntegratorEnableResult.RefusedBeforeEnabling, result);
        Assert.Contains(check.Differences, d => d.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(["GET"], api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enable_ThatDoesNotReadBackAsExpected_IsDisabledAgain(bool readsBackChanged)
    {
        // The provider either ignores the enable call or changes the registration while enabling it.
        var api = new FakeIntegratorApi(Body(), readsBackChanged ? Body("""{ "extra": "storeChanged" }""", enabled: true) : Body(enabled: false));

        var (result, _) = await TrendyolGoMealIntegratorOperations.EnableAsync(api.Client, Name, Token, Receiver, Pilot, Ct);

        Assert.Equal(TrendyolGoMealIntegratorEnableResult.DisabledAgain, result);
        Assert.Equal(["GET", "PATCH enable", "GET", "PATCH disable"], api.Calls);
    }

    [Fact]
    public async Task TestOrder_IsRequestedOnlyForAnEnabledExactRegistration()
    {
        var disabled = new FakeIntegratorApi(Body(enabled: false));
        var different = new FakeIntegratorApi(Body("""{ "sellerIds": [999999] }""", enabled: true));
        var exact = new FakeIntegratorApi(Body(enabled: true));

        var (_, _, requestedWhileDisabled) = await TrendyolGoMealIntegratorOperations.RequestTestOrderAsync(disabled.Client, Name, Token, Receiver, Pilot, Ct);
        var (_, _, requestedWhileDifferent) = await TrendyolGoMealIntegratorOperations.RequestTestOrderAsync(different.Client, Name, Token, Receiver, Pilot, Ct);
        var (orderNumber, _, requested) = await TrendyolGoMealIntegratorOperations.RequestTestOrderAsync(exact.Client, Name, Token, Receiver, Pilot, Ct);

        Assert.False(requestedWhileDisabled);
        Assert.False(requestedWhileDifferent);
        Assert.Equal(["GET"], disabled.Calls);
        Assert.Equal(["GET"], different.Calls);
        Assert.True(requested);
        Assert.Equal("123456", orderNumber);
        Assert.Equal(["GET", "POST test-order"], exact.Calls);
    }

    /// <summary>
    /// Get Integrator's body for the exact expected registration, with one change applied: an object that replaces
    /// <c>httpConfigs</c> or <c>sellerIds</c>, or names an event to <c>drop</c>, an <c>extra</c> one, or a
    /// <c>destination</c> pair to change. The integrator's secrets are in the body, as the API reports them.
    /// </summary>
    private static string Body(string change = "{}", bool enabled = false)
    {
        using var changes = JsonDocument.Parse(change);
        var c = changes.RootElement;

        var events = TrendyolGoMealWebhookEvents.Documented.ToDictionary(t => t, t => "/" + t);
        if (c.TryGetProperty("drop", out var drop))
            events.Remove(drop.GetString()!);
        if (c.TryGetProperty("extra", out var extra))
            events[extra.GetString()!] = "/" + extra.GetString();
        if (c.TryGetProperty("destination", out var destination))
            events[destination[0].GetString()!] = destination[1].GetString()!;

        var httpConfigs = c.TryGetProperty("httpConfigs", out var h) ? h.GetRawText() : $$"""{ "baseUrl": "{{Receiver}}", "timeout": 5000 }""";
        var sellerIds = c.TryGetProperty("sellerIds", out var s) ? s.GetRawText() : $"[{Seller}]";
        var eventTypes = JsonSerializer.Serialize(events.ToDictionary(e => e.Key, e => new { destinationUrl = e.Value }));
        return $$"""
            {
              "httpConfigs": {{httpConfigs}},
              "sellerIds": {{sellerIds}},
              "eventTypes": {{eventTypes}},
              "secrets": { "httpHeaders": { "X-Wasla-Webhook-Key": "not-read" } },
              "lastModifiedDate": 9223372036854775807,
              "enabled": {{(enabled ? "true" : "false")}}
            }
            """;
    }

    /// <summary>The view the integrator client reads from a Get Integrator body.</summary>
    private static TrendyolGoMealIntegratorView Parse(string body) =>
        new FakeIntegratorApi(body).Client.GetAsync(Name, Token, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// A fake stage API: Get Integrator answers <paramref name="before"/> until enable is called, then
    /// <paramref name="afterEnable"/> (by default the same registration, reported enabled).
    /// </summary>
    private sealed class FakeIntegratorApi(string before, string? afterEnable = null)
    {
        private bool _enabled;

        public List<string> Calls { get; } = [];

        public TrendyolGoMealIntegratorClient Client => new(
            new HttpClient(new DelegateHandler(Respond)) { BaseAddress = new Uri("https://stageapi.tgoapis.com") },
            "WaslaTest",
            "executor@example.invalid",
            Seller,
            "fake-seller-key",
            "fake-seller-secret");

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                Calls.Add("GET");
                var body = !_enabled ? before : afterEnable ?? before.Replace("\"enabled\": false", "\"enabled\": true", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            if (path.EndsWith("/enable", StringComparison.Ordinal))
            {
                Calls.Add("PATCH enable");
                _enabled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.EndsWith("/disable", StringComparison.Ordinal))
            {
                Calls.Add("PATCH disable");
                _enabled = false;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Calls.Add("POST test-order");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{ "orderNumber": "123456" }""", Encoding.UTF8, "application/json")
            };
        }
    }
}
