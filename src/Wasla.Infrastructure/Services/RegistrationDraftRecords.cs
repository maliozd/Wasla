using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Onboarding;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>Reads and rules the draft and phone verification services share (WAS-192).</summary>
internal static class RegistrationDraftRecords
{
    /// <summary>The draft, unless it does not exist or has expired. Expired rows are missing, whether cleaned up or not.</summary>
    public static Task<RegistrationDraft?> LoadLiveAsync(CentralDbContext central, Guid draftId, DateTime now, bool tracking, CancellationToken ct)
    {
        var drafts = tracking ? central.RegistrationDrafts : central.RegistrationDrafts.AsNoTracking();
        return drafts.FirstOrDefaultAsync(d => d.Id == draftId && d.ExpiresAtUtc > now, ct);
    }

    public static Task<RegistrationPhoneVerification?> LoadVerificationAsync(
        CentralDbContext central, Guid draftId, string phoneNumber, bool tracking, CancellationToken ct)
    {
        var rows = tracking ? central.RegistrationPhoneVerifications : central.RegistrationPhoneVerifications.AsNoTracking();
        return rows.FirstOrDefaultAsync(r => r.DraftId == draftId && r.PhoneNumber == phoneNumber, ct);
    }

    /// <summary>The draft's expiry after a write at <paramref name="now"/>: the idle lifetime, capped by the maximum lifetime.</summary>
    public static DateTime ExpiryAfterWrite(RegistrationDraft draft, DateTime now, RegistrationDraftLifetimeOptions lifetimes)
    {
        var idle = now + lifetimes.IdleLifetime;
        var max = draft.CreatedAtUtc + lifetimes.MaxLifetime;
        return idle < max ? idle : max;
    }

    public static bool OwnerSaved(RegistrationDraft draft) =>
        draft.OwnerSavedAtUtc is not null && draft.OwnerMobilePhone is not null && draft.PasswordHash is not null;

    /// <summary>The proof counts while it names the owner's current number and is within the proof lifetime.</summary>
    public static bool ProofIsCurrent(RegistrationDraft draft, DateTime now, RegistrationDraftLifetimeOptions lifetimes) =>
        draft.OwnerMobilePhone is not null
        && draft.PhoneVerifiedNumber is not null
        && string.Equals(draft.PhoneVerifiedNumber, draft.OwnerMobilePhone, StringComparison.Ordinal)
        && draft.PhoneVerifiedAtUtc is { } verifiedAt
        && verifiedAt + lifetimes.PhoneProofLifetime > now;

    public static PhoneVerificationStatus Status(
        RegistrationDraft draft,
        RegistrationPhoneVerification? row,
        SignupOptions options,
        bool senderAvailable,
        DateTime now)
    {
        const int length = PhoneVerificationPolicy.CodeLength;
        var phone = draft.OwnerMobilePhone;
        if (phone is null || draft.SubmittedAtUtc is not null)
            return new PhoneVerificationStatus(PhoneVerificationState.NotSent, null, length, null, null, null, null);

        if (ProofIsCurrent(draft, now, options.Draft))
            return new PhoneVerificationStatus(PhoneVerificationState.Verified, phone, length, null, null, null, null);

        if (!senderAvailable)
            return new PhoneVerificationStatus(PhoneVerificationState.Unavailable, phone, length, null, null, null, null);

        var rules = options.PhoneVerification;
        if (row?.LockedUntilUtc is { } lockedUntil && lockedUntil > now)
            return new PhoneVerificationStatus(PhoneVerificationState.Locked, phone, length, null, null, lockedUntil - now, null);

        var resendIn = row?.LastSentAtUtc is { } lastSent && lastSent + rules.ResendWait > now
            ? lastSent + rules.ResendWait - now
            : TimeSpan.Zero;

        if (row?.ProtectedCode is not null && row.CodeExpiresAtUtc is { } expiresAt)
        {
            if (expiresAt > now)
            {
                var remaining = Math.Max(0, rules.MaxFailedAttempts - row.FailedAttempts);
                return new PhoneVerificationStatus(PhoneVerificationState.CodeSent, phone, length, expiresAt - now, resendIn, null, remaining);
            }

            return new PhoneVerificationStatus(PhoneVerificationState.Expired, phone, length, null, resendIn, null, null);
        }

        return new PhoneVerificationStatus(PhoneVerificationState.NotSent, phone, length, null, resendIn, null, null);
    }

    public static RegistrationDraftView View(RegistrationDraft draft, PhoneVerificationStatus phone)
    {
        RegistrationDraftPlan? plan = draft.PlanSavedAtUtc is not null && draft.PlanCode is not null && draft.BillingPeriod is not null
            ? new RegistrationDraftPlan(draft.PlanCode, draft.BillingPeriod)
            : null;

        RegistrationDraftRestaurant? restaurant = draft.RestaurantSavedAtUtc is not null && draft.BusinessName is not null
            ? new RegistrationDraftRestaurant(
                draft.BusinessName,
                SplitCodes(draft.BusinessTypeCodes),
                (draft.BusinessPhoneType ?? BusinessPhoneType.Mobile).ToString(),
                draft.BusinessPhone ?? string.Empty,
                draft.BusinessEmail,
                draft.Slug ?? string.Empty,
                draft.Country ?? string.Empty,
                draft.CityId,
                draft.DistrictId,
                draft.NeighborhoodId,
                draft.StreetId,
                draft.City,
                draft.District,
                draft.Neighborhood,
                draft.StreetAddress,
                draft.BuildingNumber,
                draft.Floor,
                draft.DoorNumber,
                draft.AddressNote,
                draft.PostalCode,
                draft.LocationUrl)
            : null;

        RegistrationDraftOwner? owner = OwnerSaved(draft)
            ? new RegistrationDraftOwner(draft.OwnerFullName ?? string.Empty, draft.OwnerEmail ?? string.Empty, draft.OwnerMobilePhone!, PasswordSet: true)
            : null;

        return new RegistrationDraftView(draft.Id, draft.ExpiresAtUtc, plan, restaurant, owner, phone, draft.PendingRegistrationId);
    }

    /// <summary>The stored draft as the registration request the rules check (the password is already a hash).</summary>
    public static PendingRegistrationRequest Request(RegistrationDraft draft) => new(
        draft.PlanCode ?? string.Empty,
        draft.BillingPeriod ?? string.Empty,
        draft.BusinessName ?? string.Empty,
        SplitCodes(draft.BusinessTypeCodes),
        (draft.BusinessPhoneType ?? BusinessPhoneType.Mobile).ToString(),
        draft.BusinessPhone ?? string.Empty,
        draft.BusinessEmail,
        draft.Slug ?? string.Empty,
        draft.Country ?? string.Empty,
        draft.CityId,
        draft.DistrictId,
        draft.NeighborhoodId,
        draft.StreetId,
        draft.City,
        draft.District,
        draft.Neighborhood,
        draft.StreetAddress,
        draft.BuildingNumber,
        draft.Floor,
        draft.DoorNumber,
        draft.AddressNote,
        draft.PostalCode,
        draft.LocationUrl,
        draft.OwnerFullName ?? string.Empty,
        draft.OwnerEmail ?? string.Empty,
        draft.OwnerMobilePhone,
        string.Empty);

    public static IReadOnlyList<string> SplitCodes(string? joined) =>
        string.IsNullOrWhiteSpace(joined)
            ? Array.Empty<string>()
            : joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>One field error per invalid field, in rule order, for the given sections only.</summary>
    public static List<RegistrationDraftFieldError> FieldErrors(ValidationResult result, params RegistrationDraftSection[] sections)
    {
        var errors = new List<RegistrationDraftFieldError>();
        foreach (var failure in result.Errors)
        {
            if (PendingRegistrationRules.FieldOf(failure.PropertyName) is not { } field || !sections.Contains(field.Section))
                continue;
            if (errors.Any(e => e.Section == field.Section && e.Field == field.Field))
                continue;
            errors.Add(new RegistrationDraftFieldError(field.Section, field.Field, failure.ErrorMessage));
        }

        return errors;
    }

    /// <summary>Clears every outstanding code of the draft: a number change must leave no code that still verifies.</summary>
    public static Task<int> ClearOutstandingCodesAsync(CentralDbContext central, Guid draftId, CancellationToken ct) =>
        central.RegistrationPhoneVerifications
            .Where(r => r.DraftId == draftId && r.ProtectedCode != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProtectedCode, (string?)null)
                .SetProperty(r => r.CodeExpiresAtUtc, (DateTime?)null), ct);
}
