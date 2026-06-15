using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.Email;

public sealed class ProvisioningEmailFactory
{
    private const string PanelReadySubject = "Wasla restoran paneliniz hazır";

    private readonly EmailTemplateRenderer _renderer;
    private readonly EmailOptions _options;

    public ProvisioningEmailFactory(EmailTemplateRenderer renderer, IOptions<EmailOptions> options)
    {
        _renderer = renderer;
        _options = options.Value;
    }

    public EmailMessage BuildPanelReadyEmail(PendingRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var ownerFullName = registration.OwnerFullName.Trim();
        var ownerEmail = registration.OwnerEmail.Trim();
        var primaryDomain = registration.PrimaryDomain.Trim();
        var loginUrl = $"https://{primaryDomain}/auth/login";
        var fromName = _options.FromName.Trim();

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OwnerFullName"] = ownerFullName,
            ["PrimaryDomain"] = primaryDomain,
            ["LoginUrl"] = loginUrl,
            ["FromName"] = fromName
        };

        var htmlBody = _renderer.Render("PanelReady.tr.html", tokens, htmlEncodeValues: true);
        var textBody = _renderer.Render("PanelReady.tr.txt", tokens, htmlEncodeValues: false);

        return new EmailMessage
        {
            ToEmail = ownerEmail,
            ToName = ownerFullName,
            Subject = PanelReadySubject,
            HtmlBody = htmlBody,
            TextBody = textBody
        };
    }
}
