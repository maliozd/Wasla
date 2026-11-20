using Wasla.Domain.Enums;

namespace Wasla.Application.Contact;

/// <summary>
/// When Central Admin may send a stored message's team notification again: after it failed, after it was skipped
/// because email delivery was not configured, or when an attempt started more than <see cref="StuckAfter"/> ago and never
/// recorded an outcome (the process stopped between storing the message and recording the email). A message whose
/// notification was sent, or whose attempt started recently, is not sent again.
/// </summary>
public static class ContactNotificationRetry
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(15);

    public static bool CanResend(
        ContactMessageNotificationStatus status,
        DateTime createdAtUtc,
        DateTime? attemptedAtUtc,
        DateTime nowUtc) =>
        status switch
        {
            ContactMessageNotificationStatus.Failed or ContactMessageNotificationStatus.NotConfigured => true,
            ContactMessageNotificationStatus.Pending => (attemptedAtUtc ?? createdAtUtc) <= nowUtc - StuckAfter,
            _ => false
        };
}
