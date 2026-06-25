namespace Wasla.Application.Abstractions.Auth;

public enum TenantPasswordResetRequestStatus
{
    EmailSent = 0,
    UserNotFound = 1,
    UserInactive = 2,
    EmailDeliveryFailed = 3
}

public enum TenantPasswordResetOutcome
{
    Success = 0,
    InvalidToken = 1,
    InvalidPassword = 2
}

public sealed class TenantPasswordResetRequestResult
{
    public TenantPasswordResetRequestStatus Status { get; init; }

    public static TenantPasswordResetRequestResult EmailSent() => new()
    {
        Status = TenantPasswordResetRequestStatus.EmailSent
    };

    public static TenantPasswordResetRequestResult UserNotFound() => new()
    {
        Status = TenantPasswordResetRequestStatus.UserNotFound
    };

    public static TenantPasswordResetRequestResult UserInactive() => new()
    {
        Status = TenantPasswordResetRequestStatus.UserInactive
    };

    public static TenantPasswordResetRequestResult EmailDeliveryFailed() => new()
    {
        Status = TenantPasswordResetRequestStatus.EmailDeliveryFailed
    };
}

public sealed class TenantPasswordResetResult
{
    public TenantPasswordResetOutcome Outcome { get; init; }

    public bool IsSuccess => Outcome == TenantPasswordResetOutcome.Success;

    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();

    public static TenantPasswordResetResult Success() => new()
    {
        Outcome = TenantPasswordResetOutcome.Success
    };

    public static TenantPasswordResetResult InvalidToken() => new()
    {
        Outcome = TenantPasswordResetOutcome.InvalidToken
    };

    public static TenantPasswordResetResult InvalidPassword(IReadOnlyList<string> validationErrors) => new()
    {
        Outcome = TenantPasswordResetOutcome.InvalidPassword,
        ValidationErrors = validationErrors
    };
}

public delegate string TenantPasswordResetUrlFactory(string rawToken);

public interface ITenantPasswordResetService
{
    /// <summary>
    /// Requests a reset for a tenant-scoped user and returns a safe structured outcome.
    /// </summary>
    Task<TenantPasswordResetRequestResult> RequestResetAsync(
        Guid tenantId,
        string email,
        TenantPasswordResetUrlFactory resetUrlFactory,
        string? cultureName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Consumes a raw reset token for one tenant and updates the user's password when valid.
    /// </summary>
    Task<TenantPasswordResetResult> ResetPasswordAsync(
        Guid tenantId,
        string rawToken,
        string newPassword,
        CancellationToken ct = default);
}
