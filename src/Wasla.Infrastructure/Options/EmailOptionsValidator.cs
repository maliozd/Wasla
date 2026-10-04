using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Options;

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.Equals(options.Provider, "Log", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Success;

        if (!string.Equals(options.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Email:Provider must be either 'Log' or 'Smtp'.");
        }

        var failures = new List<string>();
        Require(options.FromEmail, "Email:FromEmail", failures);
        Require(options.FromName, "Email:FromName", failures);
        Require(options.SmtpHost, "Email:SmtpHost", failures);
        Require(options.SmtpUsername, "Email:SmtpUsername", failures);
        Require(options.SmtpPassword, "Email:SmtpPassword", failures);

        if (options.SmtpPort <= 0)
            failures.Add("Email:SmtpPort must be greater than zero.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(string? value, string key, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{key} is required when Email:Provider is Smtp.");
    }
}
