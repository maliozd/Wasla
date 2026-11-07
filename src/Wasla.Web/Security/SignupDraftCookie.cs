using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Wasla.Web.Security;

/// <summary>
/// The browser's reference to its registration draft (WAS-192). The draft id is never taken from a request body, query or
/// route; it comes only from this cookie.
/// </summary>
/// <remarks>
/// A Data Protection payload bound to one draft id and the draft's expiry (purpose
/// <c>Wasla.Signup.RegistrationDraft.v1</c>), stored in an HttpOnly, SameSite=Lax, host-only cookie on path
/// <c>/signup</c>, Secure outside Development. Every successful draft write issues it again with the draft's new expiry.
/// A payload that is tampered, expired or protected with a key the app no longer has names no draft. It works across
/// instances only when they share the Data Protection key ring, like <see cref="SignupRegistrationOwnership"/>.
/// </remarks>
public sealed class SignupDraftCookie
{
    public const string CookieName = ".Wasla.SignupDraft";

    public const string CookiePath = "/signup";

    private const string ProtectorPurpose = "Wasla.Signup.RegistrationDraft.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IWebHostEnvironment _environment;

    public SignupDraftCookie(IDataProtectionProvider dataProtection, IWebHostEnvironment environment)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _environment = environment;
    }

    /// <summary>The draft this browser holds, or null without a valid, unexpired cookie. Whether the draft still exists is the service's question.</summary>
    public Guid? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var payload = _protector.Unprotect(value, out _);
            return Guid.TryParseExact(payload, "N", out var draftId) ? draftId : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether the request carries a draft cookie at all, valid or not.</summary>
    public static bool IsPresent(HttpRequest request) => request.Cookies.ContainsKey(CookieName);

    public void Issue(HttpContext context, Guid draftId, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(CookieName, CreateValue(draftId, expiresAt), Options(context, expiresAt));
    }

    /// <summary>The protected cookie value naming <paramref name="draftId"/> until <paramref name="expiresAt"/>.</summary>
    public string CreateValue(Guid draftId, DateTimeOffset expiresAt) =>
        _protector.Protect(draftId.ToString("N"), expiresAt);

    public void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, Options(context, expiresAt: null));
    }

    private CookieOptions Options(HttpContext context, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        // Same policy as the ownership and auth cookies: always Secure outside Development.
        Secure = !_environment.IsDevelopment() || context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        Expires = expiresAt,
        IsEssential = true
    };
}
