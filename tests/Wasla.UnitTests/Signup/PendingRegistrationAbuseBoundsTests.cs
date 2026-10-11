using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The bounds <see cref="PendingRegistrationService.SubmitAsync"/> puts on anonymous signups (WAS-90): at most three
/// active registrations per owner email, refused before any lookup, hash or insert; and a database-name search with a
/// fixed number of queries, however many registrations share a business name. File-backed SQLite; every SQL command the
/// service sends is recorded. All data is fake.
/// </summary>
public sealed class PendingRegistrationAbuseBoundsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wasla-signup-bounds-{Guid.NewGuid():N}.db");
    private readonly SqlCommandCounter _commands = new();

    public PendingRegistrationAbuseBoundsTests()
    {
        using var db = NewDb(counted: false);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    // --- Per-email cap ------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmailWithThreeActiveRegistrations_IsRefusedBeforeAnyOtherWork()
    {
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, email);
        await SeedAsync(PendingRegistrationStatus.Draft, email.ToUpperInvariant());
        await SeedAsync(PendingRegistrationStatus.PaymentSucceeded, email);
        // Finished, cancelled, failed and expired registrations do not count.
        await SeedAsync(PendingRegistrationStatus.Cancelled, email);
        await SeedAsync(PendingRegistrationStatus.PaymentFailed, email);
        await SeedAsync(PendingRegistrationStatus.Expired, email);
        await SeedAsync(PendingRegistrationStatus.Provisioned, email);
        var before = await CountAsync();
        _commands.Reset();

        var result = await SubmitAsync("Burger House", email);

        Assert.False(result.Success);
        Assert.Equal(PendingRegistrationError.TooManyActiveRegistrations, result.Error);
        Assert.Null(result.RegistrationId);
        Assert.Equal(before, await CountAsync());
        // One count query; no slug, domain or database-name lookup and no insert.
        var command = Assert.Single(_commands.Commands);
        Assert.StartsWith("SELECT", command.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OwnerEmail", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmailWithFewerActiveRegistrations_CanSignUp()
    {
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, email);
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, email);
        await SeedAsync(PendingRegistrationStatus.Cancelled, email);

        var result = await SubmitAsync("Burger House", email);

        Assert.True(result.Success, result.Error.ToString());
    }

    [Fact]
    public async Task TheCap_IsConfigurable()
    {
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, email);

        var refused = await SubmitAsync("Burger House", email, maxActivePerEmail: 1);
        var accepted = await SubmitAsync("Burger House", email, maxActivePerEmail: 2);

        Assert.Equal(PendingRegistrationError.TooManyActiveRegistrations, refused.Error);
        Assert.True(accepted.Success, accepted.Error.ToString());
    }

    // --- Database-name search -----------------------------------------------------------------------

    [Fact]
    public async Task ManyRegistrationsSharingABusinessName_CostAFixedNumberOfNameQueries()
    {
        // The base name and all 50 numbered names after it are taken.
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, databaseName: "Wasla_BurgerHouse");
        for (var suffix = 2; suffix <= 51; suffix++)
            await SeedAsync(PendingRegistrationStatus.AwaitingPayment, databaseName: $"Wasla_BurgerHouse{suffix}");
        _commands.Reset();

        var result = await SubmitAsync("Burger House");

        Assert.True(result.Success, result.Error.ToString());
        Assert.Matches("^Wasla_BurgerHouse_[0-9a-f]{8}$", result.DatabaseName!);
        var nameQueries = NameQueries();
        // One query per table for the numbered names, then one pair for the random name: 4, never more than 8.
        Assert.InRange(nameQueries.Count, 4, 8);
    }

    [Fact]
    public async Task TheFirstFreeNumberedName_IsUsed_WithOneQueryPerTable()
    {
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, databaseName: "Wasla_BurgerHouse");
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, databaseName: "Wasla_BurgerHouse2");
        await SeedAsync(PendingRegistrationStatus.AwaitingPayment, databaseName: "Wasla_BurgerHouse3");
        // An expired registration does not hold its name.
        await SeedAsync(PendingRegistrationStatus.Expired, databaseName: "Wasla_BurgerHouse4");
        _commands.Reset();

        var result = await SubmitAsync("Burger House");

        Assert.True(result.Success, result.Error.ToString());
        Assert.Equal("Wasla_BurgerHouse4", result.DatabaseName);
        Assert.Equal(2, NameQueries().Count);
    }

    [Fact]
    public async Task AFreeBaseName_IsUsedAsItIs()
    {
        var result = await SubmitAsync("Burger House");

        Assert.True(result.Success, result.Error.ToString());
        Assert.Equal("Wasla_BurgerHouse", result.DatabaseName);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private async Task<PendingRegistrationResult> SubmitAsync(string businessName, string? ownerEmail = null, int maxActivePerEmail = 3)
    {
        await using var db = NewDb(counted: true);
        var service = new PendingRegistrationService(
            db,
            new WaslaPlanCatalog(),
            new SignupReferenceDataService(db),
            Options.Create(new CustomerOnboardingOptions { MaxActivePendingRegistrationsPerOwnerEmail = maxActivePerEmail }),
            NullLogger<PendingRegistrationService>.Instance);

        var token = SentinelApplicant.NewToken();
        return await service.SubmitAsync(
            new PendingRegistrationRequest(
                PlanCode: "starter",
                BillingPeriod: "Monthly",
                BusinessName: businessName,
                BusinessTypeCodes: ["burger"],
                BusinessPhoneType: "Mobile",
                BusinessPhone: "5551112233",
                BusinessEmail: null,
                Slug: $"s{token}",
                Country: "Germany",
                CityId: null,
                DistrictId: null,
                NeighborhoodId: null,
                StreetId: null,
                City: "Berlin",
                District: "Mitte",
                Neighborhood: null,
                StreetAddress: "Main Street 1",
                BuildingNumber: null,
                Floor: null,
                DoorNumber: null,
                AddressNote: null,
                PostalCode: null,
                LocationUrl: null,
                OwnerFullName: "Owner Name",
                OwnerEmail: ownerEmail ?? $"owner.{token}@sentinel.test",
                OwnerPhone: null,
                Password: "Fake-password-1"),
            TestContext.Current.CancellationToken);
    }

    private async Task SeedAsync(PendingRegistrationStatus status, string? ownerEmail = null, string? databaseName = null)
    {
        var applicant = new SentinelApplicant(status);
        if (ownerEmail is not null)
            applicant.Entity.OwnerEmail = ownerEmail;
        if (databaseName is not null)
            applicant.Entity.DatabaseName = databaseName;
        await using var db = NewDb(counted: false);
        db.PendingRegistrations.Add(applicant.Entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync()
    {
        await using var db = NewDb(counted: false);
        return await db.PendingRegistrations.CountAsync(TestContext.Current.CancellationToken);
    }

    // Lookups of database names (not the insert, which also names the column).
    private List<string> NameQueries() =>
        _commands.Commands
            .Where(c => c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        && c.Contains("\"DatabaseName\"", StringComparison.Ordinal)
                        && !c.Contains("\"Slug\"", StringComparison.Ordinal))
            .ToList();

    private CentralDbContext NewDb(bool counted)
    {
        var builder = new DbContextOptionsBuilder<CentralDbContext>().UseSqlite($"Data Source={_path};Pooling=False");
        if (counted)
            builder.AddInterceptors(_commands);
        return new CentralDbContext(builder.Options);
    }
}
