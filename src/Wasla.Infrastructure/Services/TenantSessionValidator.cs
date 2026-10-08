using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// One primary-key read of the tenant database per validated request, projecting only the account state. It opens
/// only the database of the tenant it is given and caches nothing.
/// </summary>
public sealed class TenantSessionValidator : ITenantSessionValidator
{
    private readonly ITenantDbContextFactory _dbFactory;

    public TenantSessionValidator(ITenantDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TenantSessionState> ValidateAsync(
        Guid tenantId,
        Guid userId,
        UserRole role,
        Guid securityStamp,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        var user = await db.AppUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.Role, u.SecurityStamp })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (user is null)
            return TenantSessionState.UserNotFound;

        if (!user.IsActive)
            return TenantSessionState.UserInactive;

        if (user.SecurityStamp == Guid.Empty || user.SecurityStamp != securityStamp)
            return TenantSessionState.StampChanged;

        // A role edited without a new stamp (for example directly in the database) is not trusted either.
        if (user.Role != role)
            return TenantSessionState.RoleChanged;

        return TenantSessionState.Valid;
    }
}
