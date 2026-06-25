using Wasla.Application.Abstractions.Auth;

namespace Wasla.Infrastructure.Services;

public sealed class DefaultPasswordPolicy : IPasswordPolicy
{
    private const int MinimumLength = 8;

    public PasswordPolicyResult Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return PasswordPolicyResult.Failure("Validation.PasswordRequired");

        if (password.Length < MinimumLength)
            return PasswordPolicyResult.Failure("Validation.PasswordMinLength");

        return PasswordPolicyResult.Success();
    }
}
