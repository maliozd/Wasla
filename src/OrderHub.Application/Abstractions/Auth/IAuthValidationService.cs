namespace OrderHub.Application.Abstractions.Auth;

public interface IAuthValidationService
{
    Task<AuthSessionResult?> ValidateAsync(Guid customerId, string email, string password, CancellationToken ct);
}

