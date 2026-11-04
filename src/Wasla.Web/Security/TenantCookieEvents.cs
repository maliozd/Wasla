using Microsoft.AspNetCore.Authentication.Cookies;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Security;

namespace Wasla.Web.Security;

/// <summary>
/// Web's tenant session revalidation: the shared <see cref="TenantSessionCookieEvents"/>, with the cookie handler's
/// default challenge and forbid behavior (redirects to the scheme's <c>LoginPath</c> and <c>AccessDeniedPath</c> on the
/// same host), and a logout that never renews the cookie. A tenant database failure surfaces as
/// <see cref="TenantSessionUnavailableException"/> to the global exception handler, or for a logout to
/// <see cref="Middleware.TenantLogoutFailureMiddleware"/>.
/// </summary>
public sealed class TenantCookieEvents(
    ICurrentTenantService currentTenant,
    ITenantSessionValidator validator,
    ILogger<TenantCookieEvents> logger)
    : TenantSessionCookieEvents(currentTenant, validator, logger)
{
    /// <summary>
    /// The logout itself (a POST to an action marked <see cref="TenantLogoutAttribute"/>) never renews the cookie
    /// (WAS-186). A logout that succeeds signs out, which cancels renewal anyway. One whose revocation cannot be stored gets
    /// the logout failure page, and the handler writes a requested renewal when the response starts, after the failure
    /// page has cleared the response: it would re-issue a session whose logout is unconfirmed, possibly already revoked.
    /// So renewal is not requested for this request at all. Every other request renews a due session as usual, the logout
    /// page included.
    /// </summary>
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        await base.ValidatePrincipal(context);

        if (context.ShouldRenew
            && HttpMethods.IsPost(context.Request.Method)
            && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<TenantLogoutAttribute>() is not null)
        {
            context.ShouldRenew = false;
        }
    }
}
