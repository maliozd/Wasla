using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Tests provisioning orchestration using an isolated fake for tenant database creation,
/// migration, and tenant-owner persistence.
/// </summary>
public sealed class PendingRegistrationProvisioningServiceTests : IDisposable
{
    private readonly CentralDbContext _db;

    public PendingRegistrationProvisioningServiceTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite($"Data Source={Guid.NewGuid():N}.db")
            .Options;
        _db = new CentralDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task ProvisionAsync_NotFound_ReturnsNotFound()
    {
        var service = BuildService();

        var result = await service.ProvisionAsync(Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotFound, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ProvisionAsync_UnpaidRegistration_ReturnsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "slug1");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotEligible, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ProvisionAsync_DraftRegistration_ReturnsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.Draft, "slug2");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_CancelledRegistration_ReturnsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.Cancelled, "slug3");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_ExpiredRegistration_ReturnsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.Expired, "expiredslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_AlreadyProvisioned_WithExistingTenant_ReturnsAlreadyProvisioned()
    {
        var tenantId = Guid.NewGuid();
        var tenant = SeedTenant(tenantId, "slug4");
        var reg = SeedRegistration(PendingRegistrationStatus.Provisioned, "slug4");
        reg.TenantId = tenantId;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.AlreadyProvisioned, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(tenantId, result.TenantId);
    }

    [Fact]
    public async Task ProvisionAsync_AlreadyProvisioned_MissingTenant_WithoutForce_ReturnsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.Provisioned, "slug5");
        reg.TenantId = Guid.NewGuid(); // linked but the tenant doesn't exist in DB
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, force: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_PaymentSucceeded_WithLinkedTenant_FixesStatus_ReturnsAlreadyProvisioned()
    {
        var tenantId = Guid.NewGuid();
        SeedTenant(tenantId, "slug6");
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "slug6");
        reg.TenantId = tenantId;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.AlreadyProvisioned, result.Outcome);
        Assert.True(result.IsSuccess);

        // Verify status was fixed
        var updated = await _db.PendingRegistrations.FindAsync([reg.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PendingRegistrationStatus.Provisioned, updated!.Status);
    }

    [Fact]
    public async Task ProvisionAsync_PaymentSucceeded_MissingRequiredField_ReturnsValidationError()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "slug7");
        reg.BusinessName = ""; // missing required field
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.ValidationError, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_PaymentSucceeded_SlugConflict_ReturnsValidationError()
    {
        SeedTenant(Guid.NewGuid(), "conflictslug");
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "conflictslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.ValidationError, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_PaymentSucceeded_DatabaseNameConflict_ReturnsValidationError()
    {
        SeedTenant(Guid.NewGuid(), "existingdbslug");
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "newslug");
        reg.DatabaseName = "Wasla_existingdbslug";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService();

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.ValidationError, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_PaymentSucceeded_ProvisionsSuccessfully()
    {
        var operations = new FakeTenantDatabaseProvisioningOperations();
        var secret = new FakeSecretManager();
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "successslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService(operations, secret);

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.TenantId);
        Assert.Equal("Business successslug", result.TenantName);
        Assert.Equal("successslug.wasla.local", result.TenantDomain);

        var tenant = await _db.Tenants.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(result.TenantId, tenant.Id);
        Assert.Equal("successslug", tenant.Slug);
        Assert.Equal("successslug.wasla.local", tenant.PrimaryDomain);
        Assert.Equal("Wasla_successslug", tenant.DatabaseName);
        Assert.Equal("encrypted_base64", tenant.EncryptedConnectionString);
        Assert.Equal(1, tenant.EncryptionKeyVersion);

        var updated = await _db.PendingRegistrations.FindAsync([reg.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(PendingRegistrationStatus.Provisioned, updated!.Status);
        Assert.Equal(tenant.Id, updated.TenantId);
        Assert.NotNull(updated.ProvisionedAtUtc);

        var membership = await _db.TenantMemberships.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(tenant.Id, membership.TenantId);
        Assert.Equal("starter", membership.PlanCode);
        Assert.Equal("monthly", membership.BillingPeriod);
        Assert.Equal(reg.OwnerEmail, membership.OwnerEmail);
        Assert.Equal(MembershipStatus.Trial, membership.Status);
        Assert.NotNull(membership.TrialEndsAt);
        // The restaurant setup step reads these contact fields.
        Assert.Equal("+905551112233", membership.BusinessPhone);
        Assert.Equal("Istanbul", membership.City);
        Assert.Equal("TR", membership.Country);

        Assert.Equal(1, operations.CallCount);
        Assert.Equal(1, operations.OwnerCreateAttempts);
        Assert.Equal("Wasla_successslug", operations.DatabaseNames.Single());
        Assert.Single(secret.EncryptedPlaintexts);
        Assert.Contains("Wasla_successslug", secret.EncryptedPlaintexts.Single(), StringComparison.Ordinal);

        var resultText = string.Join("|", result.Outcome, result.Message, result.TenantId, result.TenantName, result.TenantDomain);
        Assert.DoesNotContain(reg.PasswordHash, resultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", resultText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encrypted_base64", resultText, StringComparison.Ordinal);
        Assert.DoesNotContain("token", resultText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", resultText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProvisionAsync_DuplicateInvocation_ReturnsAlreadyProvisionedWithoutCreatingAgain()
    {
        var operations = new FakeTenantDatabaseProvisioningOperations();
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "dupeslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService(operations);

        var first = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);
        var second = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.Success, first.Outcome);
        Assert.Equal(ProvisioningOutcome.AlreadyProvisioned, second.Outcome);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal(1, await _db.Tenants.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.TenantMemberships.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, operations.CallCount);
        Assert.Equal(1, operations.OwnerCreateAttempts);
    }

    [Fact]
    public async Task ProvisionAsync_TenantDatabaseFailure_ReturnsSafeFailure()
    {
        var operations = new FakeTenantDatabaseProvisioningOperations
        {
            ExceptionToThrow = new InvalidOperationException("tenant db failed with password=super-secret")
        };
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "failureslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = BuildService(operations);

        var result = await service.ProvisionAsync(reg.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.Failed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Contains("tenant db failed", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(reg.PasswordHash, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _db.Tenants.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _db.TenantMemberships.CountAsync(TestContext.Current.CancellationToken));
    }

    private PendingRegistrationProvisioningService BuildService(
        FakeTenantDatabaseProvisioningOperations? operations = null,
        FakeSecretManager? secret = null)
    {
        var opts = Options.Create(new CustomerOnboardingOptions
        {
            ServerInstance = "(localdb)\\MSSQLLocalDB",
            SqlAuth = "trusted",
            TrialDays = 14
        });
        var config = new ConfigurationBuilder().Build();
        var logger = NullLogger<PendingRegistrationProvisioningService>.Instance;
        var sp = new FakeServiceProvider();
        return new PendingRegistrationProvisioningService(
            _db,
            secret ?? new FakeSecretManager(),
            opts,
            config,
            sp,
            operations ?? new FakeTenantDatabaseProvisioningOperations(),
            logger);
    }

    private PendingRegistration SeedRegistration(PendingRegistrationStatus status, string slug)
    {
        var reg = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = status,
            Slug = slug,
            BusinessName = $"Business {slug}",
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_{slug}",
            BusinessPhone = "+905551112233",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Test Owner",
            OwnerEmail = $"{slug}@test.com",
            PasswordHash = "hash",
            PlanCode = "starter",
            BillingPeriod = "monthly",
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.PendingRegistrations.Add(reg);
        return reg;
    }

    private Tenant SeedTenant(Guid id, string slug)
    {
        var tenant = new Tenant
        {
            Id = id,
            Name = $"Tenant {slug}",
            Slug = slug,
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_{slug}",
            EncryptedConnectionString = "enc",
            EncryptionKeyVersion = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        return tenant;
    }

    private sealed class FakeSecretManager : Wasla.Application.Abstractions.Security.ISecretManager
    {
        public List<string> EncryptedPlaintexts { get; } = new();

        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct)
        {
            EncryptedPlaintexts.Add(plaintext);
            return Task.FromResult(("encrypted_base64", 1));
        }

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct)
            => Task.FromResult("decrypted");
    }

    private sealed class FakeTenantDatabaseProvisioningOperations : ITenantDatabaseProvisioningOperations
    {
        public int CallCount { get; private set; }
        public int OwnerCreateAttempts { get; private set; }
        public List<string> DatabaseNames { get; } = new();
        public Exception? ExceptionToThrow { get; set; }

        public Task<TenantDatabaseProvisioningWork> ProvisionAsync(
            PendingRegistration registration,
            string server,
            string sqlAuth,
            string databaseName,
            CancellationToken ct)
        {
            CallCount++;
            DatabaseNames.Add(databaseName);

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            OwnerCreateAttempts++;
            return Task.FromResult(new TenantDatabaseProvisioningWork(
                $"Data Source={server};Initial Catalog={databaseName};Integrated Security=True",
                DateTime.UtcNow,
                OwnerCreated: true));
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
