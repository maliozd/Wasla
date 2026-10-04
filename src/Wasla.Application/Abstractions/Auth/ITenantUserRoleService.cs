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
    InvalidPassword
}

public sealed record TenantUserRoleUpdateResult(TenantUserRoleUpdateOutcome Outcome)
{
    public bool Succeeded => Outcome == TenantUserRoleUpdateOutcome.Updated;

    public static TenantUserRoleUpdateResult Updated() => new(TenantUserRoleUpdateOutcome.Updated);

    public static TenantUserRoleUpdateResult UserNotFound() => new(TenantUserRoleUpdateOutcome.UserNotFound);

    public static TenantUserRoleUpdateResult LastOwnerWouldBeRemoved() =>
        new(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved);
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
}

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
        TenantUserCreateCommand command,
        CancellationToken ct);

    Task<TenantUserMutationResult> UpdateUserAsync(
        Guid tenantId,
        Guid userId,
        TenantUserUpdateCommand command,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
        Guid tenantId,
        Guid userId,
        UserRole role,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> SetActiveAsync(
        Guid tenantId,
        Guid userId,
        bool isActive,
        CancellationToken ct);

    Task<TenantUserRoleUpdateResult> RemoveAsync(
        Guid tenantId,
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
