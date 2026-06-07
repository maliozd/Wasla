using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public sealed class OrderHubPrintBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly string _appVersion;

    public OrderHubPrintBridgeClient(
        HttpClient http,
        PrintBridgeSettingsHolder holder,
        string appVersion)
    {
        _http = http;
        _holder = holder;
        _appVersion = appVersion;
    }

    public async Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(CancellationToken ct)
    {
        var (_, bridge) = _holder.Snapshot();
        var max = Math.Clamp(bridge.MaxJobsPerPoll, 1, 10);
        var url = BuildAbsoluteUrl($"api/print-bridge/jobs/pending?max={max}");
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PendingPrintJobsResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return payload?.Jobs ?? [];
    }

    public async Task<PrintJobActionResult> MarkPrintingAsync(Guid jobId, CancellationToken ct)
    {
        var url = BuildAbsoluteUrl($"api/print-bridge/jobs/{jobId:D}/mark-printing");
        using var request = CreateRequest(HttpMethod.Post, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<PrintJobActionResult> MarkPrintedAsync(Guid jobId, CancellationToken ct)
    {
        var url = BuildAbsoluteUrl($"api/print-bridge/jobs/{jobId:D}/mark-printed");
        using var request = CreateRequest(HttpMethod.Post, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<PrintJobActionResult> MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct)
    {
        var url = BuildAbsoluteUrl($"api/print-bridge/jobs/{jobId:D}/mark-failed");
        using var request = CreateRequest(HttpMethod.Post, url);
        request.Content = JsonContent.Create(new { errorMessage });
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    private string BuildAbsoluteUrl(string relativeUrl)
    {
        var (hub, _) = _holder.Snapshot();
        return $"{hub.BaseUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var (hub, bridge) = _holder.Snapshot();
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", hub.AgentToken);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Name", bridge.BridgeName);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Version", _appVersion);
        if (!string.IsNullOrWhiteSpace(bridge.PrinterName))
            request.Headers.TryAddWithoutValidation("X-PrintBridge-Printer", bridge.PrinterName);
        return request;
    }

    private static async Task<PrintJobActionResult> ReadActionResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<PrintJobActionResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return new PrintJobActionResult(
            payload?.Success ?? false,
            payload?.Skipped ?? false,
            payload?.Result ?? "unknown");
    }

    public sealed record PendingPrintJobDto(
        Guid Id,
        Guid OrderId,
        string Type,
        int CopyCount,
        string PayloadJson,
        DateTime CreatedAtUtc);

    private sealed record PendingPrintJobsResponse(
        [property: JsonPropertyName("jobs")] List<PendingPrintJobDto> Jobs);

    private sealed record PrintJobActionResponse(bool Success, bool Skipped, string Result);

    public sealed record PrintJobActionResult(bool Success, bool Skipped, string Result);
}
