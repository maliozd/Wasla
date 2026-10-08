using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Auth;

public enum TenantUserRoleUpdateOutcome
{
    Updated,
    Created,
    UserNotFound,
    LastOwnerWouldBeRemoved,
    InvalidRole,
    DuplicateEmail,
    InvalidPassword,

    /// <summary>The acting user may not change their own role or active state, or remove themselves.</summary>
    SelfChangeNotAllowed,

    /// <summary>
    /// The acting user is no longer an active Owner with the session's security stamp when the change is written
    /// (for example, demoted or deactivated by another request in the meantime). Nothing was changed.
    /// </summary>
    ActorNotAuthorized
}

/// <summary>
/// The signed-in user making a user-management change, as identified by their validated tenant session.
/// </summary>
public sealed record TenantUserActor(Guid UserId, Guid SecurityStamp);

public sealed record TenantUserRoleUpdateResult(TenantUserRoleUpdateOutcome Outcome)
{
    public bool Succeeded => Outcome == TenantUserRoleUpdateOutcome.Updated;

    public static TenantUserRoleUpdateResult Updated() => new(TenantUserRoleUpdateOutcome.Updated);

    public static TenantUserRoleUpdateResult UserNotFound() => new(TenantUserRoleUpdateOutcome.UserNotFound);

    public static TenantUserRoleUpdateResult LastOwnerWouldBeRemoved() =>
        new(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved);

    public static TenantUserRoleUpdateResult SelfChangeNotAllowed() =>
        new(TenantUserRoleUpdateOutcome.SelfChangeNotAllowed);

    public static TenantUserRoleUpdateResult ActorNotAuthorized() =>
        new(TenantUserRoleUpdateOutcome.ActorNotAuthorized);
}

public sealed record TenantUserMutationResult(TenantUserRoleUpdateOutcome Outcome, Guid? UserId = null)
{
    public bool Succeeded => Outcome is TenantUserRoleUpdateOutcome.Created or TenantUserRoleUpdateOutcome.Updated;

    public IReadOnlyList<string> PasswordErrors { get; init; } = Array.Empty<string>();

    public static TenantUserMutationResult Created(Guid userId) => new(TenantUserRoleUpdateOutcome.Created, userId);

    public static TenantUserMutationResult Updated(Guid userId) => new(TenantUserRoleUpdateOutcome.Updated, userId);

    public static TenantUserMutationResult UserNotFound() => new(TenantUserRoleUpdateOutcome.UserNotFound);

    public static TenantUserMutationResult LastOwnerWouldBeRemoved(Guid userId) =>
        new(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved, userId);

    public static TenantUserMutationResult InvalidRole() => new(TenantUserRoleUpdateOutcome.InvalidRole);

    public static TenantUserMutationResult DuplicateEmail() => new(TenantUserRoleUpdateOutcome.DuplicateEmail);

    public static TenantUserMutationResult InvalidPassword(IReadOnlyList<string> errors) =>
        new(TenantUserRoleUpdateOutcome.InvalidPassword)
        {
            PasswordErrors = errors
        };

    public static TenantUserMutationResult SelfChangeNotAllowed(Guid userId) =>
        new(TenantUserRoleUpdateOutcome.SelfChangeNotAllowed, userId);

    public static TenantUserMutationResult ActorNotAuthorized() => new(TenantUserRoleUpdateOutcome.ActorNotAuthorized);
}

/// <summary>
/// Tenant user management. Every change is made on behalf of an <see cref="TenantUserActor"/> and, in the same
/// serializable transaction as the write, re-checks that the actor is still an active Owner with the session's security
/// stamp (the server-side form of the <c>CanManageTenantUsers</c> policy), that the actor is not changing their own role
/// or active state, and that the tenant keeps an active Owner. A change to a user's role, active state or password
/// replaces that user's security stamp, which ends their existing sessions.
/// </summary>
public interface ITenantUserRoleService
{
    Task<IReadOnlyList<TenantUserSummaryDto>> ListUsersAsync(
        Guid tenantId,
        CancellationToken ct);

    Task<TenantUserSummaryDto?> GetUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct);

    Task<TenantUserMutationResult> CreateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        TenantUserCreateCommand command,
        CancellationToken ct);

    Task<TenantUserMutationResult> UpdateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        TenantUserUpdateCommand command,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        UserRole role,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> SetActiveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        bool isActive,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> RemoveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        CancellationToken ct);
}

public sealed record TenantUserSummaryDto(
    Guid Id,
    string Email,
    string FullName,
    UserRole Role,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed record TenantUserCreateCommand(
    string Email,
    string FullName,
    UserRole Role,
    bool IsActive,
    string Password);

public sealed record TenantUserUpdateCommand(
    string FullName,
    UserRole Role,
    bool IsActive,
    string? NewPassword);
