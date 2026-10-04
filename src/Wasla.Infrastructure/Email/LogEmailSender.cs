using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Email;

namespace Wasla.Infrastructure.Email;

public sealed class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogInformation(
            "Email logged (not sent). RecipientDomain={RecipientDomain} Subject={Subject}",
            GetDomain(message.ToEmail),
            message.Subject);

        return Task.CompletedTask;
    }

    private static string GetDomain(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return "unknown";

        return email[(at + 1)..].Trim();
    }
}
