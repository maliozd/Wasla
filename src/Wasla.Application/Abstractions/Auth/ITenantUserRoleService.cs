using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Auth;

public enum TenantUserRoleUpdateOutcome
{
    Updated,
    UserNotFound,
    LastOwnerWouldBeRemoved
}

public sealed record TenantUserRoleUpdateResult(TenantUserRoleUpdateOutcome Outcome)
{
    public bool Succeeded => Outcome == TenantUserRoleUpdateOutcome.Updated;

    public static TenantUserRoleUpdateResult Updated() => new(TenantUserRoleUpdateOutcome.Updated);

    public static TenantUserRoleUpdateResult UserNotFound() => new(TenantUserRoleUpdateOutcome.UserNotFound);

    public static TenantUserRoleUpdateResult LastOwnerWouldBeRemoved() =>
        new(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved);
}

public interface ITenantUserRoleService
{
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
