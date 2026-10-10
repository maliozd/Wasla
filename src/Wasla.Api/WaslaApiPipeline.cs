using Wasla.Api.Middleware;
using Wasla.Api.Security;
using Wasla.Infrastructure.Diagnostics;

namespace Wasla.Api;

/// <summary>
/// The Api's tenant request pipeline and endpoints, used by Program.cs (and by the in-process test host, so tests run
/// the same order).
/// </summary>
public static class WaslaApiPipeline
{
    /// <summary>
    /// Tenant resolution from the host, then Print Bridge device authentication, then the tenant cookie: authentication
    /// revalidates the session against the resolved tenant's database, and authorization applies the tenant role
    /// policies.
    /// </summary>
    public static WebApplication UseWaslaApiRequestPipeline(this WebApplication app)
    {
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseMiddleware<PrintBridgeAuthMiddleware>();
        app.UseMiddleware<ExpireLegacyTenantAuthCookieMiddleware>();
        app.UseMiddleware<TenantSessionUnavailableMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Controllers, the service banner and the health probes. Every endpoint either names a tenant policy or is
    /// anonymous on purpose; anything else falls back to the Owner-only policy.
    /// </summary>
    public static WebApplication MapWaslaApiEndpoints(this WebApplication app)
    {
        app.MapControllers();
        app.MapGet("/", () => Results.Ok("Wasla API")).AllowAnonymous();
        app.MapWaslaHealthChecks();
        return app;
    }
}
