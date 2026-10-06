namespace Wasla.Application.Abstractions.Auth;

/// <summary>
/// Records a completed tenant sign-in (the user's last login time). Call it only after the session has been
/// established. A credential check on its own (<see cref="IAuthValidationService"/>, for example
/// <c>POST /api/auth/validate</c>) is not a login and must not record one.
/// </summary>
public interface ITenantLoginRecorder
{
    Task RecordSuccessfulLoginAsync(Guid tenantId, Guid userId, CancellationToken ct);
}
