using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.Email;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Stores a contact message or a pilot application, then emails the Wasla team. The first save is the only step whose
/// failure reaches the caller. The email is sent after that save, outside any transaction, and only when SMTP is
/// configured; its outcome is recorded on the row. Once stored, the notification is not cut short by the request being
/// cancelled. Logs name the row id, the topic and the failure category only, never what the sender typed.
/// </summary>
public sealed class ContactMessageService : IContactMessageService
{
    private readonly CentralDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ContactMessageEmailFactory _emailFactory;
    private readonly ITurkishProvinceCatalog _provinces;
    private readonly EmailOptions _emailOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<ContactMessageService> _logger;

    public ContactMessageService(
        CentralDbContext db,
        IEmailSender emailSender,
        ContactMessageEmailFactory emailFactory,
        ITurkishProvinceCatalog provinces,
        IOptions<EmailOptions> emailOptions,
        TimeProvider time,
        ILogger<ContactMessageService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _emailFactory = emailFactory;
        _provinces = provinces;
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

        await NotifyAndRecordAsync(message).ConfigureAwait(false);
        return new ContactMessageSubmitResult(message.Id, message.NotificationStatus);
    }

    public async Task<PilotApplicationSubmitResult> SubmitPilotApplicationAsync(PilotApplicationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The form's submission id is the stored id: a double click or a resubmitted page meets the stored row.
        var existing = await StoredPilotStatusAsync(request.SubmissionId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation("Pilot application {ContactMessageId} was already stored; the repeated submission stored and sent nothing.", request.SubmissionId);
            return new PilotApplicationSubmitResult(request.SubmissionId, existing.Value, AlreadyReceived: true);
        }

        var plate = request.CityPlateCode?.Trim() ?? string.Empty;
        var provinces = await _provinces.GetActiveProvincesAsync(ct).ConfigureAwait(false);
        if (!provinces.Any(p => p.PlateCode == plate))
            throw new ArgumentException("The city is not an active Turkish province of the city catalog.", nameof(request));

        var application = ContactMessage.CreatePilotApplication(
            request.SubmissionId,
            request.RestaurantName,
            request.ContactName,
            request.Email,
            request.Phone,
            plate,
            request.Note,
            request.Culture,
            _time.GetUtcNow().UtcDateTime);

        _db.ContactMessages.Add(application);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two posts of the same form at once: the other one stored it first. Any other failure is rethrown.
            _db.Entry(application).State = EntityState.Detached;
            var stored = await StoredPilotStatusAsync(request.SubmissionId, ct).ConfigureAwait(false);
            if (stored is null)
                throw;

            _logger.LogInformation("Pilot application {ContactMessageId} was stored by a concurrent submission; this one stored and sent nothing.", request.SubmissionId);
            return new PilotApplicationSubmitResult(request.SubmissionId, stored.Value, AlreadyReceived: true);
        }

        _logger.LogInformation("Pilot application {ContactMessageId} stored. Topic={Topic}", application.Id, application.Topic);

        await NotifyAndRecordAsync(application).ConfigureAwait(false);
        return new PilotApplicationSubmitResult(application.Id, application.NotificationStatus, AlreadyReceived: false);
    }

    public async Task<ContactNotificationResendResult> ResendNotificationAsync(Guid messageId, CancellationToken ct)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var stuckBefore = nowUtc - ContactNotificationRetry.StuckAfter;

        // Claim the attempt in one conditional update, so two administrators clicking at once send one email.
        var claimed = await _db.ContactMessages
            .Where(m => m.Id == messageId
                && (m.NotificationStatus == ContactMessageNotificationStatus.Failed
                    || m.NotificationStatus == ContactMessageNotificationStatus.NotConfigured
                    || (m.NotificationStatus == ContactMessageNotificationStatus.Pending
                        && (m.NotificationAttemptedAtUtc ?? m.CreatedAtUtc) <= stuckBefore)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.NotificationStatus, ContactMessageNotificationStatus.Pending)
                    .SetProperty(m => m.NotificationAttemptedAtUtc, nowUtc),
                ct)
            .ConfigureAwait(false);

        if (claimed == 0)
        {
            var current = await _db.ContactMessages.AsNoTracking()
                .Where(m => m.Id == messageId)
                .Select(m => (ContactMessageNotificationStatus?)m.NotificationStatus)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return current switch
            {
                null => new ContactNotificationResendResult(ContactNotificationResendOutcome.NotFound, null),
                ContactMessageNotificationStatus.Sent => new ContactNotificationResendResult(ContactNotificationResendOutcome.AlreadySent, current),
                _ => new ContactNotificationResendResult(ContactNotificationResendOutcome.InProgress, current)
            };
        }

        var message = await _db.ContactMessages.SingleAsync(m => m.Id == messageId, ct).ConfigureAwait(false);
        _logger.LogInformation("Contact message {ContactMessageId}: Central Admin asked to send its notification again. Topic={Topic}", message.Id, message.Topic);

        await NotifyAndRecordAsync(message).ConfigureAwait(false);
        return new ContactNotificationResendResult(ContactNotificationResendOutcome.Attempted, message.NotificationStatus);
    }

    private async Task<ContactMessageNotificationStatus?> StoredPilotStatusAsync(Guid id, CancellationToken ct) =>
        await _db.ContactMessages.AsNoTracking()
            .Where(m => m.Id == id && m.Topic == ContactTopic.PilotApplication)
            .Select(m => (ContactMessageNotificationStatus?)m.NotificationStatus)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private async Task NotifyAndRecordAsync(ContactMessage message)
    {
        await NotifyAsync(message).ConfigureAwait(false);

        try
        {
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Contact message {ContactMessageId} is stored, but its notification status {NotificationStatus} could not be recorded. Topic={Topic} ExceptionType={ExceptionType}",
                message.Id,
                message.NotificationStatus,
                message.Topic,
                ex.GetType().Name);
        }
    }

    private async Task NotifyAsync(ContactMessage message)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (!string.Equals(_emailOptions.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            message.MarkNotificationNotConfigured(nowUtc);
            _logger.LogInformation(
                "Contact message {ContactMessageId}: no notification email sent, because email delivery is not configured (Email:Provider is {EmailProvider}). Topic={Topic}",
                message.Id,
                _emailOptions.Provider,
                message.Topic);
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
                "Contact message {ContactMessageId} is stored, but its notification email failed. Topic={Topic} FailureCategory={FailureCategory} ExceptionType={ExceptionType}",
                message.Id,
                message.Topic,
                SmtpFailureClassifier.Classify(ex),
                ex.GetType().Name);
        }
    }
}
