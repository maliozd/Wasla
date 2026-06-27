using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class TenantUserRoleService : ITenantUserRoleService
{
    private readonly ITenantDbContextFactory _dbFactory;

    public TenantUserRoleService(ITenantDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
        Guid tenantId,
        Guid userId,
        UserRole role,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (user.Role == UserRole.Owner
            && role != UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> SetActiveAsync(
        Guid tenantId,
        Guid userId,
        bool isActive,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (!isActive
            && user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> RemoveAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    private static async Task<AppUser?> FindUserAsync(
        TenantDbContext db,
        Guid userId,
        CancellationToken ct) =>
        await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);

    private static async Task<bool> HasAnotherActiveOwnerAsync(
        TenantDbContext db,
        Guid userId,
        CancellationToken ct) =>
        await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id != userId && u.IsActive && u.Role == UserRole.Owner, ct)
            .ConfigureAwait(false);
}
