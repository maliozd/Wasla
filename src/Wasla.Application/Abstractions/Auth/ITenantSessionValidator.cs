using System.Security.Claims;

namespace Wasla.Application.Abstractions.Auth;

public enum TenantSessionState
{
    Valid,

    /// <summary>The request has no resolved tenant (a central host, or a path that skips tenant resolution).</summary>
    NoResolvedTenant,

    /// <summary>A tenant id, user id, role or security stamp claim is missing, malformed or empty.</summary>
    MissingOrMalformedClaims,

    /// <summary>The session belongs to another tenant than the one resolved from the host.</summary>
    TenantMismatch,

    UserNotFound,
    UserInactive,
    RoleChanged,
    StampChanged
}

/// <summary>
/// Checks an issued tenant session against the user's current row in the tenant's database. The single validation
/// contract for tenant cookies in Wasla.Web and Wasla.Api. The tenant must come from server-side tenant resolution; the
/// session's own <c>TenantId</c> claim only has to match it and never selects a database. Claims are checked before
/// any database is opened. It caches nothing, so a deactivation, deletion, role or password change takes effect on the
/// session's next request.
/// </summary>
public interface ITenantSessionValidator
{
    /// <exception cref="TenantSessionUnavailableException">The tenant database could not be read.</exception>
    Task<TenantSessionState> ValidateAsync(Guid? resolvedTenantId, ClaimsPrincipal? principal, CancellationToken ct);
}

/// <summary>
/// The tenant database could not be read while validating a tenant session. It carries no inner exception, so no SQL
/// or connection detail reaches the error pipeline. The session must be treated as neither valid nor invalid.
/// </summary>
public sealed class TenantSessionUnavailableException()
    : Exception("Tenant session validation could not be completed.");
