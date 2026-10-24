using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Security;

namespace Wasla.Api.Security;

public static class ApiTenantAuthenticationExtensions
{
    /// <summary>
    /// The tenant cookie scheme, with the same cookie contract as Wasla.Web, and tenant role authorization. Every request
    /// that presents the cookie is revalidated against the user's row in the tenant database
    /// (<see cref="ApiTenantCookieEvents"/>), with the validator Wasla.Web uses. Policies come from the shared table
    /// (<see cref="WaslaTenantPolicies"/>). An endpoint without a named policy, including a bare <c>[Authorize]</c>,
    /// admits only a current Owner.
    /// </summary>
    public static IServiceCollection AddWaslaApiTenantAuthentication(
        this IServiceCollection services,
        CookieSecurePolicy securePolicy)
    {
        services.AddScoped<ApiTenantCookieEvents>();

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = WaslaAuthContracts.TenantScheme;
            options.DefaultAuthenticateScheme = WaslaAuthContracts.TenantScheme;
            options.DefaultChallengeScheme = WaslaAuthContracts.TenantScheme;
        })
        .AddCookie(WaslaAuthContracts.TenantScheme, options =>
        {
            options.Cookie.Name = WaslaAuthContracts.TenantCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.EventsType = typeof(ApiTenantCookieEvents);
        });

        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddWaslaTenantRolePolicies(WaslaAuthContracts.TenantScheme);

            var ownerOnly = TenantRolePolicyRegistration.BuildPolicy([UserRole.Owner], WaslaAuthContracts.TenantScheme);
            options.DefaultPolicy = ownerOnly;
            options.FallbackPolicy = ownerOnly;
        });

        return services;
    }
}

/// <summary>
/// Wasla.Api's tenant session revalidation: the shared <see cref="TenantSessionCookieEvents"/>, answering with status
/// codes instead of redirects. An unauthenticated or rejected session gets 401, a current session without the required
/// role gets 403, and nothing redirects to a login, access-denied or return URL.
/// </summary>
public sealed class ApiTenantCookieEvents(
    ICurrentTenantService currentTenant,
    ITenantSessionValidator validator,
    ILogger<ApiTenantCookieEvents> logger)
    : TenantSessionCookieEvents(currentTenant, validator, logger)
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => Task.CompletedTask;

    public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => Task.CompletedTask;
}
