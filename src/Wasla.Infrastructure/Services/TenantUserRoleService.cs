using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Every change runs in one serializable transaction that first re-reads the acting user, so an actor who was demoted,
/// deactivated, deleted or given a new security stamp between the request's session check and the write changes
/// nothing. On SQL Server the serializable read locks also keep two concurrent changes from each relying on the other's
/// Owner: one waits for the other, or is chosen as the deadlock victim and fails without writing.
/// </summary>
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
        TenantUserActor actor,
        TenantUserCreateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        var passwordResult = _passwordPolicy.Validate(command.Password);
        if (!passwordResult.IsValid)
            return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserMutationResult.ActorNotAuthorized();

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
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Created(user.Id);
    }

    public async Task<TenantUserMutationResult> UpdateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        TenantUserUpdateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        var changesPassword = !string.IsNullOrWhiteSpace(command.NewPassword);
        if (changesPassword)
        {
            var passwordResult = _passwordPolicy.Validate(command.NewPassword!);
            if (!passwordResult.IsValid)
                return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);
        }

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserMutationResult.ActorNotAuthorized();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserMutationResult.UserNotFound();

        var changesRole = user.Role != command.Role;
        var changesActive = user.IsActive != command.IsActive;
        if (userId == actor.UserId && (changesRole || changesActive))
            return TenantUserMutationResult.SelfChangeNotAllowed(userId);

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
        if (changesPassword)
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(command.NewPassword);
        if (changesRole || changesActive || changesPassword)
            user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Updated(user.Id);
    }

    public async Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        UserRole role,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (user.Role == UserRole.Owner
            && role != UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        if (user.Role != role)
            user.SecurityStamp = Guid.NewGuid();
        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> SetActiveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        bool isActive,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

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

        if (user.IsActive != isActive)
            user.SecurityStamp = Guid.NewGuid();
        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> RemoveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

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
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    private static Task<IDbContextTransaction> BeginAsync(TenantDbContext db, CancellationToken ct) =>
        db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    /// <summary>
    /// The actor must still be an active Owner (the role of the <c>CanManageTenantUsers</c> policy) with the security
    /// stamp their session was validated with.
    /// </summary>
    private static async Task<bool> IsAuthorizedActorAsync(
        TenantDbContext db,
        TenantUserActor actor,
        CancellationToken ct) =>
        actor.UserId != Guid.Empty
        && actor.SecurityStamp != Guid.Empty
        && await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id == actor.UserId
                           && u.IsActive
                           && u.Role == UserRole.Owner
                           && u.SecurityStamp == actor.SecurityStamp, ct)
            .ConfigureAwait(false);

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
