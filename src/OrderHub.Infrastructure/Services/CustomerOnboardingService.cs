using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Application;
using OrderHub.Application.Abstractions.Onboarding;
using OrderHub.Application.Abstractions.Onboarding.Signup;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Domain.Entities.Central;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Options;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class CustomerOnboardingService : ICustomerOnboardingService
{
    private static readonly Regex SlugRegex = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly Regex SqlDbNameRegex = new(@"^Wasla_[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly CentralDbContext _central;
    private readonly ISecretManager _secretManager;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly CustomerOnboardingOptions _options;
    private readonly ILogger<CustomerOnboardingService> _logger;

    public CustomerOnboardingService(
        CentralDbContext central,
        ISecretManager secretManager,
        IOrderHubPlanCatalog planCatalog,
        IOptions<CustomerOnboardingOptions> options,
        ILogger<CustomerOnboardingService> logger)
    {
        _central = central;
        _secretManager = secretManager;
        _planCatalog = planCatalog;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct)
    {
        var normalized = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalized) || !SlugRegex.IsMatch(normalized))
            return false;

        var domain = BuildPrimaryDomain(normalized);
        if (await _central.Customers.AsNoTracking()
                .AnyAsync(c => c.Slug == normalized || c.PrimaryDomain == domain, ct))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        return !await _central.PendingRegistrations.AsNoTracking()
            .AnyAsync(p =>
                (p.Status == Domain.Enums.PendingRegistrationStatus.Draft
                 || p.Status == Domain.Enums.PendingRegistrationStatus.AwaitingPayment
                 || p.Status == Domain.Enums.PendingRegistrationStatus.PaymentSucceeded)
                && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now)
                && (p.Slug == normalized || p.PrimaryDomain == domain), ct);
    }

    public async Task<CustomerSignupResult> RegisterAsync(CustomerSignupRequest request, CancellationToken ct)
    {
        var slug = NormalizeSlug(request.Slug);
        if (!SlugRegex.IsMatch(slug))
        {
            return new CustomerSignupResult(false, null, null, null, null, CustomerSignupError.InvalidSlug);
        }

        var plan = _planCatalog.FindByCode(request.PlanCode);
        if (plan is null || plan.IsContactSales)
        {
            return new CustomerSignupResult(false, null, null, null, null, CustomerSignupError.InvalidPlan);
        }

        var primaryDomain = BuildPrimaryDomain(slug);
        var pascal = SlugToPascalCase(slug);
        var dbName = $"Wasla_{pascal}";
        if (!SqlDbNameRegex.IsMatch(dbName))
        {
            return new CustomerSignupResult(false, null, null, null, null, CustomerSignupError.InvalidSlug);
        }

        var exists = await _central.Customers.AsNoTracking()
            .AnyAsync(c => c.Slug == slug || c.PrimaryDomain == primaryDomain, ct);
        if (exists)
        {
            return new CustomerSignupResult(false, null, null, null, null, CustomerSignupError.DuplicateSlug);
        }

        var dbCreated = false;
        Customer? insertedCustomer = null;

        try
        {
            await EnsureDatabaseExistsAsync(dbName, ct);
            dbCreated = true;

            var customerConnString = BuildCustomerConnectionString(dbName);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(customerConnString)
                .Options;

            var migrationNow = DateTime.UtcNow;
            string migrationResult;
            try
            {
                await using var db = new TenantDbContext(options);
                await db.Database.MigrateAsync(ct);
                migrationResult = "Success";
            }
            catch (Exception ex)
            {
                migrationResult = "Failed: " + ex.Message;
                throw;
            }

            var (encrypted, keyVersion) = await _secretManager.EncryptAsync(customerConnString, ct);

            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var customer = new Customer
            {
                Id = customerId,
                Name = request.BusinessName.Trim(),
                Slug = slug,
                PrimaryDomain = primaryDomain,
                DatabaseName = dbName,
                EncryptedConnectionString = encrypted,
                EncryptionKeyVersion = keyVersion,
                SchemaVersion = TenantDbSchemaVersions.Current,
                LastMigrationAt = migrationNow,
                LastMigrationResult = migrationResult,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            var trialEnds = now.AddDays(Math.Max(1, _options.TrialDays));
            var membership = new CustomerMembership
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                PlanCode = plan.PlanCode,
                Status = MembershipStatus.Trial,
                BillingPeriod = NormalizeBillingPeriod(request.BillingPeriod),
                StartedAt = now,
                TrialEndsAt = trialEnds,
                CurrentPeriodEndsAt = trialEnds,
                OwnerEmail = request.OwnerEmail.Trim(),
                BusinessPhone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
                City = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim(),
                Country = string.IsNullOrWhiteSpace(request.Country) ? null : request.Country.Trim(),
                BusinessType = string.IsNullOrWhiteSpace(request.BusinessType) ? null : request.BusinessType.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };

            _central.Customers.Add(customer);
            _central.CustomerMemberships.Add(membership);
            await _central.SaveChangesAsync(ct);
            insertedCustomer = customer;

            Guid userId;
            await using (var userDb = new TenantDbContext(options))
            {
                var appUser = new AppUser
                {
                    Email = request.OwnerEmail.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                    FullName = request.OwnerFullName.Trim(),
                    Role = UserRole.Owner,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                userDb.AppUsers.Add(appUser);
                await userDb.SaveChangesAsync(ct);
                userId = appUser.Id;
            }

            _logger.LogInformation(
                "Customer signup completed. CustomerId={CustomerId} Slug={Slug} Plan={Plan}",
                customerId,
                slug,
                plan.PlanCode);

            return new CustomerSignupResult(true, customerId, userId, primaryDomain, slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Customer signup failed for slug {Slug}", slug);
            if (dbCreated)
            {
                _logger.LogWarning(
                    "Partial signup failure for slug {Slug}. Database {Database} may need manual cleanup.",
                    slug,
                    dbName);
            }

            if (insertedCustomer is not null)
            {
                return new CustomerSignupResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    CustomerSignupError.DatabaseProvisioningFailed,
                    "Signup.PartialFailure");
            }

            return new CustomerSignupResult(
                false,
                null,
                null,
                null,
                null,
                CustomerSignupError.DatabaseProvisioningFailed,
                ex.Message);
        }
    }

    private string BuildPrimaryDomain(string slug) =>
        $"{slug}.{_options.MarketingBaseDomain.Trim().TrimStart('.')}";

    private static string NormalizeSlug(string slug) =>
        slug.Trim().ToLowerInvariant();

    private static string NormalizeBillingPeriod(string billingPeriod) =>
        string.Equals(billingPeriod, "Yearly", StringComparison.OrdinalIgnoreCase) ? "Yearly" : "Monthly";

    private static string SlugToPascalCase(string slug)
    {
        return string.Join("", slug.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                if (part.Length == 0) return string.Empty;
                return char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..].ToLowerInvariant() : string.Empty);
            }));
    }

    private string BuildMasterConnectionString()
    {
        if (string.Equals(_options.SqlAuth, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = _options.ServerInstance,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        if (_options.SqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = _options.SqlAuth[4..];
            var idx = rest.IndexOf(':');
            if (idx <= 0)
                throw new InvalidOperationException("Invalid SqlAuth format. Use 'sql:username:password'.");
            var user = rest[..idx];
            var pass = rest[(idx + 1)..];
            return new SqlConnectionStringBuilder
            {
                DataSource = _options.ServerInstance,
                InitialCatalog = "master",
                UserID = user,
                Password = pass,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        throw new InvalidOperationException("Invalid SqlAuth. Use 'trusted' or 'sql:username:password'.");
    }

    private string BuildCustomerConnectionString(string database)
    {
        if (string.Equals(_options.SqlAuth, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = _options.ServerInstance,
                InitialCatalog = database,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        if (_options.SqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = _options.SqlAuth[4..];
            var idx = rest.IndexOf(':');
            if (idx <= 0)
                throw new InvalidOperationException("Invalid SqlAuth format. Use 'sql:username:password'.");
            var user = rest[..idx];
            var pass = rest[(idx + 1)..];
            return new SqlConnectionStringBuilder
            {
                DataSource = _options.ServerInstance,
                InitialCatalog = database,
                UserID = user,
                Password = pass,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        throw new InvalidOperationException("Invalid SqlAuth. Use 'trusted' or 'sql:username:password'.");
    }

    private async Task EnsureDatabaseExistsAsync(string dbName, CancellationToken ct)
    {
        if (!SqlDbNameRegex.IsMatch(dbName))
            throw new InvalidOperationException("Invalid database name after validation.");

        var masterCs = BuildMasterConnectionString();
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct);

        const string checkSql = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        await using (var check = new SqlCommand(checkSql, conn))
        {
            check.Parameters.AddWithValue("@name", dbName);
            var count = Convert.ToInt32(await check.ExecuteScalarAsync(ct)!);
            if (count > 0)
                return;
        }

        var escaped = dbName.Replace("]", "]]", StringComparison.Ordinal);
        var createSql = $"CREATE DATABASE [{escaped}]";
        await using var create = new SqlCommand(createSql, conn) { CommandTimeout = 120 };
        await create.ExecuteNonQueryAsync(ct);
    }
}
