using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Wasla.Application.Abstractions.Email;
using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.Email;

/// <summary>
/// Sends real email via SMTP using MailKit. SMTP credentials are read from configuration only.
/// </summary>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromEmail));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToEmail));
        mime.Subject = message.Subject;

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        };
        mime.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();

        var socketOptions = _options.EnableSsl
            ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.None;

        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, socketOptions, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
        {
            await client.AuthenticateAsync(_options.SmtpUsername, _options.SmtpPassword, ct)
                .ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Email sent via SMTP. To={ToEmail} | Subject={Subject}",
            message.ToEmail,
            message.Subject);
    }
}
