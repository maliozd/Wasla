using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>What Get Integrator reports, without the integrator's <c>secrets</c>, which are never read.</summary>
public sealed record TrendyolGoMealIntegratorView(
    string? BaseUrl,
    int? TimeoutMs,
    IReadOnlyList<long> SellerIds,
    IReadOnlyDictionary<string, string?> EventDestinations,
    bool Enabled,
    long? LastModifiedDate);

/// <summary>A refused Trendyol GO integrator call: the status code and the provider's error text, with secrets removed.</summary>
public sealed class TrendyolGoMealIntegratorException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The Trendyol GO Meal webhook integrator endpoints ("Webhook Meal Integration", stage URLs; checked 2026-10-26) for the
/// stage pilot's operator: create, get, enable, disable and a webhook test order. Seller calls use the seller's Basic
/// credentials and the <c>User-Agent</c> the API requires; integrator calls use <c>Authorization-int: Bearer</c> with the
/// token Create Integrator returned. Runs only against the stage API. Credentials, the token and the header secret are
/// set per request and removed from any error text.
/// </summary>
public sealed class TrendyolGoMealIntegratorClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _agentName;
    private readonly string _executorEmail;
    private readonly string _sellerId;
    private readonly string _sellerBasic;
    private readonly string[] _redact;

    public TrendyolGoMealIntegratorClient(HttpClient http, string agentName, string executorEmail, string sellerId, string sellerApiKey, string sellerApiSecret)
    {
        if (!TrendyolGoMealIntegratorExpectation.IsStageApiBaseUrl(http.BaseAddress))
            throw new InvalidOperationException($"The integrator client runs only against exactly {TrendyolGoMealIntegratorExpectation.StageApiBaseUrl}.");

        _http = http;
        _agentName = agentName;
        _executorEmail = executorEmail;
        _sellerId = sellerId;
        _sellerBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sellerApiKey}:{sellerApiSecret}"));
        _redact = [sellerApiKey, sellerApiSecret, _sellerBasic];
    }

    /// <summary>Creates the integrator (disabled) for the seller and returns its token.</summary>
    public async Task<string> CreateAsync(
        string integratorName,
        Uri webhookBaseUrl,
        int timeoutMs,
        IEnumerable<string> eventTypes,
        string headerName,
        string headerSecret,
        CancellationToken ct)
    {
        var body = new
        {
            name = integratorName,
            httpConfigs = new { baseUrl = webhookBaseUrl.ToString().TrimEnd('/'), timeout = timeoutMs },
            sellerId = long.Parse(_sellerId, System.Globalization.CultureInfo.InvariantCulture),
            eventTypes = eventTypes.ToDictionary(t => t, t => new { destinationUrl = "/" + t }),
            secrets = new { httpHeaders = new Dictionary<string, string> { [headerName] = headerSecret } }
        };

        using var request = Request(HttpMethod.Post, "/integrator/order/meal/webhook/create", integratorToken: null, seller: true);
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await SendAsync(request, [headerSecret], ct).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>(Json, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(created?.Token)
            ? throw new TrendyolGoMealIntegratorException((int)response.StatusCode, "Create Integrator returned no token.")
            : created.Token;
    }

    public async Task<TrendyolGoMealIntegratorView> GetAsync(string integratorName, string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/integrator/order/meal/webhook/{Uri.EscapeDataString(integratorName)}", token, seller: false);
        using var response = await SendAsync(request, [token], ct).ConfigureAwait(false);
        var view = await response.Content.ReadFromJsonAsync<GetResponse>(Json, ct).ConfigureAwait(false)
                   ?? throw new TrendyolGoMealIntegratorException((int)response.StatusCode, "Get Integrator returned no body.");
        return new TrendyolGoMealIntegratorView(
            view.HttpConfigs?.BaseUrl,
            view.HttpConfigs?.Timeout,
            view.SellerIds ?? [],
            (view.EventTypes ?? []).ToDictionary(e => e.Key, e => e.Value?.DestinationUrl),
            view.Enabled,
            view.LastModifiedDate);
    }

    public Task EnableAsync(string integratorName, string token, CancellationToken ct) =>
        PatchAsync($"/integrator/order/meal/webhook/{Uri.EscapeDataString(integratorName)}/enable", token, ct);

    public Task DisableAsync(string integratorName, string token, CancellationToken ct) =>
        PatchAsync($"/integrator/order/meal/webhook/{Uri.EscapeDataString(integratorName)}/disable", token, ct);

    /// <summary>Asks Trendyol GO to create a webhook test order for the seller's store; returns its order number.</summary>
    public async Task<string?> CreateTestOrderAsync(string integratorName, string token, string storeId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/integrator/order/meal/webhook/{Uri.EscapeDataString(integratorName)}/order/test", token, seller: true);
        request.Content = JsonContent.Create(
            new
            {
                storeId = long.Parse(storeId, System.Globalization.CultureInfo.InvariantCulture),
                sellerId = long.Parse(_sellerId, System.Globalization.CultureInfo.InvariantCulture)
            },
            options: Json);
        using var response = await SendAsync(request, [token], ct).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<TestOrderResponse>(Json, ct).ConfigureAwait(false);
        return created?.OrderNumber;
    }

    private async Task PatchAsync(string path, string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Patch, path, token, seller: false);
        using var response = await SendAsync(request, [token], ct).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? integratorToken, bool seller)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-agentname", _agentName);
        request.Headers.Add("x-executor-user", _executorEmail);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", $"{_sellerId} - {_agentName}");
        if (seller)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _sellerBasic);
        if (integratorToken is not null)
            request.Headers.TryAddWithoutValidation("Authorization-int", "Bearer " + integratorToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string[] alsoRedact, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        response.Dispose();
        foreach (var secret in _redact.Concat(alsoRedact).Where(s => !string.IsNullOrEmpty(s)))
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        throw new TrendyolGoMealIntegratorException(status, $"Trendyol GO answered {status}: {(text.Length <= 300 ? text : text[..300])}");
    }

    private sealed record CreateResponse(string? Token);

    private sealed record TestOrderResponse(string? OrderNumber);

    private sealed class GetResponse
    {
        public HttpConfigsResponse? HttpConfigs { get; set; }
        public List<long>? SellerIds { get; set; }
        public Dictionary<string, EventTypeResponse?>? EventTypes { get; set; }
        public bool Enabled { get; set; }
        public long? LastModifiedDate { get; set; }

        // The integrator's secrets are deliberately not mapped: they are skipped while reading, never kept or printed.
    }

    private sealed record HttpConfigsResponse(string? BaseUrl, int? Timeout);

    private sealed record EventTypeResponse(string? DestinationUrl);
}
