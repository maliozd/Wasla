namespace OrderHub.Application.Abstractions.Admin;

public interface ICentralAdminAuthService
{
    Task<bool> ValidateAsync(string email, string password, CancellationToken ct);
}
