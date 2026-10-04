using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Wasla.Web.Security;

/// <summary>
/// Proof that the current browser submitted a particular signup. A registration ID is not secret
/// (the pending-tenant redirect reveals it), so private registration pages and registration changes
/// require this proof instead.
/// </summary>
/// <remarks>
/// The proof is issued only by a successful signup submission. It is a Data Protection payload bound to
/// one registration ID with an expiry, stored in an HttpOnly, SameSite=Strict, host-only cookie (no
/// Domain, so tenant subdomains never receive it). It is never placed in a URL.
/// </remarks>
public sealed class SignupRegistrationOwnership
{
    public const string CookieName = ".Wasla.SignupRegistration";

    private const string ProtectorPurpose = "Wasla.Signup.RegistrationOwnership.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IWebHostEnvironment _environment;

    public SignupRegistrationOwnership(IDataProtectionProvider dataProtection, IWebHostEnvironment environment)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _environment = environment;
    }

    public void Grant(HttpContext context, Guid registrationId, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(CookieName, CreateProof(registrationId, expiresAt), new CookieOptions
        {
            HttpOnly = true,
            // Same policy as the auth cookies: always Secure outside Development.
            Secure = !_environment.IsDevelopment() || context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true
        });
    }

    public bool IsOwner(HttpRequest request, Guid registrationId)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var proof) || string.IsNullOrWhiteSpace(proof))
            return false;

        try
        {
            var payload = _protector.Unprotect(proof, out _);
            return Guid.TryParseExact(payload, "N", out var provenId) && provenId == registrationId;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Tampered, expired, or protected with a key this app no longer has.
            return false;
        }
    }

    public string CreateProof(Guid registrationId, DateTimeOffset expiresAt) =>
        _protector.Protect(registrationId.ToString("N"), expiresAt);
}
