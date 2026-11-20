using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// A message sent through the public contact form, or a pilot restaurant application sent through <c>/pilot</c>
/// (<see cref="ContactTopic.PilotApplication"/>, WAS-217), on the marketing site. It is stored in CentralDb before the
/// Wasla team is notified by email, so a failed or unconfigured email never loses it, and Central Admin lists it.
/// </summary>
/// <remarks>
/// Only what the sender typed is kept, plus the UI culture of the page and the time. No IP address or other request
/// data is stored. A contact form message requires the sender's acknowledgement of the contact privacy notice. A pilot
/// application shows its own privacy notice before it is sent and asks for no acknowledgement or consent, so
/// <see cref="PrivacyNoticeAcknowledged"/> is false for it; it requires a phone number and a city, and its note is
/// optional (an empty <see cref="Message"/>).
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

    /// <summary>
    /// The Turkish province of a pilot application as its two-digit plate code (<c>34</c> is İstanbul), checked against
    /// the CentralDb city catalog when the application is stored; null for a contact form message.
    /// </summary>
    public string? CityPlateCode { get; private set; }

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

        if (topic == ContactTopic.PilotApplication)
            throw new ArgumentException("A pilot application is created with CreatePilotApplication.", nameof(topic));

        return new ContactMessage
        {
            Id = Guid.NewGuid(),
            FullName = Required(fullName, ContactMessageLimits.FullNameMaxLength, nameof(fullName)),
            RestaurantName = Required(restaurantName, ContactMessageLimits.RestaurantNameMaxLength, nameof(restaurantName)),
            Email = ValidEmail(email),
            Phone = OptionalPhone(phoneDigits),
            Topic = topic,
            Message = Required(message, ContactMessageLimits.MessageMaxLength, nameof(message)),
            Culture = OptionalCulture(culture),
            PrivacyNoticeAcknowledged = true,
            CreatedAtUtc = nowUtc,
            NotificationStatus = ContactMessageNotificationStatus.Pending
        };
    }

    /// <summary>
    /// A pilot restaurant application (WAS-217). <paramref name="id"/> is the form's submission id, so a second post of
    /// the same form (a double click or a resubmitted page) meets the stored row instead of creating another. The phone
    /// number and the city are required; the note may be empty.
    /// </summary>
    public static ContactMessage CreatePilotApplication(
        Guid id,
        string restaurantName,
        string contactName,
        string email,
        string phoneDigits,
        string cityPlateCode,
        string? note,
        string? culture,
        DateTime nowUtc)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A pilot application needs its submission id.", nameof(id));

        var phone = OptionalPhone(phoneDigits)
            ?? throw new ArgumentException("A pilot application requires a phone number.", nameof(phoneDigits));

        var plate = cityPlateCode?.Trim() ?? string.Empty;
        if (!IsPlateCode(plate))
            throw new ArgumentException("A city is stored as its two-digit plate code, 01 to 81.", nameof(cityPlateCode));

        var trimmedNote = note?.Trim() ?? string.Empty;
        if (trimmedNote.Length > ContactMessageLimits.MessageMaxLength)
            throw new ArgumentException($"The note is longer than {ContactMessageLimits.MessageMaxLength} characters.", nameof(note));

        return new ContactMessage
        {
            Id = id,
            FullName = Required(contactName, ContactMessageLimits.FullNameMaxLength, nameof(contactName)),
            RestaurantName = Required(restaurantName, ContactMessageLimits.RestaurantNameMaxLength, nameof(restaurantName)),
            Email = ValidEmail(email),
            Phone = phone,
            Topic = ContactTopic.PilotApplication,
            Message = trimmedNote,
            CityPlateCode = plate,
            Culture = OptionalCulture(culture),
            PrivacyNoticeAcknowledged = false,
            CreatedAtUtc = nowUtc,
            NotificationStatus = ContactMessageNotificationStatus.Pending
        };
    }

    /// <summary>Whether <paramref name="value"/> is a Turkish plate code, two digits from <c>01</c> to <c>81</c>.</summary>
    public static bool IsPlateCode(string? value) =>
        value is { Length: ContactMessageLimits.CityPlateCodeLength }
        && char.IsAsciiDigit(value[0])
        && char.IsAsciiDigit(value[1])
        && int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 81;

    private static string ValidEmail(string email)
    {
        var normalizedEmail = Required(email, ContactMessageLimits.EmailMaxLength, nameof(email));
        var at = normalizedEmail.IndexOf('@');
        if (at <= 0 || at != normalizedEmail.LastIndexOf('@') || at == normalizedEmail.Length - 1 || normalizedEmail.Any(char.IsWhiteSpace))
            throw new ArgumentException("The email address is not valid.", nameof(email));
        return normalizedEmail;
    }

    private static string? OptionalPhone(string? phoneDigits)
    {
        var normalizedPhone = string.IsNullOrWhiteSpace(phoneDigits) ? null : phoneDigits.Trim();
        if (normalizedPhone is not null
            && (normalizedPhone.Length != ContactMessageLimits.PhoneDigits || !normalizedPhone.All(char.IsAsciiDigit)))
            throw new ArgumentException($"A phone number is stored as {ContactMessageLimits.PhoneDigits} digits.", nameof(phoneDigits));
        return normalizedPhone;
    }

    private static string? OptionalCulture(string? culture)
    {
        var normalizedCulture = string.IsNullOrWhiteSpace(culture) ? null : culture.Trim();
        if (normalizedCulture is not null && normalizedCulture.Length > ContactMessageLimits.CultureMaxLength)
            throw new ArgumentException("The culture name is too long.", nameof(culture));
        return normalizedCulture;
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
