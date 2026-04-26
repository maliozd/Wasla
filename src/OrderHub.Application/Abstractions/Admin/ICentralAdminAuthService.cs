namespace OrderHub.Application.Abstractions.Admin;

public sealed record CentralAdminLoginResult(
    bool Succeeded,
    Guid? UserId,
    string? Email,
    string? DisplayName,
    string? ErrorCode);

public interface ICentralAdminAuthService
{
    Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct);
}
