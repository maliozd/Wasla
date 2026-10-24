using System.Security.Claims;

namespace Wasla.Application.Abstractions.Admin;

public enum CentralAdminSessionState
{
    Valid,

    /// <summary>The admin id, role or security stamp claim is missing, repeated, malformed or empty.</summary>
    MissingOrMalformedClaims,

    AccountNotFound,
    AccountInactive,
    StampChanged
}

/// <summary>
/// Checks an issued Central Admin session against the admin's current row in CentralDb. Claims are checked before
/// CentralDb is read. It never opens a tenant database and caches nothing, so a deactivation, deletion or password change
/// takes effect on the session's next request.
/// </summary>
public interface ICentralAdminSessionValidator
{
    /// <exception cref="CentralAdminSessionUnavailableException">CentralDb could not be read.</exception>
    Task<CentralAdminSessionState> ValidateAsync(ClaimsPrincipal? principal, CancellationToken ct);
}

/// <summary>
/// CentralDb could not be read while validating a Central Admin session. It carries no inner exception, so no SQL or
/// connection detail reaches the error pipeline. The session must be treated as neither valid nor invalid.
/// </summary>
public sealed class CentralAdminSessionUnavailableException()
    : Exception("Central admin session validation could not be completed.");
