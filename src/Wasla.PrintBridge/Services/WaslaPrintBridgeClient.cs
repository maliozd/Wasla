using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public sealed class WaslaPrintBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly ILogger<WaslaPrintBridgeClient> _logger;
    private readonly string _appVersion;

    public WaslaPrintBridgeClient(
        HttpClient http,
        PrintBridgeSettingsHolder holder,
        ILogger<WaslaPrintBridgeClient> logger,
        string appVersion)
    {
        _http = http;
        _holder = holder;
        _logger = logger;
        _appVersion = appVersion;
    }

    public async Task<PrintBridgeHealthResult> TestHealthAsync(CancellationToken ct)
    {
        const string path = "api/print-bridge/health";
        using var response = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<PrintBridgeHealthResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return new PrintBridgeHealthResult(
            payload?.CustomerName ?? string.Empty,
            payload?.DeviceName ?? string.Empty,
            payload?.ServerTimeUtc ?? DateTime.UtcNow,
            payload?.MachineName);
    }

    public async Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(CancellationToken ct)
    {
        var (_, bridge, _) = _holder.Snapshot();
        var max = Math.Clamp(bridge.MaxJobsPerPoll, 1, 10);
        var path = $"api/print-bridge/jobs/pending?max={max}";
        using var response = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<PendingPrintJobsResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return payload?.Jobs ?? [];
    }

    public async Task<PrintJobActionResult> MarkPrintingAsync(Guid jobId, CancellationToken ct)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-printing";
        using var response = await SendAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<PrintJobActionResult> MarkPrintedAsync(Guid jobId, CancellationToken ct)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-printed";
        using var response = await SendAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<ReprintJobResult> RequestReprintAsync(Guid jobId, CancellationToken ct)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/reprint";
        using var response = await SendAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<ReprintPrintJobResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return new ReprintJobResult(
            payload?.Success ?? false,
            payload?.MessageKey ?? "Reprint.Failed",
            payload?.NewPrintJobId);
    }

    public async Task<PrintJobActionResult> MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-failed";
        using var request = CreateRequest(HttpMethod.Post, BuildAbsoluteUrl(path));
        request.Content = JsonContent.Create(new { errorMessage });
        using var response = await SendPreparedAsync(path, request, ct).ConfigureAwait(false);
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        using var request = CreateRequest(method, BuildAbsoluteUrl(relativePath));
        return await SendPreparedAsync(relativePath, request, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendPreparedAsync(
        string relativePath,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var (hub, _, _) = _holder.Snapshot();
        var baseUrl = hub.ServerUrl.TrimEnd('/');

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsSslFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Print Bridge SSL error. BaseUrl={BaseUrl}, Path={Path}",
                baseUrl,
                relativePath);
            throw PrintBridgeConnectionException.SslError(relativePath, baseUrl, ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Print Bridge server unavailable. BaseUrl={BaseUrl}, Path={Path}",
                baseUrl,
                relativePath);
            throw PrintBridgeConnectionException.ServerUnavailable(relativePath, baseUrl, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Print Bridge request timed out. BaseUrl={BaseUrl}, Path={Path}",
                baseUrl,
                relativePath);
            throw PrintBridgeConnectionException.ServerUnavailable(relativePath, baseUrl, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Print Bridge API failed. BaseUrl={BaseUrl}, Path={Path}, StatusCode={StatusCode}, Body={Body}",
            baseUrl,
            relativePath,
            (int)response.StatusCode,
            body);

        throw PrintBridgeConnectionException.FromResponse(
            relativePath,
            baseUrl,
            (int)response.StatusCode,
            body);
    }

    private string BuildAbsoluteUrl(string relativeUrl)
    {
        var (hub, _, _) = _holder.Snapshot();
        return $"{hub.ServerUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var (hub, bridge, _) = _holder.Snapshot();
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", hub.AgentToken);
        var machineName = string.IsNullOrWhiteSpace(bridge.MachineName)
            ? Environment.MachineName
            : bridge.MachineName;
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Name", machineName);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Version", _appVersion);
        if (!string.IsNullOrWhiteSpace(bridge.PrinterName))
            request.Headers.TryAddWithoutValidation("X-PrintBridge-Printer", bridge.PrinterName);
        return request;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 500)
                return body[..500] + "...";
            return body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSslFailure(HttpRequestException ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? string.Empty;
            if (name.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Ssl", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var message = ex.Message;
        return message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("certificate", StringComparison.OrdinalIgnoreCase);
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

    public sealed record PrintBridgeHealthResult(
        string CustomerName,
        string DeviceName,
        DateTime ServerTimeUtc,
        string? MachineName = null);

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

    private sealed record PrintBridgeHealthResponse(
        bool Success,
        string CustomerName,
        string DeviceName,
        DateTime ServerTimeUtc,
        string? MachineName);

    public sealed record PrintJobActionResult(bool Success, bool Skipped, string Result);

    public sealed record ReprintJobResult(bool Success, string MessageKey, Guid? NewPrintJobId);

    private sealed record ReprintPrintJobResponse(bool Success, string MessageKey, Guid? NewPrintJobId);
}
