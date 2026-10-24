using Microsoft.AspNetCore.Authentication;

namespace Wasla.Web.Security;

public static class TenantCookieAuthentication
{
    /// <summary>
    /// The tenant cookie scheme. Every request that presents the cookie is revalidated against the user's row in the
    /// tenant database (<see cref="TenantCookieEvents"/>).
    /// </summary>
    public static AuthenticationBuilder AddWaslaTenantCookie(
        this AuthenticationBuilder builder,
        CookieSecurePolicy securePolicy)
    {
        builder.Services.AddScoped<TenantCookieEvents>();

        return builder.AddCookie(AuthSchemes.Tenant, options =>
        {
            options.Cookie.Name = TenantAuthCookieNames.Active;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.LoginPath = "/auth/login";
            options.LogoutPath = "/auth/logout";
            options.AccessDeniedPath = "/auth/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.EventsType = typeof(TenantCookieEvents);
        });
    }
}
