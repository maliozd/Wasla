using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Onboarding;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Onboarding;
using OrderHub.Domain.Entities.Central;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Options;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.Services;

public sealed class PendingRegistrationService : IPendingRegistrationService
{
    private static readonly PendingRegistrationStatus[] BlockingStatuses =
    [
        PendingRegistrationStatus.Draft,
        PendingRegistrationStatus.AwaitingPayment,
        PendingRegistrationStatus.PaymentSucceeded
    ];

    private readonly CentralDbContext _central;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly CustomerOnboardingOptions _options;
    private readonly ILogger<PendingRegistrationService> _logger;

    public PendingRegistrationService(
        CentralDbContext central,
        IOrderHubPlanCatalog planCatalog,
        IOptions<CustomerOnboardingOptions> options,
        ILogger<PendingRegistrationService> logger)
    {
        _central = central;
        _planCatalog = planCatalog;
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

    public async Task<bool> IsDatabaseNameAvailableAsync(string databaseName, CancellationToken ct)
    {
        if (!RegistrationNameNormalizer.IsValidDatabaseNameFormat(databaseName))
            return false;

        return !await IsDatabaseNameTakenAsync(databaseName, ct);
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

        var now = DateTime.UtcNow;
        var expiryDays = Math.Max(1, _options.PendingRegistrationExpiryDays);
        var registration = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            PlanCode = plan.PlanCode,
            BillingPeriod = NormalizeBillingPeriod(request.BillingPeriod),
            BusinessName = request.BusinessName.Trim(),
            BusinessType = request.BusinessType.Trim(),
            Slug = slug,
            PrimaryDomain = primaryDomain,
            DatabaseName = databaseName,
            BusinessPhone = request.BusinessPhone.Trim(),
            Country = request.Country.Trim(),
            City = request.City.Trim(),
            District = request.District.Trim(),
            Neighborhood = string.IsNullOrWhiteSpace(request.Neighborhood) ? null : request.Neighborhood.Trim(),
            AddressLine1 = request.AddressLine1.Trim(),
            AddressLine2 = string.IsNullOrWhiteSpace(request.AddressLine2) ? null : request.AddressLine2.Trim(),
            PostalCode = string.IsNullOrWhiteSpace(request.PostalCode) ? null : request.PostalCode.Trim(),
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
                p.Status))
            .FirstOrDefaultAsync(ct);

        return row;
    }

    public async Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid registrationId, CancellationToken ct)
    {
        var row = await _central.PendingRegistrations.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == registrationId, ct);

        if (row is null)
            return null;

        var monthly = CheckoutSimulatedPricing.GetMonthlyPriceTry(row.PlanCode);
        var total = CheckoutSimulatedPricing.GetTotalPriceTry(row.PlanCode, row.BillingPeriod);

        return new PendingRegistrationCheckoutDetails(
            row.Id,
            row.PlanCode,
            row.BillingPeriod,
            row.BusinessName,
            row.PrimaryDomain,
            row.BusinessPhone,
            row.OwnerFullName,
            row.OwnerEmail,
            row.OwnerPhone,
            row.Country,
            row.City,
            row.District,
            row.Neighborhood,
            row.AddressLine1,
            row.AddressLine2,
            row.PostalCode,
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
        var customerTaken = await _central.Customers.AsNoTracking()
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
        var customerTaken = await _central.Customers.AsNoTracking()
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
}
