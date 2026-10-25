using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeConnectionException : Exception
{
    public PrintBridgeConnectionException(
        string userMessageKey,
        string endpointPath,
        string serverUrl,
        object[]? formatArgs = null,
        PrintBridgeRuntimeIssueCode issueCode = PrintBridgeRuntimeIssueCode.RequestFailed,
        int? statusCode = null,
        Exception? innerException = null)
        : base(userMessageKey, innerException)
    {
        UserMessageKey = userMessageKey;
        FormatArgs = formatArgs ?? [];
        IssueCode = issueCode;
        EndpointPath = endpointPath;
        ServerUrl = serverUrl;
        StatusCode = statusCode;
    }

    public string UserMessageKey { get; }

    public object[] FormatArgs { get; }

    public PrintBridgeRuntimeIssueCode IssueCode { get; }

    public string EndpointPath { get; }

    public string ServerUrl { get; }

    public int? StatusCode { get; }

    public bool IsTokenAuthFailure => StatusCode is 401 or 403;

    public static PrintBridgeConnectionException FromResponse(
        string endpointPath,
        string serverUrl,
        int statusCode,
        string? responseBody,
        string? serverErrorCode)
    {
        var issueCode = MapServerErrorCode(serverErrorCode, statusCode);
        var (key, args) = issueCode switch
        {
            PrintBridgeRuntimeIssueCode.ReconnectRequired => (
                IsTokenRevokedError(serverErrorCode)
                    ? "RuntimeIssue.ReconnectRequired.TokenRevoked.Detail"
                    : "RuntimeIssue.ReconnectRequired.Detail",
                Array.Empty<object>()),
            PrintBridgeRuntimeIssueCode.DisabledByAdmin => ("RuntimeIssue.DisabledByAdmin.Detail", Array.Empty<object>()),
            PrintBridgeRuntimeIssueCode.DuplicateInstallation => ("RuntimeIssue.DuplicateInstallation.Detail", Array.Empty<object>()),
            PrintBridgeRuntimeIssueCode.EndpointNotFound => ("Connection.EndpointNotFound", Array.Empty<object>()),
            _ when statusCode >= 500 => ("Connection.ServerUnavailable", Array.Empty<object>()),
            _ => ("Connection.RequestFailed", new object[] { statusCode })
        };

        var ex = new PrintBridgeConnectionException(key, endpointPath, serverUrl, args, issueCode, statusCode);
        if (!string.IsNullOrWhiteSpace(responseBody))
            ex.Data["ResponseBody"] = responseBody;
        if (!string.IsNullOrWhiteSpace(serverErrorCode))
            ex.Data["PrintBridgeErrorCode"] = serverErrorCode;
        return ex;
    }

    public static PrintBridgeConnectionException SslError(string endpointPath, string serverUrl, Exception inner) =>
        new("Connection.SslError", endpointPath, serverUrl, issueCode: PrintBridgeRuntimeIssueCode.SslError, innerException: inner);

    public static PrintBridgeConnectionException ServerUnavailable(string endpointPath, string serverUrl, Exception inner) =>
        new("Connection.ServerUnreachable", endpointPath, serverUrl, issueCode: PrintBridgeRuntimeIssueCode.ServerUnreachable, innerException: inner);

    private static PrintBridgeRuntimeIssueCode MapServerErrorCode(string? serverErrorCode, int statusCode)
    {
        if (string.Equals(serverErrorCode, "device_disabled", StringComparison.OrdinalIgnoreCase))
            return PrintBridgeRuntimeIssueCode.DisabledByAdmin;

        if (string.Equals(serverErrorCode, "installation_invalid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverErrorCode, "installation_mismatch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverErrorCode, "installation_already_bound", StringComparison.OrdinalIgnoreCase))
            return PrintBridgeRuntimeIssueCode.DuplicateInstallation;

        if (string.Equals(serverErrorCode, "device_removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverErrorCode, "device_auth_invalid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverErrorCode, "print_bridge_token_required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverErrorCode, "tenant_inactive", StringComparison.OrdinalIgnoreCase))
            return PrintBridgeRuntimeIssueCode.ReconnectRequired;

        return statusCode switch
        {
            401 or 403 => PrintBridgeRuntimeIssueCode.ReconnectRequired,
            404 => PrintBridgeRuntimeIssueCode.EndpointNotFound,
            _ => PrintBridgeRuntimeIssueCode.RequestFailed
        };
    }

    private static bool IsTokenRevokedError(string? serverErrorCode) =>
        string.Equals(serverErrorCode, "device_removed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverErrorCode, "device_auth_invalid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverErrorCode, "print_bridge_token_required", StringComparison.OrdinalIgnoreCase);
}
