using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.Email;

public static class EmailOptionsDiagnostics
{
    public static bool SenderMatchesAuthenticatedAccount(EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.FromEmail)
            || string.IsNullOrWhiteSpace(options.SmtpUsername))
            return false;

        return string.Equals(
            options.FromEmail.Trim(),
            options.SmtpUsername.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
