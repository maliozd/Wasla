using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Contact;

/// <summary>A message from the public contact form, already validated by the page. <see cref="Phone"/> is ten digits or null.</summary>
public sealed record ContactMessageRequest(
    string FullName,
    string RestaurantName,
    string Email,
    string? Phone,
    ContactTopic Topic,
    string Message,
    string? Culture,
    bool PrivacyNoticeAcknowledged);

public sealed record ContactMessageSubmitResult(Guid MessageId, ContactMessageNotificationStatus NotificationStatus);

/// <summary>
/// A pilot restaurant application from <c>/pilot</c> (WAS-217), already validated by the page. <see cref="SubmissionId"/>
/// comes from the rendered form and becomes the stored id, so posting the same form twice stores one application.
/// <see cref="Phone"/> is ten digits; <see cref="CityPlateCode"/> is a two-digit Turkish plate code.
/// </summary>
public sealed record PilotApplicationRequest(
    Guid SubmissionId,
    string RestaurantName,
    string ContactName,
    string Email,
    string Phone,
    string CityPlateCode,
    string? Note,
    string? Culture);

/// <summary>
/// The stored application. <see cref="AlreadyReceived"/> is true when the same form had already been stored: nothing
/// new was written and no second notification was sent.
/// </summary>
public sealed record PilotApplicationSubmitResult(Guid ApplicationId, ContactMessageNotificationStatus NotificationStatus, bool AlreadyReceived);

/// <summary>The outcome of a Central Admin request to send a stored message's team notification again.</summary>
public enum ContactNotificationResendOutcome
{
    /// <summary>The notification was attempted again; <see cref="ContactNotificationResendResult.NotificationStatus"/> says how it ended.</summary>
    Attempted,

    /// <summary>The team was already notified; nothing was sent.</summary>
    AlreadySent,

    /// <summary>Another attempt for this message started moments ago; nothing was sent.</summary>
    InProgress,

    NotFound
}

public sealed record ContactNotificationResendResult(ContactNotificationResendOutcome Outcome, ContactMessageNotificationStatus? NotificationStatus);

/// <summary>
/// Stores contact messages and pilot applications in CentralDb and then notifies the Wasla team by email. Storing comes
/// first and its failure is thrown to the caller; the email is best effort and its outcome is recorded on the stored
/// row, from where Central Admin can send it again.
/// </summary>
public interface IContactMessageService
{
    Task<ContactMessageSubmitResult> SubmitAsync(ContactMessageRequest request, CancellationToken ct);

    /// <summary>
    /// Stores a pilot application, then notifies the team. The city must be an active Turkish province of the CentralDb
    /// catalog; otherwise <see cref="ArgumentException"/> is thrown and nothing is stored. Starts no signup, payment or
    /// provisioning.
    /// </summary>
    Task<PilotApplicationSubmitResult> SubmitPilotApplicationAsync(PilotApplicationRequest request, CancellationToken ct);

    /// <summary>
    /// Sends the team notification of a stored message again when the earlier one failed, was not configured, or never
    /// finished. Only one attempt runs at a time for a message, so two administrators cannot send it twice.
    /// </summary>
    Task<ContactNotificationResendResult> ResendNotificationAsync(Guid messageId, CancellationToken ct);
}
