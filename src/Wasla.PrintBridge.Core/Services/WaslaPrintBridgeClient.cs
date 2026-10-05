using System.Net.Http.Json;
using System.Net.Sockets;
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
    private readonly PrintBridgeSetupHttpClientFactory _setupHttpClientFactory;
    private readonly ILogger<WaslaPrintBridgeClient> _logger;
    private readonly string _appVersion;

    public WaslaPrintBridgeClient(
        HttpClient http,
        PrintBridgeSettingsHolder holder,
        PrintBridgeSetupHttpClientFactory setupHttpClientFactory,
        ILogger<WaslaPrintBridgeClient> logger,
        string appVersion)
    {
        _http = http;
        _holder = holder;
        _setupHttpClientFactory = setupHttpClientFactory;
        _logger = logger;
        _appVersion = appVersion;
    }

    public Task<PrintBridgeHealthResult> TestHealthAsync(CancellationToken ct) => ReadHealthAsync(connection: null, ct);

    /// <summary>
    /// Calls the health endpoint with an explicit server URL and token instead of the saved ones, so a new
    /// connection can be verified before anything is saved. The saved settings are neither read for the
    /// address and token nor changed.
    /// </summary>
    public Task<PrintBridgeHealthResult> TestHealthAsync(WaslaOptions connection, CancellationToken ct) =>
        ReadHealthAsync(connection, ct);

    private async Task<PrintBridgeHealthResult> ReadHealthAsync(WaslaOptions? connection, CancellationToken ct)
    {
        const string path = "api/print-bridge/health";
        using var response = await SendAsync(HttpMethod.Get, path, ct, connection).ConfigureAwait(false);
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

    public async Task<PrintJobActionResult> UpdateDeviceNameAsync(string deviceName, CancellationToken ct)
    {
        const string path = "api/print-bridge/device/name";
        using var request = CreateRequest(HttpMethod.Put, BuildAbsoluteUrl(path));
        request.Content = JsonContent.Create(new { deviceName });
        using var response = await SendPreparedAsync(path, request, ct).ConfigureAwait(false);
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchange a one-time automatic-setup code against an explicit server URL (from the protocol URI),
    /// before any settings are saved. No device token is sent. Returns null on any failure.
    /// The code is never logged.
    /// </summary>
    public async Task<SetupExchangeResult?> ExchangeSetupAsync(string serverUrl, string code, CancellationToken ct)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
        {
            _logger.LogWarning(
                "Print Bridge setup exchange rejected before request because server URL is invalid. FailureKind={FailureKind}",
                "invalid_server_url");
            return null;
        }

        var url = $"{serverUri.ToString().TrimEnd('/')}/api/print-bridge/setup/exchange";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Version", _appVersion);
        var (_, bridge, _) = _holder.Snapshot();
        request.Content = JsonContent.Create(new
        {
            code,
            machineName = Environment.MachineName,
            printerName = bridge.PrinterName,
            installationId = ParseInstallationId(bridge.InstallationId),
            deviceName = bridge.DisplayName
        });

        try
        {
            using var setupHttp = _setupHttpClientFactory.Create(serverUri);
            using var response = await setupHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                var rejectionKind = ClassifySetupExchangeRejection((int)response.StatusCode, body);
                _logger.LogWarning(
                    "Print Bridge setup exchange rejected by server. ServerUrl={ServerUrl}, StatusCode={StatusCode}, FailureKind={FailureKind}, Body={Body}",
                    serverUrl,
                    (int)response.StatusCode,
                    rejectionKind,
                    body);

                if (string.Equals(rejectionKind, "installation_already_registered", StringComparison.Ordinal))
                    throw new LocalizedApplicationException("Auto.InstallationAlreadyRegistered");

                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<SetupExchangeResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceToken))
                return null;

            return new SetupExchangeResult(
                payload.SessionId,
                string.IsNullOrWhiteSpace(payload.ServerUrl) ? serverUrl : payload.ServerUrl,
                payload.DeviceToken,
                payload.DeviceName ?? string.Empty,
                payload.InstallationId,
                payload.CompletionCredential ?? string.Empty);
        }
        catch (HttpRequestException ex) when (IsSslFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Print Bridge setup exchange failed due to TLS/certificate validation. ServerUrl={ServerUrl}, FailureKind={FailureKind}",
                serverUrl,
                "tls_certificate");
            return null;
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            _logger.LogWarning(
                ex,
                "Print Bridge setup exchange failed because the server refused the connection. ServerUrl={ServerUrl}, FailureKind={FailureKind}",
                serverUrl,
                "connection_refused");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Print Bridge setup exchange failed due to network/server reachability. ServerUrl={ServerUrl}, FailureKind={FailureKind}",
                serverUrl,
                "network_unavailable");
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Print Bridge setup exchange timed out. ServerUrl={ServerUrl}, FailureKind={FailureKind}",
                serverUrl,
                "timeout");
            return null;
        }
    }

    /// <summary>Best-effort report of automatic-setup completion. Never throws.</summary>
    public async Task CompleteSetupAsync(
        string serverUrl,
        Guid sessionId,
        string completionCredential,
        bool connectionVerified,
        CancellationToken ct)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(completionCredential))
            return;

        var url = $"{serverUrl.TrimEnd('/')}/api/print-bridge/setup/complete";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = JsonContent.Create(new { sessionId, completionCredential, connectionVerified });

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Print Bridge setup completion report failed. ServerUrl={ServerUrl}", serverUrl);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken ct,
        WaslaOptions? connection = null)
    {
        using var request = CreateRequest(method, BuildAbsoluteUrl(relativePath, connection), connection);
        return await SendPreparedAsync(relativePath, request, ct, connection).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendPreparedAsync(
        string relativePath,
        HttpRequestMessage request,
        CancellationToken ct,
        WaslaOptions? connection = null)
    {
        var hub = connection ?? _holder.Snapshot().OrderHub;
        var serverUrl = hub.ServerUrl.TrimEnd('/');

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsSslFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Print Bridge SSL error. ServerUrl={ServerUrl}, Path={Path}",
                serverUrl,
                relativePath);
            throw PrintBridgeConnectionException.SslError(relativePath, serverUrl, ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Print Bridge server unavailable. ServerUrl={ServerUrl}, Path={Path}",
                serverUrl,
                relativePath);
            throw PrintBridgeConnectionException.ServerUnavailable(relativePath, serverUrl, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Print Bridge request timed out. ServerUrl={ServerUrl}, Path={Path}",
                serverUrl,
                relativePath);
            throw PrintBridgeConnectionException.ServerUnavailable(relativePath, serverUrl, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
        var serverErrorCode = TryGetPrintBridgeErrorCode(response, body);
        _logger.LogWarning(
            "Print Bridge API failed. ServerUrl={ServerUrl}, Path={Path}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, Body={Body}",
            serverUrl,
            relativePath,
            (int)response.StatusCode,
            serverErrorCode,
            body);

        throw PrintBridgeConnectionException.FromResponse(
            relativePath,
            serverUrl,
            (int)response.StatusCode,
            body,
            serverErrorCode);
    }

    private string BuildAbsoluteUrl(string relativeUrl, WaslaOptions? connection = null)
    {
        var hub = connection ?? _holder.Snapshot().OrderHub;
        return $"{hub.ServerUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
    }

    /// <summary>
    /// Builds a request with the device headers. <paramref name="connection"/> replaces only the saved server URL
    /// and token; the installation id, machine name and printer always come from the saved settings.
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string url, WaslaOptions? connection = null)
    {
        var (savedHub, bridge, _) = _holder.Snapshot();
        var hub = connection ?? savedHub;
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", hub.AgentToken);
        var machineName = string.IsNullOrWhiteSpace(bridge.MachineName)
            ? Environment.MachineName
            : bridge.MachineName;
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Name", machineName);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Version", _appVersion);
        var installationId = ParseInstallationId(bridge.InstallationId);
        if (installationId != Guid.Empty)
            request.Headers.TryAddWithoutValidation("X-PrintBridge-Installation-Id", installationId.ToString("D"));
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

    private static string? TryGetPrintBridgeErrorCode(HttpResponseMessage response, string? body)
    {
        if (response.Headers.TryGetValues("X-PrintBridge-Error-Code", out var values))
        {
            var headerValue = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(headerValue))
                return headerValue.Trim();
        }

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
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

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return true;
        }

        return ex.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase);
    }

    private static Guid ParseInstallationId(string? value) =>
        Guid.TryParse(value, out var installationId) && installationId != Guid.Empty
            ? installationId
            : Guid.Empty;

    private static string ClassifySetupExchangeRejection(int statusCode, string body)
    {
        const int badRequest = 400;
        const int conflict = 409;
        const int tooManyRequests = 429;

        if (statusCode == tooManyRequests)
            return "rate_limited";

        if (statusCode == badRequest &&
            body.Contains("invalid_or_expired_setup_code", StringComparison.OrdinalIgnoreCase))
            return "invalid_or_expired_or_setup_rejected";

        if (statusCode == conflict)
        {
            if (body.Contains("installation_already_registered", StringComparison.OrdinalIgnoreCase))
                return "installation_already_registered";

            return "setup_conflict";
        }

        return "server_rejection";
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

    public sealed record SetupExchangeResult(
        Guid SessionId,
        string ServerUrl,
        string DeviceToken,
        string DeviceName,
        Guid InstallationId,
        string CompletionCredential);

    private sealed record SetupExchangeResponse(
        Guid SessionId,
        string ServerUrl,
        string DeviceToken,
        string? DeviceName,
        Guid InstallationId,
        string? CompletionCredential);
}
