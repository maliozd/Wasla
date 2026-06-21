using Microsoft.EntityFrameworkCore;
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
    private static readonly PendingRegistrationStatus[] BlockingStatuses =
    [
        PendingRegistrationStatus.Draft,
        PendingRegistrationStatus.AwaitingPayment,
        PendingRegistrationStatus.PaymentSucceeded
    ];

    private readonly CentralDbContext _central;
    private readonly IWaslaPlanCatalog _planCatalog;
    private readonly ISignupReferenceDataService _referenceData;
    private readonly CustomerOnboardingOptions _options;
    private readonly ILogger<PendingRegistrationService> _logger;

    public PendingRegistrationService(
        CentralDbContext central,
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData,
        IOptions<CustomerOnboardingOptions> options,
        ILogger<PendingRegistrationService> logger)
    {
        _central = central;
        _planCatalog = planCatalog;
        _referenceData = referenceData;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct)
    {
        var normalized = RegistrationNameNormalizer.NormalizeSlug(slug);
        if (!RegistrationNameNormalizer.IsValidSlugFormat(normalized))
            return false;

        var primaryDomain = RegistrationNameNormalizer.BuildPrimaryDomain(normalized, _options.MarketingBaseDomain);
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

        var primaryDomain = RegistrationNameNormalizer.BuildPrimaryDomain(slug, _options.MarketingBaseDomain);
        if (await IsSlugOrDomainTakenAsync(slug, primaryDomain, ct))
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.DuplicateSlug);
        }

        var baseDatabaseName = RegistrationNameNormalizer.GenerateDatabaseNameFromBusinessName(request.BusinessName);
        if (!RegistrationNameNormalizer.IsValidDatabaseNameFormat(baseDatabaseName))
        {
            return new PendingRegistrationResult(false, null, null, null, null, PendingRegistrationError.InvalidSlug);
        }

        var databaseName = await ResolveUniqueDatabaseNameAsync(baseDatabaseName, ct);
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

        var businessTypesDisplay = await (
            from link in _central.PendingRegistrationBusinessTypes.AsNoTracking()
            join bt in _central.BusinessTypes.AsNoTracking() on link.BusinessTypeId equals bt.Id
            where link.PendingRegistrationId == registrationId
            orderby bt.SortOrder
            select bt.DisplayName).ToListAsync(ct);

        var businessTypesLabel = businessTypesDisplay.Count > 0
            ? string.Join(", ", businessTypesDisplay)
            : row.BusinessType ?? string.Empty;

        var monthly = CheckoutSimulatedPricing.GetMonthlyPriceTry(row.PlanCode);
        var total = CheckoutSimulatedPricing.GetTotalPriceTry(row.PlanCode, row.BillingPeriod);

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
            monthly,
            total,
            CheckoutSimulatedPricing.IsYearlyBilling(row.BillingPeriod));
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
        {
            registration.Status = PendingRegistrationStatus.Expired;
            await _central.SaveChangesAsync(ct);
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyExpired, registration.Id);
        }

        var reference = GenerateSimulatedPaymentReference();
        var now = DateTime.UtcNow;
        registration.Status = PendingRegistrationStatus.PaymentSucceeded;
        registration.PaymentSucceededAtUtc = now;
        registration.PaymentFailedAtUtc = null;
        registration.SimulatedPaymentReference = reference;

        await _central.SaveChangesAsync(ct);

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
        {
            registration.Status = PendingRegistrationStatus.Expired;
            await _central.SaveChangesAsync(ct);
            return new CheckoutSimulationResult(CheckoutSimulationOutcome.AlreadyExpired, registration.Id);
        }

        registration.Status = PendingRegistrationStatus.PaymentFailed;
        registration.PaymentFailedAtUtc = DateTime.UtcNow;

        await _central.SaveChangesAsync(ct);

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

        registration.Status = PendingRegistrationStatus.Cancelled;
        await _central.SaveChangesAsync(ct);

        _logger.LogInformation("Pending registration cancelled. RegistrationId={RegistrationId}", registration.Id);

        return new CheckoutSimulationResult(CheckoutSimulationOutcome.Applied, registration.Id);
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

        var now = DateTime.UtcNow;
        return await _central.PendingRegistrations.AsNoTracking()
            .AnyAsync(p =>
                BlockingStatuses.Contains(p.Status)
                && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now)
                && (p.Slug == slug || p.PrimaryDomain == primaryDomain), ct);
    }

    private async Task<bool> IsDatabaseNameTakenAsync(string databaseName, CancellationToken ct)
    {
        var customerTaken = await _central.Tenants.AsNoTracking()
            .AnyAsync(c => c.DatabaseName == databaseName, ct);
        if (customerTaken)
            return true;

        var now = DateTime.UtcNow;
        return await _central.PendingRegistrations.AsNoTracking()
            .AnyAsync(p =>
                BlockingStatuses.Contains(p.Status)
                && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now)
                && p.DatabaseName == databaseName, ct);
    }

    public async Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct)
    {
        var normalizedHost = RegistrationNameNormalizer.NormalizeHostForComparison(primaryDomain);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return null;

        var normalizedSlug = RegistrationNameNormalizer.ExtractSlugFromHost(
            normalizedHost,
            _options.MarketingBaseDomain);
        var now = DateTime.UtcNow;

        var row = await _central.PendingRegistrations.AsNoTracking()
            .Where(p => (p.PrimaryDomain.ToLower() == normalizedHost
                         || (normalizedSlug != null && p.Slug == normalizedSlug))
                     && p.Status != PendingRegistrationStatus.PaymentFailed
                     && p.Status != PendingRegistrationStatus.Cancelled
                     && p.Status != PendingRegistrationStatus.Expired
                     && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now))
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

    private async Task<string?> ResolveUniqueDatabaseNameAsync(string baseDatabaseName, CancellationToken ct)
    {
        if (!await IsDatabaseNameTakenAsync(baseDatabaseName, ct))
            return baseDatabaseName;

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = baseDatabaseName + suffix.ToString();
            if (!RegistrationNameNormalizer.IsValidDatabaseNameFormat(candidate))
                continue;

            if (!await IsDatabaseNameTakenAsync(candidate, ct))
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
