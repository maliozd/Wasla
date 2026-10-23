using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Onboarding;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class PendingRegistrationService : IPendingRegistrationService
{
    private const int DatabaseNameAttempts = 3;

    private readonly CentralDbContext _central;
    private readonly IWaslaPlanCatalog _planCatalog;
    private readonly ISignupReferenceDataService _referenceData;
    private readonly TenantOnboardingOptions _options;
    private readonly ILogger<PendingRegistrationService> _logger;
    private readonly TimeProvider _time;

    public PendingRegistrationService(
        CentralDbContext central,
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData,
        IOptions<TenantOnboardingOptions> options,
        ILogger<PendingRegistrationService> logger,
        TimeProvider? time = null)
    {
        _central = central;
        _planCatalog = planCatalog;
        _referenceData = referenceData;
        _options = options.Value;
        _logger = logger;
        // The clock checkout (BillingSubscriptionStore) uses, so the summary and checkout pick the same price.
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct)
    {
        var normalized = RegistrationNameNormalizer.NormalizeSlug(slug);
        if (!RegistrationNameNormalizer.IsValidSlugFormat(normalized) || RegistrationNameNormalizer.IsReservedSlug(normalized))
            return false;

        var primaryDomain = RegistrationNameNormalizer.BuildPrimaryDomain(normalized, _options.ApplicationBaseDomain);
        return !await IsSlugOrDomainTakenAsync(normalized, primaryDomain, ct);
    }

    public async Task<PendingRegistrationResult> SubmitAsync(PendingRegistrationRequest request, CancellationToken ct)
    {
        var slug = RegistrationNameNormalizer.NormalizeSlug(request.Slug);
        if (!RegistrationNameNormalizer.IsValidSlugFormat(slug))
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.InvalidSlug);
        }

        var plan = _planCatalog.FindByCode(request.PlanCode);
        if (plan is null || plan.IsContactSales)
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.InvalidPlan);
        }

        // Before any other lookup, the password hash and the insert: one email cannot accumulate registrations.
        if (await HasMaximumActiveRegistrationsAsync(request.OwnerEmail, ct))
        {
            _logger.LogInformation("Pending registration refused: the owner email has the maximum number of active registrations.");
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.TooManyActiveRegistrations);
        }

        // A reserved name is never a tenant address (WAS-66); it is answered like a taken one.
        var primaryDomain = RegistrationNameNormalizer.BuildPrimaryDomain(slug, _options.ApplicationBaseDomain);
        if (RegistrationNameNormalizer.IsReservedSlug(slug) || await IsSlugOrDomainTakenAsync(slug, primaryDomain, ct))
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.DuplicateSlug);
        }

        var databaseName = await AssignDatabaseNameAsync(slug, ct);
        if (databaseName is null)
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.DuplicateDatabaseName);
        }

        var businessTypes = await _referenceData.ResolveBusinessTypesByCodesAsync(request.BusinessTypeCodes, ct);
        var businessTypeDisplay = string.Join(", ", businessTypes.Select(x => x.DisplayName));
        var businessTypeCodes = businessTypes.Select(x => x.Code).ToList();
        var businessTypeIds = await _central.BusinessTypes.AsNoTracking()
            .Where(x => businessTypeCodes.Contains(x.Code))
            .Select(x => x.Id)
            .ToListAsync(ct);

        var cityName = request.City?.Trim() ?? string.Empty;
        var districtName = request.District?.Trim() ?? string.Empty;
        if (request.CityId is int cityId && request.DistrictId is int districtId)
        {
            var resolvedCityDistrict = await _referenceData.ResolveCityDistrictAsync(
                cityId,
                districtId,
                request.Country,
                ct);

            if (resolvedCityDistrict is not null)
            {
                cityName = resolvedCityDistrict.CityName;
                districtName = resolvedCityDistrict.DistrictName;
            }
        }

        var now = DateTime.UtcNow;
        var expiryDays = Math.Max(1, _options.PendingRegistrationExpiryDays);
        var registration = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            PlanCode = plan.PlanCode,
            BillingPeriod = NormalizeBillingPeriod(request.BillingPeriod),
            BusinessName = request.BusinessName.Trim(),
            BusinessType = businessTypeDisplay,
            BusinessPhoneType = ParseBusinessPhoneType(request.BusinessPhoneType),
            Slug = slug,
            PrimaryDomain = primaryDomain,
            DatabaseName = databaseName,
            BusinessPhone = NormalizeBusinessPhone(request.BusinessPhone),
            BusinessEmail = string.IsNullOrWhiteSpace(request.BusinessEmail) ? null : request.BusinessEmail.Trim(),
            Country = request.Country.Trim(),
            CityId = request.CityId,
            DistrictId = request.DistrictId,
            NeighborhoodId = request.NeighborhoodId,
            StreetId = request.StreetId,
            City = cityName,
            District = districtName,
            Neighborhood = string.IsNullOrWhiteSpace(request.Neighborhood) ? null : request.Neighborhood.Trim(),
            StreetAddress = string.IsNullOrWhiteSpace(request.StreetAddress) ? null : request.StreetAddress.Trim(),
            BuildingNumber = string.IsNullOrWhiteSpace(request.BuildingNumber) ? null : request.BuildingNumber.Trim(),
            Floor = string.IsNullOrWhiteSpace(request.Floor) ? null : request.Floor.Trim(),
            DoorNumber = string.IsNullOrWhiteSpace(request.DoorNumber) ? null : request.DoorNumber.Trim(),
            AddressNote = string.IsNullOrWhiteSpace(request.AddressNote) ? null : request.AddressNote.Trim(),
            PostalCode = string.IsNullOrWhiteSpace(request.PostalCode) ? null : request.PostalCode.Trim(),
            LocationUrl = string.IsNullOrWhiteSpace(request.LocationUrl) ? null : request.LocationUrl.Trim(),
            OwnerFullName = request.OwnerFullName.Trim(),
            OwnerEmail = request.OwnerEmail.Trim(),
            OwnerPhone = string.IsNullOrWhiteSpace(request.OwnerPhone) ? null : request.OwnerPhone.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Status = PendingRegistrationStatus.AwaitingPayment,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(expiryDays)
        };

        try
        {
            foreach (var businessTypeId in businessTypeIds)
            {
                registration.BusinessTypes.Add(new PendingRegistrationBusinessType
                {
                    PendingRegistrationId = registration.Id,
                    BusinessTypeId = businessTypeId
                });
            }

            _central.PendingRegistrations.Add(registration);
            await _central.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Pending registration created. RegistrationId={RegistrationId} Slug={Slug} DatabaseName={DatabaseName}",
                registration.Id,
                slug,
                databaseName);

            return new PendingRegistrationResult(
                true,
                registration.Id,
                primaryDomain,
                slug,
                databaseName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending registration save failed for slug {Slug}", slug);
            return new PendingRegistrationResult(
                false,
                null,
                null,
                null,
                null,
                PendingRegistrationError.SaveFailed,
                ex.Message);
        }
    }

    public async Task<PendingRegistrationSummary?> GetSummaryAsync(Guid registrationId, CancellationToken ct)
    {
        var row = await _central.PendingRegistrations.AsNoTracking()
            .Where(p => p.Id == registrationId)
            .Select(p => new PendingRegistrationSummary(
                p.Id,
                p.BusinessName,
                p.PrimaryDomain,
                p.PlanCode,
                p.BillingPeriod,
                p.Status,
                p.BusinessPhone,
                p.BusinessEmail,
                p.OwnerFullName,
                p.OwnerEmail,
                p.OwnerPhone,
                p.CreatedAtUtc,
                p.PaymentSucceededAtUtc,
                p.ProvisionedAtUtc))
            .FirstOrDefaultAsync(ct);

        return row;
    }

    public async Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid registrationId, CancellationToken ct)
    {
        var row = await _central.PendingRegistrations.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == registrationId, ct);

        if (row is null)
            return null;

        var businessTypeRows = await (
            from link in _central.PendingRegistrationBusinessTypes.AsNoTracking()
            join bt in _central.BusinessTypes.AsNoTracking() on link.BusinessTypeId equals bt.Id
            where link.PendingRegistrationId == registrationId
            orderby bt.SortOrder
            select new { bt.Code, bt.DisplayName }).ToListAsync(ct);

        var businessTypesLabel = businessTypeRows.Count > 0
            ? string.Join(", ", businessTypeRows.Select(x => x.DisplayName))
            : row.BusinessType ?? string.Empty;
        var businessTypeCodes = businessTypeRows.Select(x => x.Code).ToArray();

        // The price a checkout for this registration charges (WAS-147): its subscription's snapshot once one has
        // started, otherwise the price in effect now. Never a newer price for an existing subscription.
        var price = await BillingPlanPriceQueries.ForRegistrationAsync(
            _central, row.Id, row.PlanCode, row.BillingPeriod, _time.GetUtcNow().UtcDateTime, ct);

        return new PendingRegistrationCheckoutDetails(
            row.Id,
            row.PlanCode,
            row.BillingPeriod,
            row.BusinessName,
            row.PrimaryDomain,
            businessTypesLabel,
            row.BusinessPhone,
            row.OwnerFullName,
            row.OwnerEmail,
            row.OwnerPhone,
            row.Country,
            row.City,
            row.District,
            row.Neighborhood,
            row.StreetAddress,
            row.BuildingNumber,
            row.Floor,
            row.DoorNumber,
            row.AddressNote,
            row.PostalCode,
            row.LocationUrl,
            row.Status,
            price,
            string.Equals(price?.BillingPeriod ?? row.BillingPeriod, BillingSubscription.YearlyPeriod, StringComparison.Ordinal),
            businessTypeCodes);
    }

    public async Task<CheckoutSimulationResult> SimulatePaymentSuccessAsync(Guid registrationId, CancellationToken ct)
    {
        var registration = await _central.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Id == registrationId, ct);

        if (registration is null)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.NotFound, null);

        var terminal = MapTerminalOutcome(registration.Status, registration.Id);
        if (terminal is not null)
            return terminal;

        if (registration.Status is not (PendingRegistrationStatus.AwaitingPayment or PendingRegistrationStatus.PaymentFailed))
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.InvalidState, registration.Id);

        if (IsExpired(registration))
            return await MarkExpiredAsync(registration, ct);

        var reference = GenerateSimulatedPaymentReference();
        var now = DateTime.UtcNow;
        var applied = await TryChangeStatusAsync(registration, setters => setters
            .SetProperty(p => p.Status, PendingRegistrationStatus.PaymentSucceeded)
            .SetProperty(p => p.PaymentSucceededAtUtc, now)
            .SetProperty(p => p.PaymentFailedAtUtc, (DateTime?)null)
            .SetProperty(p => p.SimulatedPaymentReference, reference), ct);
        if (!applied)
            return await OutcomeAfterConcurrentChangeAsync(registration.Id, ct);

        _logger.LogInformation(
            "Simulated payment success. RegistrationId={RegistrationId} Reference={Reference}",
            registration.Id,
            reference);

        return new CheckoutSimulationResult(CheckoutSimulationOutcome.Applied, registration.Id, reference);
    }

    public async Task<CheckoutSimulationResult> SimulatePaymentFailedAsync(Guid registrationId, CancellationToken ct)
    {
        var registration = await _central.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Id == registrationId, ct);

        if (registration is null)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.NotFound, null);

        var terminal = MapTerminalOutcome(registration.Status, registration.Id);
        if (terminal is not null)
            return terminal;

        if (registration.Status is not (PendingRegistrationStatus.AwaitingPayment or PendingRegistrationStatus.PaymentFailed))
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.InvalidState, registration.Id);

        if (IsExpired(registration))
            return await MarkExpiredAsync(registration, ct);

        var now = DateTime.UtcNow;
        var applied = await TryChangeStatusAsync(registration, setters => setters
            .SetProperty(p => p.Status, PendingRegistrationStatus.PaymentFailed)
            .SetProperty(p => p.PaymentFailedAtUtc, now), ct);
        if (!applied)
            return await OutcomeAfterConcurrentChangeAsync(registration.Id, ct);

        _logger.LogInformation("Simulated payment failure. RegistrationId={RegistrationId}", registration.Id);

        return new CheckoutSimulationResult(CheckoutSimulationOutcome.Applied, registration.Id);
    }

    public async Task<CheckoutSimulationResult> CancelRegistrationAsync(Guid registrationId, CancellationToken ct)
    {
        var registration = await _central.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Id == registrationId, ct);

        if (registration is null)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.NotFound, null);

        if (registration.Status == PendingRegistrationStatus.Cancelled)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyCancelled, registration.Id);

        if (registration.Status is PendingRegistrationStatus.Provisioned)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyProvisioned, registration.Id);

        if (registration.Status is PendingRegistrationStatus.PaymentSucceeded)
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyPaymentSucceeded, registration.Id);

        if (registration.Status is not (PendingRegistrationStatus.AwaitingPayment or PendingRegistrationStatus.PaymentFailed))
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.InvalidState, registration.Id);

        var applied = await TryChangeStatusAsync(registration, setters => setters
            .SetProperty(p => p.Status, PendingRegistrationStatus.Cancelled), ct);
        if (!applied)
            return await OutcomeAfterConcurrentChangeAsync(registration.Id, ct);

        _logger.LogInformation("Pending registration cancelled. RegistrationId={RegistrationId}", registration.Id);

        return new CheckoutSimulationResult(CheckoutSimulationOutcome.Applied, registration.Id);
    }

    /// <summary>
    /// Applies a status change only if the stored status is still the one this request read. A second
    /// request (another tab, a double submit) may change the registration in between, and that change
    /// must not be overwritten; for example a paid registration must never become cancelled or failed.
    /// </summary>
    private async Task<bool> TryChangeStatusAsync(
        PendingRegistration registration,
        Expression<Func<SetPropertyCalls<PendingRegistration>, SetPropertyCalls<PendingRegistration>>> setters,
        CancellationToken ct)
    {
        var id = registration.Id;
        var observedStatus = registration.Status;
        var updated = await _central.PendingRegistrations
            .Where(p => p.Id == id && p.Status == observedStatus)
            .ExecuteUpdateAsync(setters, ct);
        return updated == 1;
    }

    private async Task<CheckoutSimulationResult> MarkExpiredAsync(PendingRegistration registration, CancellationToken ct)
    {
        var applied = await TryChangeStatusAsync(registration, setters => setters
            .SetProperty(p => p.Status, PendingRegistrationStatus.Expired), ct);
        return applied
            ? new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyExpired, registration.Id)
            : await OutcomeAfterConcurrentChangeAsync(registration.Id, ct);
    }

    /// <summary>Reports the status another request just stored instead of applying this one.</summary>
    private async Task<CheckoutSimulationResult> OutcomeAfterConcurrentChangeAsync(Guid registrationId, CancellationToken ct)
    {
        var status = await _central.PendingRegistrations
            .AsNoTracking()
            .Where(p => p.Id == registrationId)
            .Select(p => (PendingRegistrationStatus?)p.Status)
            .FirstOrDefaultAsync(ct);

        return status switch
        {
            null => new CheckoutSimulationResult(CheckoutSimulationOutcome.NotFound, null),
            PendingRegistrationStatus.PaymentFailed => new CheckoutSimulationResult(
                CheckoutSimulationOutcome.AlreadyPaymentFailed, registrationId),
            _ => MapTerminalOutcome(status.Value, registrationId)
                ?? new CheckoutSimulationResult(CheckoutSimulationOutcome.InvalidState, registrationId)
        };
    }

    private static CheckoutSimulationResult? MapTerminalOutcome(PendingRegistrationStatus status, Guid registrationId) =>
        status switch
        {
            PendingRegistrationStatus.PaymentSucceeded => new CheckoutSimulationResult(
                CheckoutSimulationOutcome.AlreadyPaymentSucceeded, registrationId),
            PendingRegistrationStatus.PaymentFailed => null,
            PendingRegistrationStatus.Cancelled => new CheckoutSimulationResult(
                CheckoutSimulationOutcome.AlreadyCancelled, registrationId),
            PendingRegistrationStatus.Provisioned => new CheckoutSimulationResult(
                CheckoutSimulationOutcome.AlreadyProvisioned, registrationId),
            PendingRegistrationStatus.Expired => new CheckoutSimulationResult(
                CheckoutSimulationOutcome.AlreadyExpired, registrationId),
            _ => null
        };

    private static bool IsExpired(PendingRegistration registration) =>
        registration.ExpiresAtUtc.HasValue && registration.ExpiresAtUtc.Value <= DateTime.UtcNow;

    private static string GenerateSimulatedPaymentReference()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}-{suffix}";
    }

    private async Task<bool> IsSlugOrDomainTakenAsync(string slug, string primaryDomain, CancellationToken ct)
    {
        var customerTaken = await _central.Tenants.AsNoTracking()
            .AnyAsync(c => c.Slug == slug || c.PrimaryDomain == primaryDomain, ct);
        if (customerTaken)
            return true;

        return await _central.PendingRegistrations.AsNoTracking()
            .Where(PendingRegistrationReservations.HoldsReservation(DateTime.UtcNow))
            .AnyAsync(p => p.Slug == slug || p.PrimaryDomain == primaryDomain, ct);
    }

    public async Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct)
    {
        var normalizedHost = RegistrationNameNormalizer.NormalizeHostForComparison(primaryDomain);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return null;

        var normalizedSlug = RegistrationNameNormalizer.ExtractSlugFromHost(
            normalizedHost,
            _options.ApplicationBaseDomain);
        var now = DateTime.UtcNow;

        var row = await _central.PendingRegistrations.AsNoTracking()
            .Where(p => (p.PrimaryDomain.ToLower() == normalizedHost
                         || (normalizedSlug != null && p.Slug == normalizedSlug))
                     && p.Status != PendingRegistrationStatus.PaymentFailed
                     && p.Status != PendingRegistrationStatus.Cancelled
                     && p.Status != PendingRegistrationStatus.Expired
                     // A paid registration keeps its address until provisioned, past its original expiry (WAS-93).
                     && (p.Status == PendingRegistrationStatus.PaymentSucceeded
                         || p.ExpiresAtUtc == null
                         || p.ExpiresAtUtc > now))
            .OrderByDescending(p => p.CreatedAtUtc)
            .Select(p => new PendingRegistrationSummary(
                p.Id,
                p.BusinessName,
                p.PrimaryDomain,
                p.PlanCode,
                p.BillingPeriod,
                p.Status,
                p.BusinessPhone,
                p.BusinessEmail,
                p.OwnerFullName,
                p.OwnerEmail,
                p.OwnerPhone,
                p.CreatedAtUtc,
                p.PaymentSucceededAtUtc,
                p.ProvisionedAtUtc))
            .FirstOrDefaultAsync(ct);

        return row;
    }

    private async Task<bool> HasMaximumActiveRegistrationsAsync(string ownerEmail, CancellationToken ct)
    {
        var maximum = Math.Max(1, _options.MaxActivePendingRegistrationsPerOwnerEmail);
        var normalizedEmail = ownerEmail.Trim().ToLowerInvariant();
        var active = await _central.PendingRegistrations.AsNoTracking()
            .Where(PendingRegistrationReservations.HoldsReservation(DateTime.UtcNow))
            .Where(p => p.OwnerEmail.ToLower() == normalizedEmail)
            .Take(maximum)
            .CountAsync(ct);
        return active >= maximum;
    }

    /// <summary>
    /// A server-assigned database name (WAS-93): a label from the slug and a random suffix, never derived from the
    /// business name and never CentralDb or a system database. A name is free when no tenant and no registration in any
    /// status uses it: one query per table per attempt, and a random 48-bit suffix practically never needs a second.
    /// Whether the name is free on the SQL Server is checked when provisioning creates the database.
    /// </summary>
    private async Task<string?> AssignDatabaseNameAsync(string slug, CancellationToken ct)
    {
        var centralCatalog = _central.Database.GetDbConnection().Database;
        for (var attempt = 0; attempt < DatabaseNameAttempts; attempt++)
        {
            var candidate = TenantDatabaseNames.Generate(slug);
            if (TenantDatabaseNames.GetRejectionReason(candidate, centralCatalog) is not null)
                continue;

            var taken = await _central.Tenants.AsNoTracking().AnyAsync(t => t.DatabaseName == candidate, ct)
                        || await _central.PendingRegistrations.AsNoTracking().AnyAsync(p => p.DatabaseName == candidate, ct);
            if (!taken)
                return candidate;
        }

        return null;
    }

    private static string NormalizeBillingPeriod(string billingPeriod) =>
        string.Equals(billingPeriod, "Yearly", StringComparison.OrdinalIgnoreCase) ? "Yearly" : "Monthly";

    private static BusinessPhoneType ParseBusinessPhoneType(string value) =>
        string.Equals(value, nameof(BusinessPhoneType.Landline), StringComparison.OrdinalIgnoreCase)
            ? BusinessPhoneType.Landline
            : BusinessPhoneType.Mobile;

    private static string NormalizeBusinessPhone(string value) => value.Trim();
}
