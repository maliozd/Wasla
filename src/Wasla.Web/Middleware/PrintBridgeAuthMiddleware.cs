using Wasla.Application.Abstractions.Printing;
using Wasla.Web.PrintBridge;

namespace Wasla.Web.Middleware;

public sealed class PrintBridgeAuthMiddleware
{
    public const string TokenHeader = "X-PrintBridge-Token";
    public const string NameHeader = "X-PrintBridge-Name";
    public const string VersionHeader = "X-PrintBridge-Version";
    public const string PrinterHeader = "X-PrintBridge-Printer";

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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Print Bridge token is required.");
            return;
        }

        var clientInfo = new PrintBridgeClientInfo(
            context.Request.Headers.TryGetValue(NameHeader, out var nameValues) ? nameValues.FirstOrDefault() : null,
            context.Request.Headers.TryGetValue(VersionHeader, out var versionValues) ? versionValues.FirstOrDefault() : null,
            context.Request.Headers.TryGetValue(PrinterHeader, out var printerValues) ? printerValues.FirstOrDefault() : null,
            context.Connection.RemoteIpAddress?.ToString());

        var auth = await authService.AuthenticateAsync(tokenValues.FirstOrDefault() ?? string.Empty, clientInfo, context.RequestAborted)
            .ConfigureAwait(false);

        if (auth is null)
        {
            logger.LogWarning("Print Bridge request rejected: invalid token.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid Print Bridge token.");
            return;
        }

        PrintBridgeContext.Set(context, auth);
        await _next(context);
    }
}
