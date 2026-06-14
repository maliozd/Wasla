namespace Wasla.Application.Auth.Services;

public interface IAuthService
{
    Task<bool> LoginAsync(string email, string password, CancellationToken ct);

    Task LogoutAsync(CancellationToken ct);
}

