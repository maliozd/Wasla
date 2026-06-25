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
public sealed class SmtpEmailSender : IEmailSender
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
        ArgumentNullException.ThrowIfNull(message);
        ValidateOptions();

        var mime = BuildMimeMessage(message);

        using var client = new SmtpClient();
        var secureSocketOptions = _options.EnableSsl
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(_options.SmtpHost.Trim(), _options.SmtpPort, secureSocketOptions, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
        {
            await client.AuthenticateAsync(_options.SmtpUsername.Trim(), _options.SmtpPassword, ct)
                .ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Email sent via SMTP. RecipientDomain={RecipientDomain} Subject={Subject}",
            GetDomain(message.ToEmail),
            message.Subject);
    }

    private MimeMessage BuildMimeMessage(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName.Trim(), _options.FromEmail.Trim()));

        if (string.IsNullOrWhiteSpace(message.ToName))
            mime.To.Add(MailboxAddress.Parse(message.ToEmail.Trim()));
        else
            mime.To.Add(new MailboxAddress(message.ToName.Trim(), message.ToEmail.Trim()));

        mime.Subject = message.Subject;

        var bodyBuilder = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody
        };
        mime.Body = bodyBuilder.ToMessageBody();

        return mime;
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.FromEmail))
            throw new InvalidOperationException("Email:FromEmail is required when Email:Provider is Smtp.");

        if (string.IsNullOrWhiteSpace(_options.FromName))
            throw new InvalidOperationException("Email:FromName is required when Email:Provider is Smtp.");

        if (string.IsNullOrWhiteSpace(_options.SmtpHost))
            throw new InvalidOperationException("Email:SmtpHost is required when Email:Provider is Smtp.");

        if (_options.SmtpPort <= 0)
            throw new InvalidOperationException("Email:SmtpPort must be greater than zero when Email:Provider is Smtp.");

        if (string.IsNullOrWhiteSpace(_options.SmtpUsername))
            throw new InvalidOperationException("Email:SmtpUsername is required when Email:Provider is Smtp.");

        if (string.IsNullOrWhiteSpace(_options.SmtpPassword))
            throw new InvalidOperationException("Email:SmtpPassword is required when Email:Provider is Smtp.");
    }

    private static string GetDomain(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return "unknown";

        return email[(at + 1)..].Trim();
    }
}
