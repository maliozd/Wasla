namespace Wasla.Application.Abstractions.Admin;

public sealed record CentralAdminLoginResult(
    bool Succeeded,
    Guid? UserId,
    string? Email,
    string? DisplayName,
    string? ErrorCode,
    Guid? SecurityStamp = null);

public interface ICentralAdminAuthService
{
    Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct);
}

public enum CentralAdminSessionState
{
    Valid,
    AccountNotFound,
    AccountInactive,
    StampChanged
}

/// <summary>
/// Checks an issued Central Admin session against the account's current state in CentralDb. It reads CentralDb only and
/// caches nothing, so a deactivation or credential change takes effect on the session's next request.
/// </summary>
public interface ICentralAdminSessionValidator
{
    Task<CentralAdminSessionState> ValidateAsync(Guid adminUserId, Guid securityStamp, CancellationToken ct);
}
