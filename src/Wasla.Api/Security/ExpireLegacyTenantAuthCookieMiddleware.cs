using Wasla.Application.Security;

namespace Wasla.Api.Security;

/// <summary>
/// Drops a stale pre-Wasla tenant cookie. The cookie is not read for authentication.
/// </summary>
public sealed class ExpireLegacyTenantAuthCookieMiddleware
{
    private readonly RequestDelegate _next;

    public ExpireLegacyTenantAuthCookieMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(WaslaAuthContracts.LegacyTenantCookieName))
        {
            context.Response.Cookies.Delete(WaslaAuthContracts.LegacyTenantCookieName, new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
        }

        await _next(context);
    }
}
