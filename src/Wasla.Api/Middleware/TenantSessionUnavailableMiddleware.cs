using Wasla.Application.Abstractions.Auth;

namespace Wasla.Api.Middleware;

/// <summary>
/// Answers a request whose tenant session could not be validated (the tenant database could not be read) with
/// 503 and a fixed JSON error, before any endpoint runs. The session is neither accepted nor deleted; the client may
/// retry. The validator has already logged the exception type; nothing about the failure reaches the response.
/// </summary>
public sealed class TenantSessionUnavailableMiddleware
{
    public const string ErrorCode = "tenant_session_unavailable";

    private readonly RequestDelegate _next;

    public TenantSessionUnavailableMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (TenantSessionUnavailableException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = ErrorCode });
        }
    }
}
