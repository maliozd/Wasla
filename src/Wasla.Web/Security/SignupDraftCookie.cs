using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Wasla.Web.Security;

/// <summary>
/// The browser's reference to its registration draft (WAS-192). The draft id is never taken from a request body, query or
/// route; it comes only from this cookie.
/// </summary>
/// <remarks>
/// <para>A Data Protection payload bound to one draft id and the draft's expiry (purpose
/// <c>Wasla.Signup.RegistrationDraft.v1</c>), in an HttpOnly, SameSite=Lax, host-only cookie on path <c>/</c>. Every
/// successful draft write issues it again with the draft's new expiry. A payload that is tampered, expired or protected with
/// a key the app no longer has names no draft. It works across instances only when they share the Data Protection key ring,
/// like <see cref="SignupRegistrationOwnership"/>.</para>
/// <para>Wherever the cookie is Secure (outside Development, or over HTTPS) its name carries the <c>__Host-</c> prefix,
/// so a browser accepts it only from this host, over HTTPS, without a Domain and on path <c>/</c>: a cookie planted from
/// a sibling or parent domain (cookie tossing) can never stand in for it, and only that name is read there. Development
/// over plain HTTP, where a prefixed cookie cannot be set, uses the unprefixed name.</para>
/// </remarks>
public sealed class SignupDraftCookie
{
    /// <summary>The name wherever the cookie is Secure (every environment but Development over plain HTTP).</summary>
    public const string SecureCookieName = "__Host-Wasla.SignupDraft";

    /// <summary>The name in Development over plain HTTP only.</summary>
    public const string DevelopmentHttpCookieName = ".Wasla.SignupDraft";

    /// <summary>The <c>__Host-</c> prefix requires the root path.</summary>
    public const string CookiePath = "/";

    private const string ProtectorPurpose = "Wasla.Signup.RegistrationDraft.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IWebHostEnvironment _environment;

    public SignupDraftCookie(IDataProtectionProvider dataProtection, IWebHostEnvironment environment)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _environment = environment;
    }

    /// <summary>The cookie name this request reads and writes.</summary>
    public string NameFor(HttpRequest request) => IsSecure(request) ? SecureCookieName : DevelopmentHttpCookieName;

    /// <summary>The draft this browser holds, or null without a valid, unexpired cookie. Whether the draft still exists is the service's question.</summary>
    public Guid? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(NameFor(request), out var value) || string.IsNullOrWhiteSpace(value))
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

    /// <summary>Whether the request carries this request's draft cookie at all, valid or not.</summary>
    public bool IsPresent(HttpRequest request) => request.Cookies.ContainsKey(NameFor(request));

    public void Issue(HttpContext context, Guid draftId, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(NameFor(context.Request), CreateValue(draftId, expiresAt), Options(context, expiresAt));
    }

    /// <summary>The protected cookie value naming <paramref name="draftId"/> until <paramref name="expiresAt"/>.</summary>
    public string CreateValue(Guid draftId, DateTimeOffset expiresAt) =>
        _protector.Protect(draftId.ToString("N"), expiresAt);

    public void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(NameFor(context.Request), Options(context, expiresAt: null));
    }

    // Same policy as the ownership and auth cookies: always Secure outside Development.
    private bool IsSecure(HttpRequest request) => !_environment.IsDevelopment() || request.IsHttps;

    private CookieOptions Options(HttpContext context, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = IsSecure(context.Request),
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        Expires = expiresAt,
        IsEssential = true
    };
}
