using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Onboarding;
using Wasla.Application.Signup;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Signup;

public sealed class BusinessSubtypeFlowTests : IDisposable
{
    private readonly CentralDbContext _db;

    public BusinessSubtypeFlowTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite($"Data Source={Guid.NewGuid():N}-subtypes.db")
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
    public async Task Submit_AcceptsMultipleSubtypes_AndStoresThemOnPendingRegistration()
    {
        var result = await SubmitAsync(["burger", "pizza", "kebab"]);

        Assert.True(result.Success);
        var codes = await LinkCodesAsync(result.RegistrationId!.Value);
        Assert.Equal(["burger", "kebab", "pizza"], codes.OrderBy(code => code, StringComparer.Ordinal).ToArray());
        Assert.Equal(3, await _db.PendingRegistrationBusinessTypes.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provisioning_KeepsSubtypeLinks_AndDoesNotDuplicateThem()
    {
        var submitted = await SubmitAsync(["burger", "pizza"]);
        var registration = await _db.PendingRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        registration.Status = PendingRegistrationStatus.PaymentSucceeded;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = BuildProvisioningService();
        var first = await service.ProvisionAsync(registration.Id, ct: TestContext.Current.CancellationToken);
        var second = await service.ProvisionAsync(registration.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningOutcome.Success, first.Outcome);
        Assert.Equal(ProvisioningOutcome.AlreadyProvisioned, second.Outcome);
        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal(1, await _db.Tenants.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.TenantMemberships.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await _db.PendingRegistrationBusinessTypes.CountAsync(TestContext.Current.CancellationToken));

        var reader = new TenantBusinessSubtypeReader(_db);
        var codes = await reader.GetSubtypeCodesAsync(first.TenantId!.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(codes);
        Assert.Equal(["burger", "pizza"], codes!.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Reader_ReturnsNullWithoutRegistration_AndDropsLegacyCategoryCodes()
    {
        var reader = new TenantBusinessSubtypeReader(_db);
        Assert.Null(await reader.GetSubtypeCodesAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        var registration = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = PendingRegistrationStatus.Provisioned,
            TenantId = Guid.NewGuid(),
            Slug = "legacy-type",
            BusinessName = "Legacy",
            PrimaryDomain = "legacy-type.wasla.local",
            DatabaseName = "Wasla_legacy_type",
            BusinessPhone = "555",
            BusinessPhoneType = BusinessPhoneType.Mobile,
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Owner",
            OwnerEmail = "legacy@test.com",
            PasswordHash = "hash",
            PlanCode = "starter",
            BillingPeriod = "monthly",
            CreatedAtUtc = DateTime.UtcNow
        };
        registration.BusinessTypes.Add(new PendingRegistrationBusinessType
        {
            PendingRegistrationId = registration.Id,
            BusinessTypeId = 1
        });
        registration.BusinessTypes.Add(new PendingRegistrationBusinessType
        {
            PendingRegistrationId = registration.Id,
            BusinessTypeId = 9
        });
        _db.PendingRegistrations.Add(registration);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var codes = await reader.GetSubtypeCodesAsync(registration.TenantId!.Value, TestContext.Current.CancellationToken);
        Assert.Equal(["burger"], codes);
    }

    [Fact]
    public async Task Validator_RejectsEmptyAndLegacyCategorySelections()
    {
        var validator = new PendingRegistrationRequestValidator(new SelfServicePlanCatalog(), new CatalogReferenceData());

        var empty = await validator.ValidateAsync(Request([]), TestContext.Current.CancellationToken);
        Assert.Contains(empty.Errors, error => error.ErrorMessage == "Validation.BusinessSubtypeRequired");

        var legacy = await validator.ValidateAsync(Request(["restaurant", "fast-food"]), TestContext.Current.CancellationToken);
        Assert.Contains(legacy.Errors, error => error.ErrorMessage == "Validation.BusinessTypeInvalid");

        var accepted = await validator.ValidateAsync(Request(["burger", "pizza"]), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(accepted.Errors, error =>
            error.ErrorMessage is "Validation.BusinessSubtypeRequired" or "Validation.BusinessTypeInvalid");
    }

    private async Task<PendingRegistrationResult> SubmitAsync(IReadOnlyList<string> codes)
    {
        var service = new PendingRegistrationService(
            _db,
            new SelfServicePlanCatalog(),
            new SignupReferenceDataService(_db),
            Options.Create(new CustomerOnboardingOptions()),
            NullLogger<PendingRegistrationService>.Instance);

        return await service.SubmitAsync(Request(codes), TestContext.Current.CancellationToken);
    }

    private static PendingRegistrationRequest Request(IReadOnlyList<string> codes) =>
        new(
            "starter",
            "Monthly",
            "Burger House",
            codes,
            "Mobile",
            "5551112233",
            null,
            "burger-house",
            "Germany",
            null,
            null,
            null,
            null,
            "Berlin",
            "Mitte",
            null,
            "Main Street 1",
            null,
            null,
            null,
            null,
            null,
            null,
            "Owner Name",
            "owner@example.com",
            null,
            "password1");

    private async Task<IReadOnlyList<string>> LinkCodesAsync(Guid registrationId)
    {
        return await (
            from link in _db.PendingRegistrationBusinessTypes.AsNoTracking()
            join businessType in _db.BusinessTypes.AsNoTracking() on link.BusinessTypeId equals businessType.Id
            where link.PendingRegistrationId == registrationId
            select businessType.Code)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private PendingRegistrationProvisioningService BuildProvisioningService()
    {
        return new PendingRegistrationProvisioningService(
            _db,
            new FakeSecretManager(),
            Options.Create(new CustomerOnboardingOptions
            {
                ServerInstance = "(localdb)\\MSSQLLocalDB",
                SqlAuth = "trusted",
                TrialDays = 14
            }),
            new ConfigurationBuilder().Build(),
            new FakeServiceProvider(),
            new FakeTenantDatabaseProvisioningOperations(),
            NullLogger<PendingRegistrationProvisioningService>.Instance);
    }

    private sealed class SelfServicePlanCatalog : IWaslaPlanCatalog
    {
        private static readonly WaslaPlanDefinition Starter = new(
            "starter", "plan", "plan", 100m, "price", [], false, false, false, 1, "cta", "devices");

        public IReadOnlyList<WaslaPlanDefinition> GetPublicPlans() => [Starter];
        public string? NormalizePlanCode(string? planCode) => planCode;
        public WaslaPlanDefinition? FindByCode(string? planCode) =>
            string.Equals(planCode, "starter", StringComparison.OrdinalIgnoreCase) ? Starter : null;
        public bool IsSelfServicePlan(string? planCode) => FindByCode(planCode) is not null;
    }

    private sealed class CatalogReferenceData : ISignupReferenceDataService
    {
        public Task<IReadOnlyList<SignupBusinessTypeOption>> ResolveBusinessTypesByCodesAsync(
            IReadOnlyList<string> codes,
            CancellationToken ct)
        {
            IReadOnlyList<SignupBusinessTypeOption> resolved = codes
                .Where(BusinessSubtypeCatalog.IsSubtypeCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(code => new SignupBusinessTypeOption(code, code))
                .ToArray();
            return Task.FromResult(resolved);
        }

        public Task<IReadOnlyList<SignupBusinessTypeOption>> GetActiveBusinessTypesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignupBusinessTypeOption>>([]);
        public Task<IReadOnlyList<SignupCityOption>> GetActiveCitiesAsync(string countryCode, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignupCityOption>>([]);
        public Task<IReadOnlyList<SignupDistrictOption>> GetDistrictsByCityIdAsync(int cityId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignupDistrictOption>>([]);
        public Task<IReadOnlyList<SignupNeighborhoodOption>> GetNeighborhoodsByDistrictIdAsync(int districtId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignupNeighborhoodOption>>([]);
        public Task<IReadOnlyList<SignupStreetOption>> GetStreetsByNeighborhoodIdAsync(int neighborhoodId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignupStreetOption>>([]);
        public Task<SignupCityOption?> GetCityByIdAsync(int cityId, CancellationToken ct) => Task.FromResult<SignupCityOption?>(null);
        public Task<SignupCityDistrictNames?> ResolveCityDistrictAsync(int cityId, int districtId, string countryCode, CancellationToken ct) =>
            Task.FromResult<SignupCityDistrictNames?>(null);
        public Task<SignupNeighborhoodNames?> ResolveNeighborhoodAsync(int districtId, int neighborhoodId, CancellationToken ct) =>
            Task.FromResult<SignupNeighborhoodNames?>(null);
        public Task<SignupStreetNames?> ResolveStreetAsync(int neighborhoodId, int streetId, CancellationToken ct) =>
            Task.FromResult<SignupStreetNames?>(null);
    }

    private sealed class FakeSecretManager : Wasla.Application.Abstractions.Security.ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult(("encrypted_base64", 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult("decrypted");
    }

    private sealed class FakeTenantDatabaseProvisioningOperations : ITenantDatabaseProvisioningOperations
    {
        public Task<TenantDatabaseProvisioningWork> ProvisionAsync(
            PendingRegistration registration,
            string server,
            string sqlAuth,
            string databaseName,
            CancellationToken ct) =>
            Task.FromResult(new TenantDatabaseProvisioningWork(
                $"Data Source={server};Initial Catalog={databaseName};Integrated Security=True",
                DateTime.UtcNow,
                OwnerCreated: true));
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
