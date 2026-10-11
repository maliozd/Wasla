using Microsoft.AspNetCore.Authentication;

namespace Wasla.Web.Security;

public static class CentralAdminCookieAuthentication
{
    /// <summary>
    /// The Central Admin cookie scheme. Every request that presents the cookie is revalidated against the admin's row in
    /// CentralDb (<see cref="CentralAdminCookieEvents"/>).
    /// </summary>
    public static AuthenticationBuilder AddWaslaCentralAdminCookie(
        this AuthenticationBuilder builder,
        CookieSecurePolicy securePolicy)
    {
        builder.Services.AddScoped<CentralAdminCookieEvents>();

        return builder.AddCookie(AuthSchemes.CentralAdmin, options =>
        {
            options.Cookie.Name = CentralAdminAuthCookieNames.Active;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.LoginPath = "/admin/login";
            options.LogoutPath = "/admin/logout";
            options.AccessDeniedPath = "/admin/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.EventsType = typeof(CentralAdminCookieEvents);
        });
    }
}
