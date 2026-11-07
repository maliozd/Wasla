using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Sms;
using Wasla.Application.Onboarding;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The registration draft (WAS-192): section saves validated with the registration rules of each section, and the
/// gated submit that is the only way a signup creates a <see cref="PendingRegistration"/>.
/// </summary>
/// <remarks>
/// Every write raises the draft's <see cref="RegistrationDraft.Revision"/>; a write that loses to a concurrent one is
/// evaluated again from the stored row (at most three times). The submit claims the draft with a conditional update on
/// that revision inside the transaction that inserts the registration, so the registration is exactly what was validated
/// and a draft yields at most one registration.
/// </remarks>
public sealed class RegistrationDraftService : IRegistrationDraftService
{
    private const int MaxAttempts = 3;

    private readonly CentralDbContext _central;
    private readonly IWaslaPlanCatalog _planCatalog;
    private readonly ISignupReferenceDataService _referenceData;
    private readonly PendingRegistrationService _registrations;
    private readonly ISmsSender _sms;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationDraftService> _logger;

    public RegistrationDraftService(
        CentralDbContext central,
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData,
        PendingRegistrationService registrations,
        ISmsSender sms,
        IOptions<SignupOptions> options,
        TimeProvider time,
        ILogger<RegistrationDraftService> logger)
    {
        _central = central;
        _planCatalog = planCatalog;
        _referenceData = referenceData;
        _registrations = registrations;
        _sms = sms;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<RegistrationDraftView?> GetAsync(Guid draftId, CancellationToken ct)
    {
        var now = Now;
        var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
        return draft is null ? null : await ViewAsync(draft, now, ct);
    }

    public Task<RegistrationDraftSaveResult> SavePlanAsync(Guid? draftId, RegistrationPlanInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = Empty() with
        {
            PlanCode = input.PlanCode?.Trim() ?? string.Empty,
            BillingPeriod = input.BillingPeriod?.Trim() ?? string.Empty
        };

        return SaveAsync(draftId, ct, async (_, _) =>
        {
            var result = await PendingRegistrationRules
                .ForSection(RegistrationDraftSection.Plan, _planCatalog, _referenceData)
                .ValidateAsync(request, ct);
            return RegistrationDraftRecords.FieldErrors(result, RegistrationDraftSection.Plan);
        }, (draft, now) =>
        {
            draft.PlanCode = _planCatalog.FindByCode(request.PlanCode)!.PlanCode;
            draft.BillingPeriod = string.Equals(request.BillingPeriod, "Yearly", StringComparison.OrdinalIgnoreCase) ? "Yearly" : "Monthly";
            draft.PlanSavedAtUtc = now;
            return false;
        });
    }

    public Task<RegistrationDraftSaveResult> SaveRestaurantAsync(Guid? draftId, RegistrationRestaurantInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        PendingRegistrationRequest request = Empty();

        return SaveAsync(draftId, ct, async (_, _) =>
        {
            request = await RestaurantRequestAsync(input, ct);
            var result = await PendingRegistrationRules
                .ForSection(RegistrationDraftSection.Restaurant, _planCatalog, _referenceData)
                .ValidateAsync(request, ct);
            var errors = RegistrationDraftRecords.FieldErrors(result, RegistrationDraftSection.Restaurant);
            // Advisory, like the old form: a taken or reserved address is refused here and checked again at submit.
            if (!errors.Any(e => e.Field == "slug") && !await _registrations.IsSlugAvailableAsync(request.Slug, ct))
                errors.Add(new RegistrationDraftFieldError(RegistrationDraftSection.Restaurant, "slug", SignupErrorCodes.SlugUnavailable));
            return errors;
        }, (draft, now) =>
        {
            var isTurkey = PendingRegistrationRules.IsTurkey(request.Country);
            draft.BusinessName = request.BusinessName;
            draft.BusinessTypeCodes = string.Join(',', request.BusinessTypeCodes);
            draft.BusinessPhoneType = string.Equals(request.BusinessPhoneType, nameof(BusinessPhoneType.Landline), StringComparison.OrdinalIgnoreCase)
                ? BusinessPhoneType.Landline
                : BusinessPhoneType.Mobile;
            draft.BusinessPhone = request.BusinessPhone;
            draft.BusinessEmail = request.BusinessEmail;
            draft.Slug = request.Slug;
            draft.Country = request.Country;
            draft.CityId = isTurkey ? request.CityId : null;
            draft.DistrictId = isTurkey ? request.DistrictId : null;
            draft.NeighborhoodId = isTurkey ? request.NeighborhoodId : null;
            draft.StreetId = isTurkey ? request.StreetId : null;
            draft.City = request.City;
            draft.District = request.District;
            draft.Neighborhood = request.Neighborhood;
            draft.StreetAddress = request.StreetAddress;
            draft.BuildingNumber = request.BuildingNumber;
            draft.Floor = request.Floor;
            draft.DoorNumber = request.DoorNumber;
            draft.AddressNote = request.AddressNote;
            draft.PostalCode = request.PostalCode;
            draft.LocationUrl = request.LocationUrl;
            draft.RestaurantSavedAtUtc = now;
            return false;
        });
    }

    public Task<RegistrationDraftSaveResult> SaveOwnerAsync(Guid? draftId, RegistrationOwnerInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = Empty() with
        {
            OwnerFullName = input.FullName?.Trim() ?? string.Empty,
            OwnerEmail = input.Email?.Trim() ?? string.Empty,
            OwnerPhone = input.MobilePhone,
            Password = input.Password ?? string.Empty
        };
        var passwordTyped = !string.IsNullOrEmpty(input.Password);

        return SaveAsync(draftId, ct, async (draft, _) =>
        {
            // The first save needs a password; a later save without one keeps the stored hash.
            var passwordRequired = passwordTyped || draft?.PasswordHash is null;
            var result = await PendingRegistrationRules
                .ForSection(RegistrationDraftSection.Owner, _planCatalog, _referenceData, passwordRequired)
                .ValidateAsync(request, ct);
            var errors = RegistrationDraftRecords.FieldErrors(result, RegistrationDraftSection.Owner);
            if (passwordTyped && !errors.Any(e => e.Field == "password"))
            {
                if (string.IsNullOrEmpty(input.ConfirmPassword))
                    errors.Add(new RegistrationDraftFieldError(RegistrationDraftSection.Owner, "confirmPassword", "Validation.ConfirmPasswordRequired"));
                else if (!string.Equals(input.Password, input.ConfirmPassword, StringComparison.Ordinal))
                    errors.Add(new RegistrationDraftFieldError(RegistrationDraftSection.Owner, "confirmPassword", "Validation.PasswordMismatch"));
            }

            return errors;
        }, (draft, now) =>
        {
            TurkishMobilePhone.TryNormalize(request.OwnerPhone, out var mobile);
            draft.OwnerFullName = request.OwnerFullName;
            draft.OwnerEmail = request.OwnerEmail;
            if (passwordTyped)
                draft.PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password);
            draft.OwnerSavedAtUtc = now;

            if (string.Equals(draft.OwnerMobilePhone, mobile, StringComparison.Ordinal))
                return false;

            // A changed number needs verification again, and no code sent to the old one may verify the new one.
            draft.OwnerMobilePhone = mobile;
            draft.PhoneVerifiedNumber = null;
            draft.PhoneVerifiedAtUtc = null;
            return true;
        });
    }

    public async Task<RegistrationDraftSubmitResult> SubmitAsync(Guid draftId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            _central.ChangeTracker.Clear();
            var now = Now;
            var draft = await RegistrationDraftRecords.LoadLiveAsync(_central, draftId, now, tracking: false, ct);
            if (draft is null)
                return RegistrationDraftSubmitResult.Failed(SignupErrorCodes.DraftMissing);
            if (draft.PendingRegistrationId is { } existing)
                return RegistrationDraftSubmitResult.Submitted(existing, alreadySubmitted: true);

            var missing = new List<RegistrationDraftSection>();
            if (draft.PlanSavedAtUtc is null)
                missing.Add(RegistrationDraftSection.Plan);
            if (draft.RestaurantSavedAtUtc is null)
                missing.Add(RegistrationDraftSection.Restaurant);
            if (!RegistrationDraftRecords.OwnerSaved(draft))
                missing.Add(RegistrationDraftSection.Owner);
            if (missing.Count > 0)
                return new RegistrationDraftSubmitResult(null, false, SignupErrorCodes.DraftIncomplete, Array.Empty<RegistrationDraftFieldError>(), missing);

            // Everything again, from the stored values only: reference data or plans may have changed since the saves.
            var request = RegistrationDraftRecords.Request(draft);
            var validation = await PendingRegistrationRules.ForStoredDraft(_planCatalog, _referenceData).ValidateAsync(request, ct);
            var fieldErrors = RegistrationDraftRecords.FieldErrors(
                validation, RegistrationDraftSection.Plan, RegistrationDraftSection.Restaurant, RegistrationDraftSection.Owner);
            if (fieldErrors.Count > 0)
                return new RegistrationDraftSubmitResult(null, false, SignupErrorCodes.DraftValidationFailed, fieldErrors, Array.Empty<RegistrationDraftSection>());

            if (!RegistrationDraftRecords.ProofIsCurrent(draft, now, _options.Draft))
                return RegistrationDraftSubmitResult.Failed(SignupErrorCodes.PhoneNotVerified);

            _central.ChangeTracker.Clear();
            await using var transaction = await _central.Database.BeginTransactionAsync(ct);

            // Claims the draft as it was validated. A concurrent submit waits for this row and then finds it submitted.
            var revision = draft.Revision;
            var claimed = await _central.RegistrationDrafts
                .Where(d => d.Id == draftId && d.SubmittedAtUtc == null && d.Revision == revision && d.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.SubmittedAtUtc, now)
                    .SetProperty(d => d.Revision, d => d.Revision + 1), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                continue;
            }

            var created = await _registrations.CreateAsync(
                new VerifiedRegistration(request, draft.PasswordHash!, draft.OwnerMobilePhone!, draft.PhoneVerifiedAtUtc!.Value),
                ct);
            if (!created.Success)
            {
                await transaction.RollbackAsync(ct);
                _central.ChangeTracker.Clear();
                return SubmitFailure(created.Error);
            }

            var registrationId = created.RegistrationId!.Value;
            // The registration now holds what it needs; the draft keeps no personal data, only its link.
            await _central.RegistrationDrafts
                .Where(d => d.Id == draftId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.PendingRegistrationId, registrationId)
                    .SetProperty(d => d.BusinessName, (string?)null)
                    .SetProperty(d => d.BusinessTypeCodes, (string?)null)
                    .SetProperty(d => d.BusinessPhone, (string?)null)
                    .SetProperty(d => d.BusinessEmail, (string?)null)
                    .SetProperty(d => d.Slug, (string?)null)
                    .SetProperty(d => d.City, (string?)null)
                    .SetProperty(d => d.District, (string?)null)
                    .SetProperty(d => d.Neighborhood, (string?)null)
                    .SetProperty(d => d.StreetAddress, (string?)null)
                    .SetProperty(d => d.BuildingNumber, (string?)null)
                    .SetProperty(d => d.Floor, (string?)null)
                    .SetProperty(d => d.DoorNumber, (string?)null)
                    .SetProperty(d => d.AddressNote, (string?)null)
                    .SetProperty(d => d.PostalCode, (string?)null)
                    .SetProperty(d => d.LocationUrl, (string?)null)
                    .SetProperty(d => d.CityId, (int?)null)
                    .SetProperty(d => d.DistrictId, (int?)null)
                    .SetProperty(d => d.NeighborhoodId, (int?)null)
                    .SetProperty(d => d.StreetId, (int?)null)
                    .SetProperty(d => d.OwnerFullName, (string?)null)
                    .SetProperty(d => d.OwnerEmail, (string?)null)
                    .SetProperty(d => d.OwnerMobilePhone, (string?)null)
                    .SetProperty(d => d.PasswordHash, (string?)null)
                    .SetProperty(d => d.PhoneVerifiedNumber, (string?)null), ct);
            await _central.RegistrationPhoneVerifications
                .Where(r => r.DraftId == draftId)
                .ExecuteDeleteAsync(ct);

            await transaction.CommitAsync(ct);
            _central.ChangeTracker.Clear();

            _logger.LogInformation(
                "Registration draft submitted. DraftId={DraftId} RegistrationId={RegistrationId}",
                draftId,
                registrationId);
            return RegistrationDraftSubmitResult.Submitted(registrationId, alreadySubmitted: false);
        }

        return RegistrationDraftSubmitResult.Failed(SignupErrorCodes.RegistrationFailed);
    }

    private static RegistrationDraftSubmitResult SubmitFailure(PendingRegistrationError error) => error switch
    {
        PendingRegistrationError.TooManyActiveRegistrations => RegistrationDraftSubmitResult.Failed(SignupErrorCodes.TooManyAttempts),
        PendingRegistrationError.DuplicateSlug or PendingRegistrationError.DuplicateDomain or PendingRegistrationError.InvalidSlug =>
            new RegistrationDraftSubmitResult(
                null,
                false,
                SignupErrorCodes.SlugUnavailable,
                [new RegistrationDraftFieldError(RegistrationDraftSection.Restaurant, "slug", SignupErrorCodes.SlugUnavailable)],
                Array.Empty<RegistrationDraftSection>()),
        PendingRegistrationError.InvalidPlan =>
            new RegistrationDraftSubmitResult(
                null,
                false,
                SignupErrorCodes.DraftValidationFailed,
                [new RegistrationDraftFieldError(RegistrationDraftSection.Plan, "planCode", "Validation.PlanInvalid")],
                Array.Empty<RegistrationDraftSection>()),
        _ => RegistrationDraftSubmitResult.Failed(SignupErrorCodes.RegistrationFailed)
    };

    /// <summary>
    /// The shared save path: opens the draft (or prepares a new one), validates, applies and stores. <paramref name="apply"/>
    /// returns true when the owner's number changed, so every outstanding code is cleared in the same transaction.
    /// </summary>
    private async Task<RegistrationDraftSaveResult> SaveAsync(
        Guid? draftId,
        CancellationToken ct,
        Func<RegistrationDraft?, DateTime, Task<List<RegistrationDraftFieldError>>> validate,
        Func<RegistrationDraft, DateTime, bool> apply)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            _central.ChangeTracker.Clear();
            var now = Now;
            RegistrationDraft? draft = null;
            if (draftId is { } id)
            {
                draft = await RegistrationDraftRecords.LoadLiveAsync(_central, id, now, tracking: true, ct);
                if (draft is null)
                    return RegistrationDraftSaveResult.Failed(SignupErrorCodes.DraftMissing);
                if (draft.SubmittedAtUtc is not null)
                    return RegistrationDraftSaveResult.Failed(SignupErrorCodes.DraftSubmitted);
            }

            var errors = await validate(draft, now);
            if (errors.Count > 0)
                return RegistrationDraftSaveResult.Invalid(errors);

            var created = draft is null;
            draft ??= new RegistrationDraft { Id = Guid.NewGuid(), CreatedAtUtc = now };
            var numberChanged = apply(draft, now);
            draft.UpdatedAtUtc = now;
            draft.ExpiresAtUtc = RegistrationDraftRecords.ExpiryAfterWrite(draft, now, _options.Draft);
            draft.Revision++;
            if (created)
                _central.RegistrationDrafts.Add(draft);

            try
            {
                if (numberChanged && !created)
                {
                    await using var transaction = await _central.Database.BeginTransactionAsync(ct);
                    await _central.SaveChangesAsync(ct);
                    await RegistrationDraftRecords.ClearOutstandingCodesAsync(_central, draft.Id, ct);
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    await _central.SaveChangesAsync(ct);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another write changed the draft since it was read: evaluate again from the stored row.
                continue;
            }

            _central.ChangeTracker.Clear();
            return RegistrationDraftSaveResult.Saved(await ViewAsync(draft, now, ct), created);
        }

        return RegistrationDraftSaveResult.Failed(SignupErrorCodes.RegistrationFailed);
    }

    private async Task<RegistrationDraftView> ViewAsync(RegistrationDraft draft, DateTime now, CancellationToken ct)
    {
        var row = draft.OwnerMobilePhone is null
            ? null
            : await RegistrationDraftRecords.LoadVerificationAsync(_central, draft.Id, draft.OwnerMobilePhone, tracking: false, ct);
        return RegistrationDraftRecords.View(
            draft,
            RegistrationDraftRecords.Status(draft, row, _options, _sms.IsAvailable, now));
    }

    /// <summary>The restaurant input as a registration request, with Türkiye address names resolved from their ids.</summary>
    private async Task<PendingRegistrationRequest> RestaurantRequestAsync(RegistrationRestaurantInput input, CancellationToken ct)
    {
        var country = Trimmed(input.Country) ?? string.Empty;
        var city = Trimmed(input.City);
        var district = Trimmed(input.District);
        var neighborhood = Trimmed(input.Neighborhood);
        var streetAddress = Trimmed(input.StreetAddress);

        if (PendingRegistrationRules.IsTurkey(country) && input.CityId is > 0 && input.DistrictId is > 0)
        {
            var names = await _referenceData.ResolveCityDistrictAsync(input.CityId.Value, input.DistrictId.Value, country, ct);
            city = names?.CityName;
            district = names?.DistrictName;
            if (names is not null && input.NeighborhoodId is { } neighborhoodId)
            {
                var resolved = await _referenceData.ResolveNeighborhoodAsync(input.DistrictId.Value, neighborhoodId, ct);
                if (resolved is not null)
                {
                    neighborhood = resolved.NeighborhoodName;
                    if (input.StreetId is { } streetId
                        && await _referenceData.ResolveStreetAsync(neighborhoodId, streetId, ct) is { } street)
                    {
                        streetAddress = string.IsNullOrWhiteSpace(street.StreetType)
                            ? street.StreetName.Trim()
                            : $"{street.StreetName.Trim()} {street.StreetType.Trim()}";
                    }
                }
            }
        }

        var codes = (input.BusinessTypeCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return Empty() with
        {
            BusinessName = Trimmed(input.BusinessName) ?? string.Empty,
            BusinessTypeCodes = codes,
            BusinessPhoneType = Trimmed(input.BusinessPhoneType) ?? string.Empty,
            BusinessPhone = Trimmed(input.BusinessPhone) ?? string.Empty,
            BusinessEmail = Trimmed(input.BusinessEmail),
            Slug = RegistrationNameNormalizer.NormalizeSlug(input.Slug ?? string.Empty),
            Country = country,
            CityId = input.CityId,
            DistrictId = input.DistrictId,
            NeighborhoodId = input.NeighborhoodId,
            StreetId = input.StreetId,
            City = city,
            District = district,
            Neighborhood = neighborhood,
            StreetAddress = streetAddress,
            BuildingNumber = Trimmed(input.BuildingNumber),
            Floor = Trimmed(input.Floor),
            DoorNumber = Trimmed(input.DoorNumber),
            AddressNote = Trimmed(input.AddressNote),
            PostalCode = Trimmed(input.PostalCode),
            LocationUrl = Trimmed(input.LocationUrl)
        };
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PendingRegistrationRequest Empty() => new(
        string.Empty, string.Empty, string.Empty, Array.Empty<string>(), string.Empty, string.Empty, null, string.Empty,
        string.Empty, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        string.Empty, string.Empty, null, string.Empty);
}
