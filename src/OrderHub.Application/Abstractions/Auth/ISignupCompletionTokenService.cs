using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Auth;

public sealed record SignupCompletionPayload(
    Guid CustomerId,
    Guid UserId,
    string Email,
    string FullName,
    UserRole Role);

public interface ISignupCompletionTokenService
{
    string CreateToken(SignupCompletionPayload payload);

    SignupCompletionPayload? ValidateAndConsume(string? token);
}
