namespace Wasla.Application.Abstractions.Auth;

public sealed class PasswordPolicyResult
{
    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static PasswordPolicyResult Success() => new();

    public static PasswordPolicyResult Failure(params string[] errors) => new()
    {
        Errors = errors
    };
}

public interface IPasswordPolicy
{
    PasswordPolicyResult Validate(string password);
}
