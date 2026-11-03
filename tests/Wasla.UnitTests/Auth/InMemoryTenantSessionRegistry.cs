using System.Collections.Concurrent;
using System.Security.Claims;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Stands in for <c>TenantSessionRegistry</c> in tests that construct the tenant <c>AuthController</c> directly with a
/// capturing authentication service, so no cookie is ever read back. It records what the controller created and
/// revoked. The real registry, its database and the cookie check are covered over HTTP by
/// <see cref="TenantLogoutRevocationTests"/>.
/// </summary>
internal sealed class InMemoryTenantSessionRegistry : ITenantSessionRegistry
{
    public ConcurrentQueue<(Guid TenantId, Guid UserId, Guid SessionId)> Created { get; } = new();

    public ConcurrentQueue<(Guid TenantId, Guid UserId, Guid SessionId)> Revoked { get; } = new();

    public Task<Guid> CreateAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid();
        Created.Enqueue((tenantId, userId, sessionId));
        return Task.FromResult(sessionId);
    }

    public Task<bool> RevokeAsync(Guid tenantId, Guid userId, Guid sessionId, CancellationToken ct)
    {
        Revoked.Enqueue((tenantId, userId, sessionId));
        return Task.FromResult(true);
    }

    /// <summary>A signed-in tenant principal as the cookie scheme hands it to a controller after validation.</summary>
    public static ClaimsPrincipal SessionPrincipal(Guid tenantId, Guid userId, Guid sessionId, UserRole role = UserRole.Owner) =>
        new(new ClaimsIdentity(TenantSessionClaims.Create(tenantId, userId, role, Guid.NewGuid(), sessionId), AuthSchemes.Tenant));
}
