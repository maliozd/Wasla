using Microsoft.AspNetCore.Authentication;

namespace Wasla.Web.Security;

public static class AuthCookiePersistence
{
    public static readonly TimeSpan CentralAdminPersistentDuration = TimeSpan.FromDays(1);
    public static readonly TimeSpan TenantPersistentDuration = TimeSpan.FromDays(14);

    public static AuthenticationProperties Create(bool rememberMe, TimeSpan persistentDuration)
    {
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            IssuedUtc = now
        };

        if (rememberMe)
            properties.ExpiresUtc = now.Add(persistentDuration);

        return properties;
    }
}
