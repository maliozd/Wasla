using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Stores a contact message, then emails the Wasla team. The first save is the only step whose failure reaches the
/// caller. The email is sent after that save, outside any transaction, and only when SMTP is configured; its outcome
/// is recorded on the message. Once stored, the notification is not cut short by the request being cancelled. Logs name
/// the message id and failure category only, never what the sender typed.
/// </summary>
public sealed class ContactMessageService : IContactMessageService
{
    private readonly CentralDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ContactMessageEmailFactory _emailFactory;
    private readonly EmailOptions _emailOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<ContactMessageService> _logger;

    public ContactMessageService(
        CentralDbContext db,
        IEmailSender emailSender,
        ContactMessageEmailFactory emailFactory,
        IOptions<EmailOptions> emailOptions,
        TimeProvider time,
        ILogger<ContactMessageService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _emailFactory = emailFactory;
        _emailOptions = emailOptions.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<ContactMessageSubmitResult> SubmitAsync(ContactMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = ContactMessage.Create(
            request.FullName,
            request.RestaurantName,
            request.Email,
            request.Phone,
            request.Topic,
            request.Message,
            request.Culture,
            request.PrivacyNoticeAcknowledged,
            _time.GetUtcNow().UtcDateTime);

        _db.ContactMessages.Add(message);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Contact message {ContactMessageId} stored. Topic={Topic}", message.Id, message.Topic);

        await NotifyAsync(message).ConfigureAwait(false);

        try
        {
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Contact message {ContactMessageId} is stored, but its notification status {NotificationStatus} could not be recorded. ExceptionType={ExceptionType}",
                message.Id,
                message.NotificationStatus,
                ex.GetType().Name);
        }

        return new ContactMessageSubmitResult(message.Id, message.NotificationStatus);
    }

    private async Task NotifyAsync(ContactMessage message)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (!string.Equals(_emailOptions.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            message.MarkNotificationNotConfigured(nowUtc);
            _logger.LogInformation(
                "Contact message {ContactMessageId}: no notification email sent, because email delivery is not configured (Email:Provider is {EmailProvider}).",
                message.Id,
                _emailOptions.Provider);
            return;
        }

        try
        {
            await _emailSender.SendAsync(_emailFactory.BuildNotification(message), CancellationToken.None).ConfigureAwait(false);
            message.MarkNotificationSent(_time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            message.MarkNotificationFailed(_time.GetUtcNow().UtcDateTime);
            _logger.LogWarning(
                "Contact message {ContactMessageId} is stored, but its notification email failed. FailureCategory={FailureCategory} ExceptionType={ExceptionType}",
                message.Id,
                SmtpFailureClassifier.Classify(ex),
                ex.GetType().Name);
        }
    }
}
