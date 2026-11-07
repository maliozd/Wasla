using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Services;

public sealed class WaslaPrintBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The error codes the Wasla server answers Print Bridge with: <c>PrintBridgeAuthMiddleware</c> (Web and Api),
    /// <c>PrintBridgeSetupApiController</c> and <c>PrintBridgeSetupFailureReasons</c>. Only these are ever logged.
    /// </summary>
    private static readonly string[] KnownServerErrorCodes =
    [
        "print_bridge_token_required",
        "device_auth_invalid",
        "device_removed",
        "device_disabled",
        "tenant_inactive",
        "installation_invalid",
        "installation_mismatch",
        "installation_already_bound",
        "invalid_or_expired_setup_code",
        "source_credential_rejected",
        "printing_in_progress",
        "device_limit_reached",
        "installation_conflict",
        "destination_device_disabled",
        "same_tenant",
        "transfer_failed",
        "installation_already_registered",
        "installation_registered_to_another_tenant"
    ];

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
            payload?.TenantName ?? string.Empty,
            payload?.DeviceName ?? string.Empty,
            payload?.ServerTimeUtc ?? DateTime.UtcNow,
            payload?.MachineName);
    }

    /// <summary>
    /// <paramref name="connection"/> replaces the saved server URL and token, so a polling cycle and the jobs it claims
    /// stay on one connection even if the saved one changes meanwhile.
    /// </summary>
    public async Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(CancellationToken ct, WaslaOptions? connection = null)
    {
        var (_, bridge, _) = _holder.Snapshot();
        var max = Math.Clamp(bridge.MaxJobsPerPoll, 1, 10);
        var path = $"api/print-bridge/jobs/pending?max={max}";
        using var response = await SendAsync(HttpMethod.Get, path, ct, connection).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<PendingPrintJobsResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return payload?.Jobs ?? [];
    }

    public async Task<PrintJobActionResult> MarkPrintingAsync(Guid jobId, CancellationToken ct, WaslaOptions? connection = null)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-printing";
        using var response = await SendAsync(HttpMethod.Post, path, ct, connection).ConfigureAwait(false);
        return await ReadActionResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<PrintJobActionResult> MarkPrintedAsync(Guid jobId, CancellationToken ct, WaslaOptions? connection = null)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-printed";
        using var response = await SendAsync(HttpMethod.Post, path, ct, connection).ConfigureAwait(false);
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

    public async Task<PrintJobActionResult> MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct, WaslaOptions? connection = null)
    {
        var path = $"api/print-bridge/jobs/{jobId:D}/mark-failed";
        using var request = CreateRequest(HttpMethod.Post, BuildAbsoluteUrl(path, connection), connection);
        request.Content = JsonContent.Create(new { errorMessage });
        using var response = await SendPreparedAsync(path, request, ct, connection).ConfigureAwait(false);
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
                // The body is read only to classify the rejection and is never logged: an echo of the request would
                // contain the setup code.
                var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                var rejectionKind = ClassifySetupExchangeRejection((int)response.StatusCode, body);
                _logger.LogWarning(
                    "Print Bridge setup exchange rejected by server. ServerUrl={ServerUrl}, StatusCode={StatusCode}, FailureKind={FailureKind}",
                    serverUrl,
                    (int)response.StatusCode,
                    rejectionKind);

                if (string.Equals(rejectionKind, "installation_already_registered", StringComparison.Ordinal))
                    throw new LocalizedApplicationException("Auto.InstallationAlreadyRegistered");

                // WAS-91: still registered to another restaurant and not moved with that device's own token.
                if (string.Equals(rejectionKind, "installation_registered_to_another_tenant", StringComparison.Ordinal))
                    throw new LocalizedApplicationException("Auto.InstallationRegisteredToAnotherRestaurant");

                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<SetupExchangeResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceToken))
                return null;

            return new SetupExchangeResult(
                payload.SessionId,
                ResolveSetupServerUrl(serverUrl, payload.ServerUrl),
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

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
            return;

        var url = $"{serverUrl.TrimEnd('/')}/api/print-bridge/setup/complete";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = JsonContent.Create(new { sessionId, completionCredential, connectionVerified });

        try
        {
            // The setup client, which never follows a redirect: the completion credential is not sent again elsewhere.
            using var setupHttp = _setupHttpClientFactory.Create(serverUri);
            using var response = await setupHttp.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Print Bridge setup completion report failed. ServerUrl={ServerUrl}", serverUrl);
        }
    }

    /// <summary>
    /// Read-only check of a move to the setup link's restaurant (WAS-91), asked of the server Print Bridge is connected
    /// to now, with its own token: the token never goes to the link's server. Returns the verified names for the
    /// confirmation. Nothing changes on the server and the code stays unused.
    /// </summary>
    public Task<SetupTransferReply> PreviewSetupTransferAsync(
        WaslaOptions current,
        string installationId,
        string setupServerUrl,
        string code,
        CancellationToken ct) =>
        SendSetupTransferAsync(
            current.ServerUrl,
            current.AgentToken,
            installationId,
            "api/print-bridge/setup/transfer/preview",
            new { code, serverUrl = setupServerUrl },
            setupServerUrl,
            isTransfer: false,
            ct);

    /// <summary>
    /// The confirmed move, sent to the server it was confirmed against with the pending change's secrets. Repeating it
    /// with the same pending change is safe: the server answers with the same result.
    /// </summary>
    public Task<SetupTransferReply> TransferSetupAsync(PrintBridgePendingConnectionChange change, CancellationToken ct)
    {
        var (_, bridge, _) = _holder.Snapshot();
        return SendSetupTransferAsync(
            change.SourceServerUrl,
            change.SourceToken,
            change.InstallationId,
            "api/print-bridge/setup/transfer",
            new
            {
                code = change.Code,
                serverUrl = change.SetupServerUrl,
                deviceToken = change.NewToken,
                completionCredential = change.CompletionCredential,
                machineName = Environment.MachineName,
                printerName = bridge.PrinterName
            },
            change.SetupServerUrl,
            isTransfer: true,
            ct);
    }

    private async Task<SetupTransferReply> SendSetupTransferAsync(
        string sourceServerUrl,
        string sourceToken,
        string installationId,
        string path,
        object body,
        string setupServerUrl,
        bool isTransfer,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(sourceServerUrl, UriKind.Absolute, out var serverUri))
            return new SetupTransferReply(SetupTransferReplyKind.Unknown);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{sourceServerUrl.TrimEnd('/')}/{path}");
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", sourceToken);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Installation-Id", installationId);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Version", _appVersion);
        request.Content = JsonContent.Create(body);

        try
        {
            using var setupHttp = _setupHttpClientFactory.Create(serverUri);
            using var response = await setupHttp.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var reply = isTransfer
                    ? await ReadTransferReplyAsync(response, setupServerUrl, ct).ConfigureAwait(false)
                    : await ReadTransferPreviewAsync(response, setupServerUrl, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Print Bridge transfer request answered. Path={Path}, StatusCode={StatusCode}, Reply={Reply}",
                    path,
                    status,
                    reply.Kind);
                return reply;
            }

            // Only the error code is read, and it is logged only when the Wasla server sends it: the request carried
            // secrets, and an answer that is not the server's own may echo them.
            var errorCode = TryGetPrintBridgeErrorCode(response, await SafeReadBodyAsync(response, ct).ConfigureAwait(false));
            var kind = ClassifyTransferRefusal(status, errorCode);
            _logger.LogWarning(
                "Print Bridge transfer request refused. ServerUrl={ServerUrl}, Path={Path}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, Reply={Reply}",
                sourceServerUrl,
                path,
                status,
                LoggableErrorCode(errorCode),
                kind);
            return new SetupTransferReply(kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // No answer, or one that cannot be read: whether the server stored a move is not known.
            _logger.LogWarning(
                ex,
                "Print Bridge transfer request got no usable answer. ServerUrl={ServerUrl}, Path={Path}",
                sourceServerUrl,
                path);
            return new SetupTransferReply(SetupTransferReplyKind.Unknown);
        }
    }

    private static async Task<SetupTransferReply> ReadTransferPreviewAsync(
        HttpResponseMessage response,
        string setupServerUrl,
        CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<SetupTransferPreviewResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        if (payload is null)
            return new SetupTransferReply(SetupTransferReplyKind.Unknown);

        if (payload.SameTenant)
            return new SetupTransferReply(SetupTransferReplyKind.SameTenant);

        // The server checks that the session belongs to the link's server; a different answer is never shown.
        if (!PrintBridgeServerTrustPolicy.IsSameOrigin(payload.TargetServerUrl, setupServerUrl))
            return new SetupTransferReply(SetupTransferReplyKind.InvalidOrExpired);

        return new SetupTransferReply(
            SetupTransferReplyKind.Ready,
            CurrentTenantName: payload.CurrentTenantName?.Trim() ?? string.Empty,
            TargetTenantName: payload.TargetTenantName?.Trim() ?? string.Empty,
            TargetServerUrl: payload.TargetServerUrl!);
    }

    private async Task<SetupTransferReply> ReadTransferReplyAsync(
        HttpResponseMessage response,
        string setupServerUrl,
        CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<SetupTransferResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        if (payload is null || payload.SessionId == Guid.Empty)
            return new SetupTransferReply(SetupTransferReplyKind.Unknown);

        return new SetupTransferReply(
            SetupTransferReplyKind.Ready,
            SessionId: payload.SessionId,
            ServerUrl: ResolveSetupServerUrl(setupServerUrl, payload.ServerUrl),
            DeviceName: payload.DeviceName ?? string.Empty,
            InstallationId: payload.InstallationId);
    }

    /// <summary>
    /// The server URL to save after setup: the server's canonical value only when it has the setup link's origin,
    /// otherwise the link's server (WAS-91). The link's server is the one that was checked against the trust policy.
    /// </summary>
    private string ResolveSetupServerUrl(string setupServerUrl, string? responseServerUrl)
    {
        if (string.IsNullOrWhiteSpace(responseServerUrl))
            return setupServerUrl;

        if (PrintBridgeServerTrustPolicy.IsSameOrigin(setupServerUrl, responseServerUrl))
            return responseServerUrl.Trim();

        _logger.LogWarning(
            "Print Bridge setup answer named a server with another origin than the setup link; the link's server is kept. ServerUrl={ServerUrl}",
            setupServerUrl);
        return setupServerUrl;
    }

    private static SetupTransferReplyKind ClassifyTransferRefusal(int statusCode, string? errorCode) =>
        statusCode switch
        {
            // A redirect is never followed: the request carried the token and the move's secrets. Whether the server
            // stored the move before answering is not known, so the pending change is kept for recovery.
            >= 300 and < 400 => SetupTransferReplyKind.Unknown,
            400 => SetupTransferReplyKind.InvalidOrExpired,
            401 or 403 => SetupTransferReplyKind.SourceRejected,
            409 => errorCode switch
            {
                "printing_in_progress" => SetupTransferReplyKind.PrintingInProgress,
                "device_limit_reached" => SetupTransferReplyKind.DeviceLimitReached,
                "installation_conflict" => SetupTransferReplyKind.InstallationConflict,
                "destination_device_disabled" => SetupTransferReplyKind.DestinationDeviceDisabled,
                "same_tenant" => SetupTransferReplyKind.SameTenant,
                _ => SetupTransferReplyKind.InvalidOrExpired
            },
            // 404 (a server without moves), 429 and 5xx: nothing is known, so nothing is given up.
            _ => SetupTransferReplyKind.Unknown
        };

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
        var target = connection ?? _holder.Snapshot().Connection;
        var serverUrl = target.ServerUrl.TrimEnd('/');

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

        // The answer is read only to classify it. Its body and its error code are never logged: a server or proxy may
        // echo what the request carried, such as the device token (WAS-191). Only a code the Wasla server sends is named.
        var statusCode = (int)response.StatusCode;
        var serverErrorCode = TryGetPrintBridgeErrorCode(response, await SafeReadBodyAsync(response, ct).ConfigureAwait(false));
        var failure = PrintBridgeConnectionException.FromResponse(relativePath, serverUrl, statusCode, serverErrorCode);
        _logger.LogWarning(
            "Print Bridge API failed. ServerUrl={ServerUrl}, Path={Path}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, Issue={Issue}",
            serverUrl,
            relativePath,
            statusCode,
            LoggableErrorCode(serverErrorCode),
            failure.IssueCode);

        throw failure;
    }

    private string BuildAbsoluteUrl(string relativeUrl, WaslaOptions? connection = null)
    {
        var target = connection ?? _holder.Snapshot().Connection;
        return $"{target.ServerUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
    }

    /// <summary>
    /// Builds a request with the device headers. <paramref name="connection"/> replaces only the saved server URL
    /// and token; the installation id, machine name and printer always come from the saved settings.
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string url, WaslaOptions? connection = null)
    {
        var (savedConnection, bridge, _) = _holder.Snapshot();
        var target = connection ?? savedConnection;
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", target.AgentToken);
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

    /// <summary>
    /// The error code as it may be logged: the name of a code the Wasla server sends, <c>unrecognized</c> for any other
    /// value, and null when the answer has none. An unknown value is never written, because it may echo the request.
    /// </summary>
    private static string? LoggableErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return null;

        foreach (var known in KnownServerErrorCodes)
        {
            if (string.Equals(known, errorCode, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return "unrecognized";
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

        // Setup redirects are never followed, so the code is not sent again; whether it was used is not known.
        if (statusCode is >= 300 and < 400)
            return "redirect_not_followed";

        if (statusCode == badRequest &&
            body.Contains("invalid_or_expired_setup_code", StringComparison.OrdinalIgnoreCase))
            return "invalid_or_expired_or_setup_rejected";

        if (statusCode == conflict)
        {
            if (body.Contains("installation_already_registered", StringComparison.OrdinalIgnoreCase))
                return "installation_already_registered";

            if (body.Contains("installation_registered_to_another_tenant", StringComparison.OrdinalIgnoreCase))
                return "installation_registered_to_another_tenant";

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
        string TenantName,
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
        string TenantName,
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

    /// <summary>
    /// How the server answered a move or its preview (WAS-91). Every kind except <see cref="SetupTransferReplyKind.Unknown"/>
    /// is the server's own answer; a refusal changed nothing.
    /// </summary>
    public enum SetupTransferReplyKind
    {
        Ready,
        SameTenant,
        SourceRejected,
        InvalidOrExpired,
        PrintingInProgress,
        DeviceLimitReached,
        InstallationConflict,
        DestinationDeviceDisabled,

        /// <summary>No answer or an unreadable one (network, timeout, 404, 429, 5xx): whether a move was stored is not known.</summary>
        Unknown
    }

    public sealed record SetupTransferReply(
        SetupTransferReplyKind Kind,
        string CurrentTenantName = "",
        string TargetTenantName = "",
        string TargetServerUrl = "",
        Guid SessionId = default,
        string ServerUrl = "",
        string DeviceName = "",
        Guid InstallationId = default);

    private sealed record SetupTransferPreviewResponse(
        bool SameTenant,
        string? CurrentTenantName,
        string? TargetTenantName,
        string? TargetServerUrl);

    private sealed record SetupTransferResponse(
        Guid SessionId,
        string? ServerUrl,
        string? DeviceName,
        Guid InstallationId);
}
