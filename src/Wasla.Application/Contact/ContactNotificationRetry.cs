using System.Linq.Expressions;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.Application.Contact;

/// <summary>
/// When a stored message's team notification needs attention and Central Admin may send it again: after it failed,
/// after it was skipped because email delivery was not configured, or when an attempt started more than
/// <see cref="StuckAfter"/> ago and never recorded an outcome (the process stopped between storing the message and
/// recording the email). A message whose notification was sent, or whose attempt started recently, is not sent again.
/// </summary>
/// <remarks>
/// One attempt is cut off after <see cref="SendTimeout"/>, far inside <see cref="StuckAfter"/>, so an attempt that is
/// still running is never taken for a stuck one. <see cref="NeedsAttentionAt"/> is the rule as a query (the resend claim
/// and the Central Admin filter use it); <see cref="CanResend"/> is the same rule for a row already read, and a test
/// holds the two together.
/// </remarks>
public static class ContactNotificationRetry
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(15);

    /// <summary>The longest one notification attempt may take, the SMTP connection, sign-in and send together.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(3);

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

    /// <summary><see cref="CanResend"/> as a query over stored messages, evaluated by the database.</summary>
    public static Expression<Func<ContactMessage, bool>> NeedsAttentionAt(DateTime nowUtc)
    {
        var stuckBefore = nowUtc - StuckAfter;
        return m => m.NotificationStatus == ContactMessageNotificationStatus.Failed
            || m.NotificationStatus == ContactMessageNotificationStatus.NotConfigured
            || (m.NotificationStatus == ContactMessageNotificationStatus.Pending
                && (m.NotificationAttemptedAtUtc ?? m.CreatedAtUtc) <= stuckBefore);
    }

    /// <summary>Pending attempts that started more than <see cref="StuckAfter"/> ago and never recorded an outcome.</summary>
    public static Expression<Func<ContactMessage, bool>> StuckAt(DateTime nowUtc)
    {
        var stuckBefore = nowUtc - StuckAfter;
        return m => m.NotificationStatus == ContactMessageNotificationStatus.Pending
            && (m.NotificationAttemptedAtUtc ?? m.CreatedAtUtc) <= stuckBefore;
    }
}
