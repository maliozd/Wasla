using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Abstractions.Sms;
using Wasla.Application.Onboarding;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Sms;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Mandatory SMS verification of a registration draft owner's mobile phone (WAS-192).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Codes are 6 random digits, stored only as a Data Protection payload bound to their verification row, compared in
/// constant time and never logged or returned.</item>
/// <item>A send is counted and recorded in a short transaction (on SQL Server under
/// <see cref="RegistrationPhoneVerificationSendLock"/>), and handed to the sender after the commit.</item>
/// <item>A check first reserves one attempt with a conditional increment, then compares, then consumes the code with an
/// update conditional on the exact stored code and writes the proof conditional on the draft's current number, so
/// concurrent checks can neither exceed the attempt limit nor use a code twice.</item>
/// </list>
/// </remarks>
public sealed class RegistrationPhoneVerificationService : IRegistrationPhoneVerificationService
{
    private const string ProtectorPurpose = "Wasla.Signup.PhoneVerificationCode.v1";
    private static readonly TimeSpan SendLockTimeout = TimeSpan.FromSeconds(5);
    private static long _lastCapacityWarningTicks;

    private readonly CentralDbContext _central;
    private readonly ISmsSender _sms;
    private readonly SmsMessageTemplates _templates;
    private readonly IDataProtector _protector;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationPhoneVerificationService> _logger;

    public RegistrationPhoneVerificationService(
        CentralDbContext central,
        ISmsSender sms,
        SmsMessageTemplates templates,
        IDataProtectionProvider dataProtection,
        IOptions<SignupOptions> options,
        TimeProvider time,
        ILogger<RegistrationPhoneVerificationService> logger)
    {
        _central = central;
        _sms = sms;
        _templates = templates;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public bool IsAvailable => _sms.IsAvailable;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private PhoneVerificationOptions Rules => _options.PhoneVerification;

    public async Task<PhoneVerificationResult> GetStatusAsync(Guid draftId, CancellationToken ct)
    {
        var now = Now;
        var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
        return draft is null
            ? PhoneVerificationResult.Failed(SignupErrorCodes.DraftMissing)
            : PhoneVerificationResult.Ok(await StatusAsync(draft, now, ct));
    }

    public async Task<PhoneVerificationResult> SendCodeAsync(Guid draftId, CultureInfo culture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(culture);
        _central.ChangeTracker.Clear();
        var now = Now;
        var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
        if (draft is null)
            return PhoneVerificationResult.Failed(SignupErrorCodes.DraftMissing);
        if (draft.SubmittedAtUtc is not null)
            return PhoneVerificationResult.Failed(SignupErrorCodes.DraftSubmitted);
        if (!RegistrationDraftRecords.OwnerSaved(draft))
            return await FailedAsync(SignupErrorCodes.PhoneOwnerIncomplete, draft, now, null, ct);
        if (RegistrationDraftRecords.ProofIsCurrent(draft, now, _options.Draft))
            return await FailedAsync(SignupErrorCodes.PhoneAlreadyVerified, draft, now, null, ct);
        if (!_sms.IsAvailable)
            return await FailedAsync(SignupErrorCodes.PhoneUnavailable, draft, now, null, ct);

        var number = draft.OwnerMobilePhone!;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var text = _templates.RegistrationVerificationText(culture, code, (int)Math.Ceiling(Rules.CodeLifetime.TotalMinutes));
        if (text is null)
        {
            _logger.LogError("Phone verification send refused: no SMS template for the default culture. DraftId={DraftId}", draftId);
            return await FailedAsync(SignupErrorCodes.PhoneUnavailable, draft, now, null, ct);
        }

        var reserved = await ReserveSendAsync(draft, number, code, now, ct);
        if (reserved.Error is not null)
            return await FailedAsync(reserved.Error, draft, now, reserved.RetryAfter, ct);

        SmsDeliveryResult delivery;
        try
        {
            delivery = await _sms.SendAsync(
                new SmsMessage(number, text, SmsPurpose.RegistrationPhoneVerification, draftId.ToString("N")),
                ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The sender gave up (for example an HttpClient timeout) while the request itself goes on: a timeout, whose
            // code must not stay usable. Adapters should report this as a result; this guards one that throws instead.
            _logger.LogWarning("Phone verification SMS timed out. DraftId={DraftId} ExceptionType={ExceptionType}", draftId, ex.GetType().Name);
            delivery = SmsDeliveryResult.Failed(SmsDeliveryFailure.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Phone verification SMS failed. DraftId={DraftId} ExceptionType={ExceptionType}", draftId, ex.GetType().Name);
            delivery = SmsDeliveryResult.Failed(SmsDeliveryFailure.Unavailable);
        }

        if (delivery.Accepted)
        {
            await SetSendOutcomeAsync(reserved.SendId, RegistrationPhoneVerificationSendOutcome.Accepted, ct);
            _logger.LogInformation("Phone verification code sent. DraftId={DraftId}", draftId);
            return PhoneVerificationResult.Ok(await StatusAsync(draftId, now, ct));
        }

        // The code that was not delivered must not verify; the send stays counted and the resend wait applies.
        var protectedCode = reserved.ProtectedCode;
        await _central.RegistrationPhoneVerifications
            .Where(r => r.Id == reserved.VerificationId && r.ProtectedCode == protectedCode)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProtectedCode, (string?)null)
                .SetProperty(r => r.CodeExpiresAtUtc, (DateTime?)null), CancellationToken.None);
        await SetSendOutcomeAsync(reserved.SendId, RegistrationPhoneVerificationSendOutcome.Failed, CancellationToken.None);
        _logger.LogWarning(
            "Phone verification SMS was not accepted. DraftId={DraftId} Failure={Failure}",
            draftId,
            delivery.Failure);
        return await FailedAsync(SignupErrorCodes.PhoneDeliveryFailed, draft, now, null, ct);
    }

    public async Task<PhoneVerificationResult> VerifyCodeAsync(Guid draftId, string? code, CancellationToken ct)
    {
        _central.ChangeTracker.Clear();
        var now = Now;
        var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
        if (draft is null)
            return PhoneVerificationResult.Failed(SignupErrorCodes.DraftMissing);
        if (draft.SubmittedAtUtc is not null)
            return PhoneVerificationResult.Failed(SignupErrorCodes.DraftSubmitted);
        if (!RegistrationDraftRecords.OwnerSaved(draft))
            return await FailedAsync(SignupErrorCodes.PhoneOwnerIncomplete, draft, now, null, ct);
        if (RegistrationDraftRecords.ProofIsCurrent(draft, now, _options.Draft))
            return await FailedAsync(SignupErrorCodes.PhoneAlreadyVerified, draft, now, null, ct);

        var entered = code?.Trim() ?? string.Empty;
        if (entered.Length != PhoneVerificationPolicy.CodeLength || !entered.All(char.IsAsciiDigit))
            return await FailedAsync(SignupErrorCodes.PhoneCodeFormatInvalid, draft, now, null, ct);

        var number = draft.OwnerMobilePhone!;
        var row = await RegistrationDraftRecords.LoadVerificationAsync(_central, draftId, number, tracking: false, ct);
        if (row?.LockedUntilUtc is { } lockedUntil && lockedUntil > now)
            return await FailedAsync(SignupErrorCodes.PhoneLocked, draft, now, lockedUntil - now, ct);
        if (row?.ProtectedCode is null || row.CodeExpiresAtUtc is null)
            return await FailedAsync(SignupErrorCodes.PhoneNoActiveCode, draft, now, null, ct);
        if (row.CodeExpiresAtUtc <= now)
            return await FailedAsync(SignupErrorCodes.PhoneCodeExpired, draft, now, null, ct);

        // Reserve one attempt before comparing: concurrent checks can never use more than the limit. The reservation that
        // reaches the limit sets the lock in the same statement, so no later failure can leave the limit used up without a
        // lock; a right code checked with that last attempt still verifies and lifts the lock.
        var max = Rules.MaxFailedAttempts;
        var rowId = row.Id;
        var lockedUntilIfLast = now + Rules.LockoutDuration;
        var reservedAttempt = await _central.RegistrationPhoneVerifications
            .Where(r => r.Id == rowId
                        && r.ProtectedCode != null
                        && r.CodeExpiresAtUtc > now
                        && r.FailedAttempts < max
                        && (r.LockedUntilUtc == null || r.LockedUntilUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.FailedAttempts, r => r.FailedAttempts + 1)
                .SetProperty(r => r.LockedUntilUtc, r => r.FailedAttempts + 1 >= max ? lockedUntilIfLast : r.LockedUntilUtc), ct);
        if (reservedAttempt != 1)
            return await RefusedCheckAsync(draft, rowId, now, ct);

        var current = await _central.RegistrationPhoneVerifications.AsNoTracking().FirstAsync(r => r.Id == rowId, ct);
        if (current.ProtectedCode is not null && Matches(current.ProtectedCode, rowId, entered))
        {
            var consumed = await ConsumeAsync(draftId, number, rowId, current.ProtectedCode, now, ct);
            if (consumed)
            {
                _logger.LogInformation("Owner phone verified. DraftId={DraftId}", draftId);
                return PhoneVerificationResult.Ok(await StatusAsync(draftId, now, ct));
            }

            return await RefusedCheckAsync(draft, rowId, now, ct);
        }

        if (current.FailedAttempts >= max)
        {
            // The last allowed wrong code: the reservation locked the number; its code is dropped too.
            await _central.RegistrationPhoneVerifications
                .Where(r => r.Id == rowId && r.FailedAttempts >= max)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ProtectedCode, (string?)null)
                    .SetProperty(r => r.CodeExpiresAtUtc, (DateTime?)null), ct);
            _logger.LogInformation("Owner phone verification locked after wrong codes. DraftId={DraftId}", draftId);
            var lockedFor = current.LockedUntilUtc is { } until && until > now ? until - now : Rules.LockoutDuration;
            return await FailedAsync(SignupErrorCodes.PhoneLocked, draft, now, lockedFor, ct);
        }

        return await FailedAsync(SignupErrorCodes.PhoneCodeIncorrect, draft, now, null, ct);
    }

    public async Task<PhoneVerificationResult> ChangeNumberAsync(Guid draftId, string? mobilePhone, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _central.ChangeTracker.Clear();
            var now = Now;
            var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: true, ct);
            if (draft is null)
                return PhoneVerificationResult.Failed(SignupErrorCodes.DraftMissing);
            if (draft.SubmittedAtUtc is not null)
                return PhoneVerificationResult.Failed(SignupErrorCodes.DraftSubmitted);
            if (!RegistrationDraftRecords.OwnerSaved(draft))
                return await FailedAsync(SignupErrorCodes.PhoneOwnerIncomplete, draft, now, null, ct);

            if (string.IsNullOrWhiteSpace(mobilePhone) || !TurkishMobilePhone.TryNormalize(mobilePhone, out var number))
            {
                var fieldCode = string.IsNullOrWhiteSpace(mobilePhone) ? "Validation.MobilePhoneRequired" : "Validation.MobilePhoneInvalid";
                return new PhoneVerificationResult(
                    await StatusAsync(draft, now, ct),
                    SignupErrorCodes.DraftValidationFailed,
                    null,
                    [new RegistrationDraftFieldError(RegistrationDraftSection.Owner, "mobilePhone", fieldCode)]);
            }

            if (string.Equals(number, draft.OwnerMobilePhone, StringComparison.Ordinal))
                return PhoneVerificationResult.Ok(await StatusAsync(draft, now, ct));

            draft.OwnerMobilePhone = number;
            draft.PhoneVerifiedNumber = null;
            draft.PhoneVerifiedAtUtc = null;
            draft.UpdatedAtUtc = now;
            draft.ExpiresAtUtc = RegistrationDraftRecords.ExpiryAfterWrite(draft, now, _options.Draft);
            draft.Revision++;
            try
            {
                await using var transaction = await _central.Database.BeginTransactionAsync(ct);
                await _central.SaveChangesAsync(ct);
                await RegistrationDraftRecords.ClearOutstandingCodesAsync(_central, draftId, ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }

            _central.ChangeTracker.Clear();
            _logger.LogInformation("Owner phone changed; verification required again. DraftId={DraftId}", draftId);
            return PhoneVerificationResult.Ok(await StatusAsync(draftId, now, ct));
        }

        return PhoneVerificationResult.Failed(SignupErrorCodes.RegistrationFailed);
    }

    private sealed record SendReservation(string? Error, TimeSpan? RetryAfter, Guid VerificationId, Guid SendId, string ProtectedCode)
    {
        public static SendReservation Refused(string error, TimeSpan? retryAfter = null) =>
            new(error, retryAfter, Guid.Empty, Guid.Empty, string.Empty);
    }

    /// <summary>Checks every send limit and records the send and its new code, in one short transaction.</summary>
    private async Task<SendReservation> ReserveSendAsync(RegistrationDraft draft, string number, string code, DateTime now, CancellationToken ct)
    {
        _central.ChangeTracker.Clear();
        try
        {
            await using var transaction = await _central.Database.BeginTransactionAsync(ct);
            if (!await RegistrationPhoneVerificationSendLock.TryAcquireAsync(_central, SendLockTimeout, ct))
                return SendReservation.Refused(SignupErrorCodes.PhoneCapacityReached, SendLockTimeout);

            var row = await RegistrationDraftRecords.LoadVerificationAsync(_central, draft.Id, number, tracking: true, ct);
            var isNew = row is null;
            row ??= new RegistrationPhoneVerification
            {
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                PhoneNumber = number,
                CreatedAtUtc = now
            };

            if (row.LockedUntilUtc is { } lockedUntil && lockedUntil > now)
                return SendReservation.Refused(SignupErrorCodes.PhoneLocked, lockedUntil - now);
            if (row.LockedUntilUtc is not null || row.FailedAttempts >= Rules.MaxFailedAttempts)
            {
                // The lock has passed (or, for a row written before the lock was set with the last attempt, the attempts
                // are used up without one): the number starts again with a full attempt budget.
                row.LockedUntilUtc = null;
                row.FailedAttempts = 0;
            }

            if (row.LastSentAtUtc is { } lastSent && lastSent + Rules.ResendWait > now)
                return SendReservation.Refused(SignupErrorCodes.PhoneResendNotAvailable, lastSent + Rules.ResendWait - now);

            var draftId = draft.Id;
            var draftSends = await _central.RegistrationPhoneVerifications
                .Where(r => r.DraftId == draftId)
                .SumAsync(r => r.SendCount, ct);
            if (draftSends >= Rules.MaxSendsPerDraft)
                return SendReservation.Refused(SignupErrorCodes.PhoneSendLimitReached);

            var numberLimit = await WindowLimitAsync(number, Rules.MaxSendsPerNumberPerHour, TimeSpan.FromHours(1), now, ct)
                              ?? await WindowLimitAsync(number, Rules.MaxSendsPerNumberPerDay, TimeSpan.FromDays(1), now, ct);
            if (numberLimit is { } numberRetry)
                return SendReservation.Refused(SignupErrorCodes.PhoneSendLimitReached, numberRetry);

            var globalLimit = await WindowLimitAsync(null, Rules.MaxSendsPerHour, TimeSpan.FromHours(1), now, ct)
                              ?? await WindowLimitAsync(null, Rules.MaxSendsPerDay, TimeSpan.FromDays(1), now, ct);
            if (globalLimit is { } globalRetry)
            {
                WarnCapacityOnce(now);
                return SendReservation.Refused(SignupErrorCodes.PhoneCapacityReached, globalRetry);
            }

            var protectedCode = Protect(row.Id, code);
            row.ProtectedCode = protectedCode;
            row.CodeIssuedAtUtc = now;
            row.CodeExpiresAtUtc = now + Rules.CodeLifetime;
            row.LastSentAtUtc = now;
            row.SendCount++;
            if (isNew)
                _central.RegistrationPhoneVerifications.Add(row);

            var send = new RegistrationPhoneVerificationSend
            {
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                PhoneNumber = number,
                SentAtUtc = now,
                Outcome = RegistrationPhoneVerificationSendOutcome.Pending
            };
            _central.RegistrationPhoneVerificationSends.Add(send);

            await _central.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SendReservation(null, null, row.Id, send.Id, protectedCode);
        }
        catch (DbUpdateException ex)
        {
            // Another send for this draft and number was stored first (its row was inserted or its last send changed).
            // Any other database failure is not a resend refusal.
            _central.ChangeTracker.Clear();
            if (ex is DbUpdateConcurrencyException || await AnotherSendStoredAsync(draft.Id, number, now, ct))
                return SendReservation.Refused(SignupErrorCodes.PhoneResendNotAvailable, Rules.ResendWait);
            throw;
        }
        finally
        {
            _central.ChangeTracker.Clear();
        }
    }

    private Task<bool> AnotherSendStoredAsync(Guid draftId, string number, DateTime now, CancellationToken ct)
    {
        var since = now - Rules.ResendWait;
        return _central.RegistrationPhoneVerifications.AsNoTracking()
            .AnyAsync(r => r.DraftId == draftId && r.PhoneNumber == number && r.LastSentAtUtc != null && r.LastSentAtUtc >= since, ct);
    }

    /// <summary>The wait until the window has room again, or null when it has room now.</summary>
    private async Task<TimeSpan?> WindowLimitAsync(string? number, int limit, TimeSpan window, DateTime now, CancellationToken ct)
    {
        var from = now - window;
        var sends = _central.RegistrationPhoneVerificationSends.AsNoTracking().Where(s => s.SentAtUtc > from);
        if (number is not null)
            sends = sends.Where(s => s.PhoneNumber == number);

        var count = await sends.CountAsync(ct);
        if (count < limit)
            return null;

        // The window frees a place when its oldest send leaves it.
        var oldest = await sends.OrderBy(s => s.SentAtUtc).Select(s => s.SentAtUtc).Skip(count - limit).FirstAsync(ct);
        var wait = oldest + window - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    private void WarnCapacityOnce(DateTime now)
    {
        var last = Interlocked.Read(ref _lastCapacityWarningTicks);
        if (now.Ticks - last < TimeSpan.TicksPerHour)
            return;
        if (Interlocked.CompareExchange(ref _lastCapacityWarningTicks, now.Ticks, last) == last)
            _logger.LogWarning("Phone verification SMS volume limit reached; sends are refused until the window has room.");
    }

    /// <summary>
    /// Stores the proof for the draft's current number and consumes the code (only if it is still the stored one), in one
    /// transaction. Like every write that touches both, it locks the draft first and then its verification row (a number
    /// change and the submit do the same), so two of them can never deadlock.
    /// </summary>
    private async Task<bool> ConsumeAsync(Guid draftId, string number, Guid rowId, string protectedCode, DateTime now, CancellationToken ct)
    {
        await using var transaction = await _central.Database.BeginTransactionAsync(ct);
        var proven = await _central.RegistrationDrafts
            .Where(d => d.Id == draftId && d.OwnerMobilePhone == number && d.SubmittedAtUtc == null && d.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.PhoneVerifiedNumber, number)
                .SetProperty(d => d.PhoneVerifiedAtUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), ct);
        if (proven != 1)
        {
            // The number changed (or the draft was submitted) while the code was checked: nothing is verified.
            await transaction.RollbackAsync(ct);
            return false;
        }

        var consumed = await _central.RegistrationPhoneVerifications
            .Where(r => r.Id == rowId && r.ProtectedCode == protectedCode)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProtectedCode, (string?)null)
                .SetProperty(r => r.CodeExpiresAtUtc, (DateTime?)null)
                .SetProperty(r => r.VerifiedAtUtc, now)
                .SetProperty(r => r.LockedUntilUtc, (DateTime?)null)
                .SetProperty(r => r.FailedAttempts, r => r.FailedAttempts > 0 ? r.FailedAttempts - 1 : 0), ct);
        if (consumed != 1)
        {
            // Another check used the code, or a resend replaced it: this one verifies nothing.
            await transaction.RollbackAsync(ct);
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Why a check could not reserve an attempt or consume its code, from the stored state.</summary>
    private async Task<PhoneVerificationResult> RefusedCheckAsync(RegistrationDraft draft, Guid rowId, DateTime now, CancellationToken ct)
    {
        var fresh = await RegistrationDraftRecords.LoadLiveAsync(_central, draft.Id, now, tracking: false, ct);
        if (fresh is null)
            return PhoneVerificationResult.Failed(SignupErrorCodes.DraftMissing);
        if (RegistrationDraftRecords.ProofIsCurrent(fresh, now, _options.Draft))
            return await FailedAsync(SignupErrorCodes.PhoneAlreadyVerified, fresh, now, null, ct);

        var row = await _central.RegistrationPhoneVerifications.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rowId, ct);
        if (row?.ProtectedCode is null)
            return await FailedAsync(SignupErrorCodes.PhoneNoActiveCode, fresh, now, null, ct);
        if (row.LockedUntilUtc is { } lockedUntil && lockedUntil > now)
            return await FailedAsync(SignupErrorCodes.PhoneLocked, fresh, now, lockedUntil - now, ct);
        // The attempts are used up and the lock has passed: the code is spent; a new one must be requested.
        if (row.FailedAttempts >= Rules.MaxFailedAttempts)
            return await FailedAsync(SignupErrorCodes.PhoneNoActiveCode, fresh, now, null, ct);
        if (row.CodeExpiresAtUtc <= now)
            return await FailedAsync(SignupErrorCodes.PhoneCodeExpired, fresh, now, null, ct);
        return await FailedAsync(SignupErrorCodes.PhoneCodeIncorrect, fresh, now, null, ct);
    }

    private string Protect(Guid rowId, string code) => _protector.Protect($"{rowId:N}:{code}");

    private bool Matches(string protectedCode, Guid rowId, string entered)
    {
        string payload;
        try
        {
            payload = _protector.Unprotect(protectedCode);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes($"{rowId:N}:{entered}");
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(payload), expected);
    }

    private Task SetSendOutcomeAsync(Guid sendId, RegistrationPhoneVerificationSendOutcome outcome, CancellationToken ct) =>
        _central.RegistrationPhoneVerificationSends
            .Where(s => s.Id == sendId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Outcome, outcome), ct);

    private async Task<PhoneVerificationResult> FailedAsync(string error, RegistrationDraft draft, DateTime now, TimeSpan? retryAfter, CancellationToken ct) =>
        PhoneVerificationResult.Failed(error, await StatusAsync(draft.Id, now, ct), retryAfter);

    private async Task<PhoneVerificationStatus> StatusAsync(Guid draftId, DateTime now, CancellationToken ct)
    {
        var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
        return draft is null
            ? new PhoneVerificationStatus(PhoneVerificationState.NotSent, null, PhoneVerificationPolicy.CodeLength, null, null, null, null)
            : await StatusAsync(draft, now, ct);
    }

    private async Task<PhoneVerificationStatus> StatusAsync(RegistrationDraft draft, DateTime now, CancellationToken ct)
    {
        var row = draft.OwnerMobilePhone is null
            ? null
            : await RegistrationDraftRecords.LoadVerificationAsync(_central, draft.Id, draft.OwnerMobilePhone, tracking: false, ct);
        return RegistrationDraftRecords.Status(draft, row, _options, _sms.IsAvailable, now);
    }
}
