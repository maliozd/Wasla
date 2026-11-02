namespace Wasla.Domain.Enums;

/// <summary>Whether the Wasla team was notified by email of a contact message. The message itself is always stored first.</summary>
public enum ContactMessageNotificationStatus
{
    /// <summary>Stored; the notification has not been attempted yet.</summary>
    Pending = 0,

    /// <summary>The SMTP server accepted the notification email.</summary>
    Sent = 1,

    /// <summary>Sending the notification email failed; the message is still stored.</summary>
    Failed = 2,

    /// <summary>No email was sent because email delivery is not configured (<c>Email:Provider</c> is <c>Log</c>).</summary>
    NotConfigured = 3
}
