using System.Linq.Expressions;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.Application.Contact;

/// <summary>
/// The one retention rule for pilot applications (WAS-222, approved 22 November 2026, legal wording still under review):
/// a pilot application's personal data is kept for <see cref="Period"/> after its last meaningful activity (received,
/// status changed, or a contact with the restaurant recorded) and then deleted. The same rule holds whatever the status:
/// an unanswered, declined or accepted application is deleted <see cref="Period"/> after its last activity, and an
/// application the team is still talking about is kept because every recorded contact moves the activity forward.
/// Contact form messages are never selected (their <c>PilotLastActivityAtUtc</c> is null and their topic differs).
/// </summary>
/// <remarks>
/// <para>Times are UTC. The boundary is inclusive: an application whose last activity was at <c>T</c> may be deleted at
/// <c>T + Period</c> exactly (<see cref="DeleteOnOrAfter"/>) and is kept at any earlier instant.</para>
/// <para>An application whose team notification is being sent right now (a claim taken less than
/// <see cref="ContactNotificationRetry.StuckAfter"/> ago) is not deleted; a claim older than that belongs to an attempt
/// that ended at its <see cref="ContactNotificationRetry.SendTimeout"/> deadline long ago and holds nothing back.</para>
/// <para><see cref="ExpiredAt"/> is the rule as a query, used by the cleanup's selection and inside its conditional
/// delete; <see cref="IsExpired"/> and <see cref="HasActiveOperation"/> are the same rule for a row already read (Central
/// Admin), and a test holds the forms together.</para>
/// </remarks>
public static class PilotApplicationRetention
{
    public const int PeriodDays = 90;

    public static readonly TimeSpan Period = TimeSpan.FromDays(PeriodDays);

    /// <summary>The first instant at which an application last active at <paramref name="lastActivityAtUtc"/> may be deleted.</summary>
    public static DateTime DeleteOnOrAfter(DateTime lastActivityAtUtc) => lastActivityAtUtc + Period;

    public static bool IsExpired(DateTime lastActivityAtUtc, DateTime nowUtc) => DeleteOnOrAfter(lastActivityAtUtc) <= nowUtc;

    /// <summary>Whether a team notification attempt may still be running for the row.</summary>
    public static bool HasActiveOperation(Guid? notificationClaimId, DateTime? notificationAttemptedAtUtc, DateTime createdAtUtc, DateTime nowUtc) =>
        notificationClaimId is not null && (notificationAttemptedAtUtc ?? createdAtUtc) > nowUtc - ContactNotificationRetry.StuckAfter;

    /// <summary>
    /// Pilot applications whose retention period has ended at <paramref name="nowUtc"/> and that no running operation
    /// holds: the rows the cleanup deletes.
    /// </summary>
    public static Expression<Func<ContactMessage, bool>> ExpiredAt(DateTime nowUtc)
    {
        var lastActivityBefore = nowUtc - Period;
        var activeSince = nowUtc - ContactNotificationRetry.StuckAfter;
        return m => m.Topic == ContactTopic.PilotApplication
            && m.PilotLastActivityAtUtc != null
            && m.PilotLastActivityAtUtc <= lastActivityBefore
            && (m.NotificationClaimId == null || (m.NotificationAttemptedAtUtc ?? m.CreatedAtUtc) <= activeSince);
    }

    /// <summary>
    /// A pilot application that no running operation holds at <paramref name="nowUtc"/>, whatever its age: the condition
    /// of a deletion on a data subject's request.
    /// </summary>
    public static Expression<Func<ContactMessage, bool>> DeletableOnRequestAt(DateTime nowUtc)
    {
        var activeSince = nowUtc - ContactNotificationRetry.StuckAfter;
        return m => m.Topic == ContactTopic.PilotApplication
            && (m.NotificationClaimId == null || (m.NotificationAttemptedAtUtc ?? m.CreatedAtUtc) <= activeSince);
    }
}
