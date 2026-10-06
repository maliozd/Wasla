using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class TenantUserRoleService : ITenantUserRoleService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IPasswordPolicy _passwordPolicy;

    public TenantUserRoleService(ITenantDbContextFactory dbFactory, IPasswordPolicy passwordPolicy)
    {
        _dbFactory = dbFactory;
        _passwordPolicy = passwordPolicy;
    }

    public async Task<IReadOnlyList<TenantUserSummaryDto>> ListUsersAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        return await db.AppUsers
            .AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new TenantUserSummaryDto(
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantUserSummaryDto?> GetUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        return await db.AppUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new TenantUserSummaryDto(
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantUserMutationResult> CreateUserAsync(
        Guid tenantId,
        TenantUserCreateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        var passwordResult = _passwordPolicy.Validate(command.Password);
        if (!passwordResult.IsValid)
            return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var email = NormalizeEmail(command.Email);
        var emailExists = await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Email == email, ct)
            .ConfigureAwait(false);
        if (emailExists)
            return TenantUserMutationResult.DuplicateEmail();

        var now = DateTime.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = command.FullName.Trim(),
            Role = command.Role,
            IsActive = command.IsActive,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(command.Password),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AppUsers.Add(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Created(user.Id);
    }

    public async Task<TenantUserMutationResult> UpdateUserAsync(
        Guid tenantId,
        Guid userId,
        TenantUserUpdateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        if (!string.IsNullOrWhiteSpace(command.NewPassword))
        {
            var passwordResult = _passwordPolicy.Validate(command.NewPassword);
            if (!passwordResult.IsValid)
                return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);
        }

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserMutationResult.UserNotFound();

        if (user.Role == UserRole.Owner
            && command.Role != UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserMutationResult.LastOwnerWouldBeRemoved(userId);
        }

        if (!command.IsActive
            && user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserMutationResult.LastOwnerWouldBeRemoved(userId);
        }

        user.FullName = command.FullName.Trim();
        user.Role = command.Role;
        user.IsActive = command.IsActive;
        if (!string.IsNullOrWhiteSpace(command.NewPassword))
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(command.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Updated(user.Id);
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

    private static bool IsAllowedRole(UserRole role) =>
        role is UserRole.Owner or UserRole.Manager or UserRole.Kitchen or UserRole.Cashier or UserRole.Viewer;

    private static string NormalizeEmail(string email) => email.Trim();
}
