using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// For tests that construct the tenant AuthController directly with an Owner session, outside the request pipeline:
/// the user lands on <c>/dashboard</c> and a safe local return URL is followed. The real resolver, which authorizes the
/// destination endpoint, is covered over HTTP by <c>TenantPostLoginRedirectTests</c>.
/// </summary>
internal sealed class DashboardLandingPages : ITenantLandingPageResolver
{
    public Task<string?> GetLandingPageAsync(ClaimsPrincipal user, HttpContext httpContext) =>
        Task.FromResult<string?>("/dashboard");

    public Task<string?> ResolveAsync(ClaimsPrincipal user, string? returnUrl, HttpContext httpContext) =>
        Task.FromResult<string?>(TenantReturnUrl.TryGetLocalPath(returnUrl, out _) ? returnUrl : "/dashboard");
}
