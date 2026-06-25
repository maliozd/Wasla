using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Email;

namespace Wasla.Infrastructure.Email;

public sealed class LogEmailSender : IEmailSender
{
    private const int BodyPreviewLength = 240;

    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(message);

        var recipient = string.IsNullOrWhiteSpace(message.ToName)
            ? message.ToEmail
            : $"{message.ToName} <{message.ToEmail}>";

        if (message.IsSensitive)
        {
            _logger.LogInformation(
                "Email (Log provider): RecipientDomain={RecipientDomain} Subject={Subject} BodyPreviewSuppressed=true",
                GetDomain(message.ToEmail),
                message.Subject);
        }
        else
        {
            var preview = BuildBodyPreview(message.TextBody);
            _logger.LogInformation(
                "Email (Log provider): To={Recipient} Subject={Subject} Preview={Preview}",
                recipient,
                message.Subject,
                preview);
        }

        return Task.CompletedTask;
    }

    private static string BuildBodyPreview(string textBody)
    {
        if (string.IsNullOrWhiteSpace(textBody))
            return "(empty body)";

        var normalized = textBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= BodyPreviewLength)
            return normalized;

        return normalized[..BodyPreviewLength] + "…";
    }

    private static string GetDomain(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return "unknown";

        return email[(at + 1)..].Trim();
    }
}
