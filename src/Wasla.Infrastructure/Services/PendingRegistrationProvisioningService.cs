using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Email;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.Central;
using TenantEntity = Wasla.Domain.Entities.Central.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Shared provisioning service. Called by both Wasla.Cli (provision-signup-request) and the
/// Central Admin web UI. Contains only reusable orchestration — no CLI argument parsing or console output.
/// </summary>
public sealed class PendingRegistrationProvisioningService : IPendingRegistrationProvisioningService
{
    private static readonly Regex SqlDbNameRegex = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private readonly CentralDbContext _central;
    private readonly ISecretManager _secret;
    private readonly CustomerOnboardingOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITenantDatabaseProvisioningOperations _tenantDatabaseOperations;
    private readonly ILogger<PendingRegistrationProvisioningService> _logger;

    public PendingRegistrationProvisioningService(
        CentralDbContext central,
        ISecretManager secret,
        IOptions<CustomerOnboardingOptions> options,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ITenantDatabaseProvisioningOperations tenantDatabaseOperations,
        ILogger<PendingRegistrationProvisioningService> logger)
    {
        _central = central;
        _secret = secret;
        _options = options.Value;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _tenantDatabaseOperations = tenantDatabaseOperations;
        _logger = logger;
    }

    public async Task<ProvisioningResult> ProvisionAsync(
        Guid registrationId,
        bool force = false,
        string? sqlServerOverride = null,
        string? sqlAuthOverride = null,
        CancellationToken ct = default)
    {
        var registration = await _central.PendingRegistrations
            .FirstOrDefaultAsync(r => r.Id == registrationId, ct)
            .ConfigureAwait(false);

        if (registration is null)
            return ProvisioningResult.NotFound();

        // Idempotent: already provisioned with valid tenant
        if (registration.Status == PendingRegistrationStatus.Provisioned)
        {
            if (registration.TenantId is { } existingId)
            {
                var existing = await _central.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == existingId, ct)
                    .ConfigureAwait(false);
                if (existing is not null)
                    return ProvisioningResult.AlreadyProvisioned(existing.Id, existing.Name, existing.PrimaryDomain);
            }

            if (!force)
                return ProvisioningResult.NotEligible(
                    "Registration is marked Provisioned but the tenant record is missing. Use --force to retry.");
        }
        else if (registration.Status == PendingRegistrationStatus.PaymentSucceeded
                 && registration.TenantId is { } linkedTenantId)
        {
            // Status is PaymentSucceeded but tenant row already exists — fix the status
            var linked = await _central.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == linkedTenantId, ct)
                .ConfigureAwait(false);
            if (linked is not null)
            {
                registration.Status = PendingRegistrationStatus.Provisioned;
                registration.ProvisionedAtUtc ??= DateTime.UtcNow;
                await _central.SaveChangesAsync(ct).ConfigureAwait(false);
                return ProvisioningResult.AlreadyProvisioned(linked.Id, linked.Name, linked.PrimaryDomain);
            }
        }
        else if (registration.Status != PendingRegistrationStatus.PaymentSucceeded)
        {
            return ProvisioningResult.NotEligible(
                $"Registration must be in PaymentSucceeded status before provisioning. Current status: {registration.Status}.");
        }

        var fieldError = ValidateRequiredFields(registration);
        if (fieldError is not null)
            return ProvisioningResult.ValidationError(fieldError);

        var dbName = registration.DatabaseName.Trim();

        var uniquenessError = await ValidateUniquenessAsync(registration, ct).ConfigureAwait(false);
        if (uniquenessError is not null)
            return ProvisioningResult.ValidationError(uniquenessError);

        var server = ResolveServer(sqlServerOverride);
        var sqlAuth = ResolveAuth(sqlAuthOverride);

        _logger.LogInformation(
            "Provisioning registration {RegistrationId} for slug {Slug}",
            registration.Id, registration.Slug);

        try
        {
            var tenantDatabase = await _tenantDatabaseOperations
                .ProvisionAsync(registration, server, sqlAuth, dbName, ct)
                .ConfigureAwait(false);

            var (encrypted, keyVersion) = await _secret.EncryptAsync(tenantDatabase.ConnectionString, ct).ConfigureAwait(false);

            var tenantId = registration.TenantId ?? Guid.NewGuid();
            var trialDays = _options.TrialDays;
            var now = DateTime.UtcNow;

            var tenant = new TenantEntity
            {
                Id = tenantId,
                Name = registration.BusinessName.Trim(),
                Slug = registration.Slug.Trim(),
                PrimaryDomain = registration.PrimaryDomain.Trim(),
                DatabaseName = dbName,
                EncryptedConnectionString = encrypted,
                EncryptionKeyVersion = keyVersion,
                SchemaVersion = TenantDbSchemaVersions.Current,
                LastMigrationAt = tenantDatabase.MigratedAtUtc,
                LastMigrationResult = "Success",
                IsActive = true,
                BillingPaymentStatus = TenantBillingPaymentStatus.Paid,
                ProvisioningStatus = ProvisioningStatus.Completed,
                SubscriptionStatus = SubscriptionStatus.Trialing,
                CreatedAt = now,
                UpdatedAt = now
            };

            _central.Tenants.Add(tenant);
            await _central.SaveChangesAsync(ct).ConfigureAwait(false);

            var membershipExists = await _central.TenantMemberships
                .AnyAsync(m => m.TenantId == tenantId, ct)
                .ConfigureAwait(false);
            if (!membershipExists)
            {
                _central.TenantMemberships.Add(new TenantMembership
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanCode = registration.PlanCode.Trim(),
                    BillingPeriod = registration.BillingPeriod.Trim(),
                    Status = MembershipStatus.Trial,
                    StartedAt = now,
                    TrialEndsAt = now.AddDays(trialDays),
                    OwnerEmail = registration.OwnerEmail.Trim(),
                    BusinessPhone = registration.BusinessPhone.Trim(),
                    City = registration.City.Trim(),
                    Country = registration.Country.Trim(),
                    BusinessType = registration.BusinessType,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await _central.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            registration.Status = PendingRegistrationStatus.Provisioned;
            registration.TenantId = tenantId;
            registration.ProvisionedAtUtc = now;
            await _central.SaveChangesAsync(ct).ConfigureAwait(false);

            await TrySendPanelReadyEmailAsync(registration, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Registration {RegistrationId} provisioned. TenantId={TenantId} Domain={Domain}",
                registration.Id, tenantId, tenant.PrimaryDomain);

            return ProvisioningResult.Success(tenantId, tenant.Name, tenant.PrimaryDomain);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or DbUpdateException)
        {
            var safeMessage = SanitizeError(ex);
            _logger.LogError(
                "Provisioning failed for registration {RegistrationId}: {ExceptionType}: {Message}",
                registrationId, ex.GetType().Name, safeMessage);
            return ProvisioningResult.Failure(safeMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Unexpected provisioning error for registration {RegistrationId}: {ExceptionType}",
                registrationId, ex.GetType().Name);
            return ProvisioningResult.Failure("An unexpected error occurred during provisioning.");
        }
    }

    private string ResolveServer(string? sqlServerOverride)
    {
        if (!string.IsNullOrWhiteSpace(sqlServerOverride))
            return sqlServerOverride.Trim();

        return _configuration["CustomerDb:ServerInstance"]?.Trim()
            ?? _options.ServerInstance;
    }

    private string ResolveAuth(string? sqlAuthOverride)
    {
        if (!string.IsNullOrWhiteSpace(sqlAuthOverride))
            return sqlAuthOverride.Trim();

        return _options.SqlAuth;
    }

    private static string? ValidateRequiredFields(PendingRegistration r)
    {
        if (string.IsNullOrWhiteSpace(r.BusinessName)) return "BusinessName is required.";
        if (string.IsNullOrWhiteSpace(r.Slug)) return "Slug is required.";
        if (string.IsNullOrWhiteSpace(r.PrimaryDomain)) return "PrimaryDomain is required.";
        if (string.IsNullOrWhiteSpace(r.DatabaseName)) return "DatabaseName is required.";
        if (!SqlDbNameRegex.IsMatch(r.DatabaseName.Trim())) return $"DatabaseName '{r.DatabaseName}' contains invalid characters.";
        if (string.IsNullOrWhiteSpace(r.OwnerEmail)) return "OwnerEmail is required.";
        if (string.IsNullOrWhiteSpace(r.OwnerFullName)) return "OwnerFullName is required.";
        if (string.IsNullOrWhiteSpace(r.PasswordHash)) return "PasswordHash is required.";
        if (string.IsNullOrWhiteSpace(r.PlanCode)) return "PlanCode is required.";
        if (string.IsNullOrWhiteSpace(r.BillingPeriod)) return "BillingPeriod is required.";
        return null;
    }

    private async Task<string?> ValidateUniquenessAsync(
        PendingRegistration registration,
        CancellationToken ct)
    {
        var slug = registration.Slug.Trim();
        var domain = registration.PrimaryDomain.Trim();
        var dbName = registration.DatabaseName.Trim();

        var bySlug = await _central.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, ct).ConfigureAwait(false);
        if (bySlug is not null && bySlug.Id != registration.TenantId)
            return $"A tenant with slug '{slug}' already exists (TenantId={bySlug.Id}).";

        var byDomain = await _central.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.PrimaryDomain == domain, ct).ConfigureAwait(false);
        if (byDomain is not null && byDomain.Id != registration.TenantId)
            return $"A tenant with primary domain '{domain}' already exists (TenantId={byDomain.Id}).";

        var byDb = await _central.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.DatabaseName == dbName, ct).ConfigureAwait(false);
        if (byDb is not null && byDb.Id != registration.TenantId)
            return $"A tenant with database name '{dbName}' already exists (TenantId={byDb.Id}).";

        var now = DateTime.UtcNow;
        var pendingConflict = await _central.PendingRegistrations.AsNoTracking()
            .AnyAsync(p =>
                p.Id != registration.Id
                && (p.Status == PendingRegistrationStatus.Draft
                    || p.Status == PendingRegistrationStatus.AwaitingPayment
                    || p.Status == PendingRegistrationStatus.PaymentSucceeded)
                && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now)
                && (p.Slug == slug || p.PrimaryDomain == domain || p.DatabaseName == dbName),
                ct).ConfigureAwait(false);
        if (pendingConflict)
            return "Another active pending registration reserves the same slug, domain, or database name.";

        return null;
    }

    private async Task TrySendPanelReadyEmailAsync(Domain.Entities.Central.PendingRegistration registration, CancellationToken ct)
    {
        try
        {
            var factory = _serviceProvider.GetService(typeof(ProvisioningEmailFactory)) as ProvisioningEmailFactory;
            var sender = _serviceProvider.GetService(typeof(IEmailSender)) as IEmailSender;
            if (factory is null || sender is null) return;

            var message = factory.BuildPanelReadyEmail(registration);
            await sender.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PanelReady email failed for registration {RegistrationId} (provisioning itself succeeded)",
                registration.Id);
        }
    }

    private static string SanitizeError(Exception ex)
    {
        var msg = (ex.Message ?? "Unknown error").Replace("\r", " ").Replace("\n", " ").Trim();
        msg = Regex.Replace(
            msg,
            @"(?i)\b(password|pwd|secret|token)\s*=\s*[^;,\s]+",
            "$1=[redacted]");
        return msg.Length <= 500 ? msg : msg[..500];
    }
}
