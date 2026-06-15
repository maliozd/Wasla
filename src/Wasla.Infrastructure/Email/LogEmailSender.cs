using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Email;

namespace Wasla.Infrastructure.Email;

/// <summary>
/// Development-safe sender: logs recipient, subject, and body preview. Does not send real email.
/// </summary>
internal sealed class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var preview = message.TextBody.Length > 200
            ? string.Concat(message.TextBody.AsSpan(0, 200), "...")
            : message.TextBody;

        _logger.LogInformation(
            "[Email:Log] To={ToEmail} | Subject={Subject} | Body preview: {Preview}",
            message.ToEmail,
            message.Subject,
            preview);

        return Task.CompletedTask;
    }
}
