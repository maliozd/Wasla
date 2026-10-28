using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// A message sent through the public contact form on the marketing site. It is stored in CentralDb before the Wasla
/// team is notified by email, so a failed or unconfigured email never loses it, and Central Admin lists it.
/// </summary>
/// <remarks>
/// Only what the sender typed is kept, plus the UI culture of the page and the time. No IP address or other request
/// data is stored. The sender acknowledged the privacy notice (Aydınlatma Metni) before sending; a message without that
/// acknowledgement is not created.
/// </remarks>
public sealed class ContactMessage
{
    private ContactMessage()
    {
    }

    public Guid Id { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string RestaurantName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    /// <summary>A Turkish phone number as ten digits without the country code, or null when the sender left it empty.</summary>
    public string? Phone { get; private set; }

    public ContactTopic Topic { get; private set; }

    public string Message { get; private set; } = string.Empty;

    /// <summary>The UI culture of the contact page when the message was sent, for example <c>tr-TR</c>.</summary>
    public string? Culture { get; private set; }

    public bool PrivacyNoticeAcknowledged { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public ContactMessageNotificationStatus NotificationStatus { get; private set; }

    public DateTime? NotificationAttemptedAtUtc { get; private set; }

    public static ContactMessage Create(
        string fullName,
        string restaurantName,
        string email,
        string? phoneDigits,
        ContactTopic topic,
        string message,
        string? culture,
        bool privacyNoticeAcknowledged,
        DateTime nowUtc)
    {
        if (!privacyNoticeAcknowledged)
            throw new ArgumentException("A contact message requires the privacy notice acknowledgement.", nameof(privacyNoticeAcknowledged));
        if (!Enum.IsDefined(topic))
            throw new ArgumentOutOfRangeException(nameof(topic));

        var normalizedEmail = Required(email, ContactMessageLimits.EmailMaxLength, nameof(email));
        var at = normalizedEmail.IndexOf('@');
        if (at <= 0 || at != normalizedEmail.LastIndexOf('@') || at == normalizedEmail.Length - 1 || normalizedEmail.Any(char.IsWhiteSpace))
            throw new ArgumentException("The email address is not valid.", nameof(email));

        var normalizedPhone = string.IsNullOrWhiteSpace(phoneDigits) ? null : phoneDigits.Trim();
        if (normalizedPhone is not null
            && (normalizedPhone.Length != ContactMessageLimits.PhoneDigits || !normalizedPhone.All(char.IsAsciiDigit)))
            throw new ArgumentException($"A phone number is stored as {ContactMessageLimits.PhoneDigits} digits.", nameof(phoneDigits));

        var normalizedCulture = string.IsNullOrWhiteSpace(culture) ? null : culture.Trim();
        if (normalizedCulture is not null && normalizedCulture.Length > ContactMessageLimits.CultureMaxLength)
            throw new ArgumentException("The culture name is too long.", nameof(culture));

        return new ContactMessage
        {
            Id = Guid.NewGuid(),
            FullName = Required(fullName, ContactMessageLimits.FullNameMaxLength, nameof(fullName)),
            RestaurantName = Required(restaurantName, ContactMessageLimits.RestaurantNameMaxLength, nameof(restaurantName)),
            Email = normalizedEmail,
            Phone = normalizedPhone,
            Topic = topic,
            Message = Required(message, ContactMessageLimits.MessageMaxLength, nameof(message)),
            Culture = normalizedCulture,
            PrivacyNoticeAcknowledged = true,
            CreatedAtUtc = nowUtc,
            NotificationStatus = ContactMessageNotificationStatus.Pending
        };
    }

    public void MarkNotificationSent(DateTime nowUtc) => RecordNotification(ContactMessageNotificationStatus.Sent, nowUtc);

    public void MarkNotificationFailed(DateTime nowUtc) => RecordNotification(ContactMessageNotificationStatus.Failed, nowUtc);

    public void MarkNotificationNotConfigured(DateTime nowUtc) => RecordNotification(ContactMessageNotificationStatus.NotConfigured, nowUtc);

    private void RecordNotification(ContactMessageNotificationStatus status, DateTime nowUtc)
    {
        NotificationStatus = status;
        NotificationAttemptedAtUtc = nowUtc;
    }

    private static string Required(string value, int maxLength, string parameterName)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException("The value is required.", parameterName);
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"The value is longer than {maxLength} characters.", parameterName);
        return trimmed;
    }
}
