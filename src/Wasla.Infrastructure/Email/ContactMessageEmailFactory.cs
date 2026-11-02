using System.Globalization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.Email;

/// <summary>
/// The internal notification to the Wasla team for a contact message, in Turkish. The subject names only the topic;
/// the sender's details are in the body, HTML-encoded, and the message is sensitive so no log carries its content.
/// </summary>
public sealed class ContactMessageEmailFactory
{
    private readonly EmailTemplateRenderer _renderer;
    private readonly EmailOptions _emailOptions;
    private readonly ContactOptions _contactOptions;

    public ContactMessageEmailFactory(
        EmailTemplateRenderer renderer,
        IOptions<EmailOptions> emailOptions,
        IOptions<ContactOptions> contactOptions)
    {
        _renderer = renderer;
        _emailOptions = emailOptions.Value;
        _contactOptions = contactOptions.Value;
    }

    public EmailMessage BuildNotification(ContactMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var topic = TopicLabel(message.Topic);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FullName"] = message.FullName,
            ["RestaurantName"] = message.RestaurantName,
            ["Email"] = message.Email,
            ["Phone"] = FormatPhone(message.Phone),
            ["Topic"] = topic,
            ["Message"] = message.Message,
            ["Culture"] = message.Culture ?? "-",
            ["CreatedAtUtc"] = message.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            ["MessageId"] = message.Id.ToString(),
            ["FromName"] = _emailOptions.FromName.Trim()
        };

        return new EmailMessage
        {
            ToEmail = _contactOptions.RecipientEmail.Trim(),
            Subject = $"Wasla iletişim formu: yeni mesaj ({topic})",
            HtmlBody = _renderer.Render("ContactMessageReceived.tr.html", tokens, htmlEncodeValues: true),
            TextBody = _renderer.Render("ContactMessageReceived.tr.txt", tokens, htmlEncodeValues: false),
            IsSensitive = true
        };
    }

    public static string TopicLabel(ContactTopic topic) => topic switch
    {
        ContactTopic.WaslaOrders => "Wasla Orders",
        ContactTopic.WaslaPos => "Wasla POS",
        ContactTopic.PrintBridgeSetup => "Print Bridge kurulumu",
        ContactTopic.Pricing => "Fiyatlandırma",
        _ => "Diğer"
    };

    private static string FormatPhone(string? digits) =>
        digits is { Length: ContactMessageLimits.PhoneDigits }
            ? $"+90 {digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..]}"
            : "-";
}
