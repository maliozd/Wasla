using Wasla.Api.Printing;
using Wasla.Application.Abstractions.Printing;

namespace Wasla.Api.Middleware;

public sealed class PrintBridgeAuthMiddleware
{
    public const string TokenHeader = "X-PrintBridge-Token";
    public const string NameHeader = "X-PrintBridge-Name";
    public const string VersionHeader = "X-PrintBridge-Version";
    public const string PrinterHeader = "X-PrintBridge-Printer";
    public const string InstallationIdHeader = "X-PrintBridge-Installation-Id";
    public const string ErrorCodeHeader = "X-PrintBridge-Error-Code";

    private readonly RequestDelegate _next;

    public PrintBridgeAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IPrintBridgeAuthService authService,
        ILogger<PrintBridgeAuthMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/print-bridge", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Automatic setup endpoints authenticate via the one-time setup code / completion
        // credential, not a device token. The app has no token yet during setup.
        if (path.StartsWith("/api/print-bridge/setup", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(TokenHeader, out var tokenValues) ||
            string.IsNullOrWhiteSpace(tokenValues.FirstOrDefault()))
        {
            await WriteAuthFailureAsync(
                context,
                PrintBridgeAuthFailureCode.MissingToken,
                "Print Bridge token is required.");
            return;
        }

        var installationId = context.Request.Headers.TryGetValue(InstallationIdHeader, out var installationIdValues) &&
            Guid.TryParse(installationIdValues.FirstOrDefault(), out var parsedInstallationId)
                ? parsedInstallationId
                : (Guid?)null;

        var clientInfo = new PrintBridgeClientInfo(
            context.Request.Headers.TryGetValue(NameHeader, out var nameValues) ? nameValues.FirstOrDefault() : null,
            context.Request.Headers.TryGetValue(VersionHeader, out var versionValues) ? versionValues.FirstOrDefault() : null,
            context.Request.Headers.TryGetValue(PrinterHeader, out var printerValues) ? printerValues.FirstOrDefault() : null,
            context.Connection.RemoteIpAddress?.ToString(),
            installationId);

        var auth = await authService.AuthenticateDetailedAsync(tokenValues.FirstOrDefault() ?? string.Empty, clientInfo, context.RequestAborted)
            .ConfigureAwait(false);

        if (!auth.Succeeded || auth.Context is null)
        {
            logger.LogWarning(
                "Print Bridge request rejected. FailureCode={FailureCode}",
                auth.FailureCode);
            await WriteAuthFailureAsync(
                context,
                auth.FailureCode ?? PrintBridgeAuthFailureCode.InvalidToken,
                "Invalid Print Bridge token.");
            return;
        }

        PrintBridgeContext.Set(context, auth.Context);
        await _next(context);
    }

    private static async Task WriteAuthFailureAsync(
        HttpContext context,
        PrintBridgeAuthFailureCode failureCode,
        string message)
    {
        var errorCode = ToErrorCode(failureCode);
        context.Response.StatusCode = failureCode == PrintBridgeAuthFailureCode.DeviceDisabled
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;
        context.Response.Headers[ErrorCodeHeader] = errorCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = errorCode,
            message
        });
    }

    private static string ToErrorCode(PrintBridgeAuthFailureCode failureCode) =>
        failureCode switch
        {
            PrintBridgeAuthFailureCode.MissingToken => "print_bridge_token_required",
            PrintBridgeAuthFailureCode.DeviceDisabled => "device_disabled",
            PrintBridgeAuthFailureCode.DeviceRemoved => "device_removed",
            PrintBridgeAuthFailureCode.InstallationIdInvalid => "installation_invalid",
            PrintBridgeAuthFailureCode.InstallationIdMismatch => "installation_mismatch",
            PrintBridgeAuthFailureCode.InstallationIdAlreadyBound => "installation_already_bound",
            PrintBridgeAuthFailureCode.TenantInactive => "tenant_inactive",
            _ => "device_auth_invalid"
        };
}
