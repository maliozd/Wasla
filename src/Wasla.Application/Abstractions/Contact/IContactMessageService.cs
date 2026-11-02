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
/// Stores a contact message in CentralDb and then notifies the Wasla team by email. Storing comes first and its
/// failure is thrown to the caller; the email is best effort and its outcome is recorded on the stored message.
/// </summary>
public interface IContactMessageService
{
    Task<ContactMessageSubmitResult> SubmitAsync(ContactMessageRequest request, CancellationToken ct);
}
