using Microsoft.Data.SqlClient;
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
/// configured. Once stored, the notification is not cut short by the request being cancelled, but one attempt ends
/// after <see cref="ContactNotificationRetry.SendTimeout"/>. Logs name the row id, the topic and the failure category
/// only, never what the sender typed.
/// </summary>
/// <remarks>
/// Every attempt owns the row through <see cref="ContactMessage.NotificationClaimId"/>: the stored row carries the first
/// attempt's claim, and a resend claims a new one in a conditional update. An attempt writes its outcome with a
/// conditional update that matches its own claim only, so a late outcome of an earlier attempt never overwrites a newer
/// attempt's state. The SMTP server's acceptance and that write are separate steps; an attempt that cannot tell whether
/// the server took the email is recorded as failed, and a resend may then deliver a second copy (at least once).
/// </remarks>
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

        var status = await NotifyAndRecordAsync(message, message.NotificationClaimId!.Value).ConfigureAwait(false);
        return new ContactMessageSubmitResult(message.Id, status);
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
        catch (DbUpdateException ex)
        {
            // Two posts of the same form at once: the other one stored it first. Any other failure is rethrown.
            _db.Entry(application).State = EntityState.Detached;
            var stored = await StoredPilotStatusAsync(request.SubmissionId, ct).ConfigureAwait(false);
            if (stored is null)
            {
                LogSaveFailure(application.Id, ex);
                throw;
            }

            _logger.LogInformation("Pilot application {ContactMessageId} was stored by a concurrent submission; this one stored and sent nothing.", request.SubmissionId);
            return new PilotApplicationSubmitResult(request.SubmissionId, stored.Value, AlreadyReceived: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSaveFailure(application.Id, ex);
            throw;
        }

        _logger.LogInformation("Pilot application {ContactMessageId} stored. Topic={Topic}", application.Id, application.Topic);

        var status = await NotifyAndRecordAsync(application, application.NotificationClaimId!.Value).ConfigureAwait(false);
        return new PilotApplicationSubmitResult(application.Id, status, AlreadyReceived: false);
    }

    public async Task<ContactNotificationResendResult> ResendNotificationAsync(Guid messageId, CancellationToken ct)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var claimId = Guid.NewGuid();

        // Claim the attempt in one conditional update, so two administrators clicking at once start one attempt. The
        // condition is the canonical rule of ContactNotificationRetry; the new claim id makes any earlier attempt that
        // is still finishing unable to record its outcome over this one.
        var claimed = await _db.ContactMessages
            .Where(m => m.Id == messageId)
            .Where(ContactNotificationRetry.NeedsAttentionAt(nowUtc))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.NotificationStatus, ContactMessageNotificationStatus.Pending)
                    .SetProperty(m => m.NotificationAttemptedAtUtc, nowUtc)
                    .SetProperty(m => m.NotificationClaimId, claimId),
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

        var message = await _db.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct).ConfigureAwait(false);
        _logger.LogInformation("Contact message {ContactMessageId}: Central Admin asked to send its notification again. Topic={Topic}", message.Id, message.Topic);

        var status = await NotifyAndRecordAsync(message, claimId).ConfigureAwait(false);
        return new ContactNotificationResendResult(ContactNotificationResendOutcome.Attempted, status);
    }

    public Task<int> CountStuckNotificationsAsync(CancellationToken ct) =>
        _db.ContactMessages.AsNoTracking().CountAsync(ContactNotificationRetry.StuckAt(_time.GetUtcNow().UtcDateTime), ct);

    private async Task<ContactMessageNotificationStatus?> StoredPilotStatusAsync(Guid id, CancellationToken ct) =>
        await _db.ContactMessages.AsNoTracking()
            .Where(m => m.Id == id && m.Topic == ContactTopic.PilotApplication)
            .Select(m => (ContactMessageNotificationStatus?)m.NotificationStatus)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Sends the notification of the attempt that owns <paramref name="claimId"/> and records its outcome on the row, if
    /// that attempt still owns it. Returns this attempt's outcome.
    /// </summary>
    private async Task<ContactMessageNotificationStatus> NotifyAndRecordAsync(ContactMessage message, Guid claimId)
    {
        var outcome = await NotifyAsync(message).ConfigureAwait(false);
        var recordedAtUtc = _time.GetUtcNow().UtcDateTime;

        try
        {
            var recorded = await _db.ContactMessages
                .Where(m => m.Id == message.Id
                    && m.NotificationClaimId == claimId
                    && m.NotificationStatus == ContactMessageNotificationStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.NotificationStatus, outcome)
                        .SetProperty(m => m.NotificationAttemptedAtUtc, recordedAtUtc)
                        .SetProperty(m => m.NotificationClaimId, (Guid?)null),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (recorded == 0)
            {
                _logger.LogWarning(
                    "Contact message {ContactMessageId}: the outcome {NotificationStatus} of a notification attempt was not recorded, because a newer attempt owns the notification. Topic={Topic}",
                    message.Id,
                    outcome,
                    message.Topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Contact message {ContactMessageId} is stored, but its notification status {NotificationStatus} could not be recorded. Topic={Topic} ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                message.Id,
                outcome,
                message.Topic,
                ex.GetType().Name,
                SqlErrorNumber(ex));
        }

        return outcome;
    }

    private async Task<ContactMessageNotificationStatus> NotifyAsync(ContactMessage message)
    {
        if (!string.Equals(_emailOptions.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Contact message {ContactMessageId}: no notification email sent, because email delivery is not configured (Email:Provider is {EmailProvider}). Topic={Topic}",
                message.Id,
                _emailOptions.Provider,
                message.Topic);
            return ContactMessageNotificationStatus.NotConfigured;
        }

        // One attempt ends at the deadline even if the sender does not stop: the token reaches the SMTP client, and the
        // wait gives up on a send that ignores it.
        using var deadline = new CancellationTokenSource(ContactNotificationRetry.SendTimeout, _time);
        try
        {
            await _emailSender.SendAsync(_emailFactory.BuildNotification(message), deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            return ContactMessageNotificationStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Contact message {ContactMessageId} is stored, but its notification email failed. Topic={Topic} FailureCategory={FailureCategory} ExceptionType={ExceptionType}",
                message.Id,
                message.Topic,
                SmtpFailureClassifier.Classify(ex),
                ex.GetType().Name);
            return ContactMessageNotificationStatus.Failed;
        }
    }

    /// <summary>Why the first save failed, without anything the visitor typed.</summary>
    private void LogSaveFailure(Guid id, Exception ex) =>
        _logger.LogError(
            "Pilot application {ContactMessageId} could not be stored. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType} SqlErrorNumber={SqlErrorNumber}",
            id,
            ex.GetType().Name,
            ex.InnerException?.GetType().Name,
            SqlErrorNumber(ex));

    private static int? SqlErrorNumber(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is SqlException sql)
                return sql.Number;
        return null;
    }
}
