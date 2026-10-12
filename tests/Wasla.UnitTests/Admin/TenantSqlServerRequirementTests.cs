using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// WAS-93: tenant databases are created only on an explicitly configured SQL Server. Without
/// <c>--sql-server</c>, <c>CustomerDb:ServerInstance</c> or <c>OrderHub:CustomerOnboarding:ServerInstance</c>,
/// provisioning and <c>add-customer</c> stop before any database work; nothing falls back to LocalDB or '.'.
/// All databases are in-memory SQLite or fakes.
/// </summary>
public sealed class TenantSqlServerRequirementTests : IDisposable
{
    private const string LocalDb = "(localdb)\\MSSQLLocalDB";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CentralDbContext _db;

    public TenantSqlServerRequirementTests()
    {
        _connection.Open();
        _db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Resolution ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", "\t", " ")]
    public void Resolve_WithNothingConfigured_HasNoServer(string? overrideServer, string? customerDb, string? onboarding)
    {
        var resolution = TenantSqlServerConfiguration.Resolve(overrideServer, customerDb, onboarding);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Server);
        Assert.Equal(TenantSqlServerConfiguration.MissingMessage, resolution.Error);
    }

    [Theory]
    [InlineData(" sql-override ", "sql-customerdb", "sql-onboarding", "sql-override")]
    [InlineData(null, " sql-customerdb ", "sql-onboarding", "sql-customerdb")]
    [InlineData("  ", "", "sql-onboarding", "sql-onboarding")]
    [InlineData(null, LocalDb, null, LocalDb)]
    [InlineData(".", null, null, ".")]
    public void Resolve_UsesTheFirstExplicitValue(string? overrideServer, string? customerDb, string? onboarding, string expected)
    {
        var resolution = TenantSqlServerConfiguration.Resolve(overrideServer, customerDb, onboarding);

        Assert.True(resolution.IsResolved);
        Assert.Equal(expected, resolution.Server);
    }

    [Theory]
    [InlineData("sql;Database=master")]
    [InlineData("Server=sql")]
    [InlineData("sql\"x")]
    [InlineData("sql\nx")]
    public void Resolve_AnInvalidExplicitValue_StopsInsteadOfFallingThrough(string invalid)
    {
        var resolution = TenantSqlServerConfiguration.Resolve(invalid, "sql-customerdb", "sql-onboarding");

        Assert.False(resolution.IsResolved);
        Assert.NotNull(resolution.Error);
    }

    // --- Provisioning service -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProvisionAsync_WithoutAConfiguredServer_StopsBeforeAnyDatabaseWork(string? configured)
    {
        var operations = new RecordingOperations();
        var reg = await SeedPaidRegistrationAsync("noserver");
        var service = BuildService(operations, optionsServer: configured, customerDbServer: configured);

        var result = await service.ProvisionAsync(reg.Id, ct: Ct);

        Assert.Equal(ProvisioningOutcome.ValidationError, result.Outcome);
        Assert.Equal(TenantSqlServerConfiguration.MissingMessage, result.Message);
        Assert.Empty(operations.Servers);
        Assert.Equal(0, await _db.Tenants.CountAsync(Ct));
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded,
            (await _db.PendingRegistrations.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task ProvisionAsync_UsesTheOverride_OverConfiguration()
    {
        var operations = new RecordingOperations();
        var reg = await SeedPaidRegistrationAsync("override");
        var service = BuildService(operations, optionsServer: "sql-onboarding", customerDbServer: "sql-customerdb");

        var result = await service.ProvisionAsync(reg.Id, sqlServerOverride: "sql-override", ct: Ct);

        Assert.Equal(ProvisioningOutcome.Success, result.Outcome);
        Assert.Equal("sql-override", Assert.Single(operations.Servers));
    }

    [Fact]
    public async Task ProvisionAsync_UsesCustomerDbServerInstance_ThenTheOnboardingServer()
    {
        var first = new RecordingOperations();
        var reg = await SeedPaidRegistrationAsync("customerdb");
        var result = await BuildService(first, optionsServer: "sql-onboarding", customerDbServer: "sql-customerdb")
            .ProvisionAsync(reg.Id, ct: Ct);
        Assert.Equal(ProvisioningOutcome.Success, result.Outcome);
        Assert.Equal("sql-customerdb", Assert.Single(first.Servers));

        var second = new RecordingOperations();
        var other = await SeedPaidRegistrationAsync("onboarding");
        result = await BuildService(second, optionsServer: LocalDb, customerDbServer: null).ProvisionAsync(other.Id, ct: Ct);
        Assert.Equal(ProvisioningOutcome.Success, result.Outcome);
        Assert.Equal(LocalDb, Assert.Single(second.Servers));
    }

    [Fact]
    public async Task ProvisionAsync_AnInvalidOverride_StopsBeforeAnyDatabaseWork()
    {
        var operations = new RecordingOperations();
        var reg = await SeedPaidRegistrationAsync("badoverride");
        var service = BuildService(operations, optionsServer: "sql-onboarding", customerDbServer: null);

        var result = await service.ProvisionAsync(reg.Id, sqlServerOverride: "sql;Database=master", ct: Ct);

        Assert.Equal(ProvisioningOutcome.ValidationError, result.Outcome);
        Assert.Empty(operations.Servers);
    }

    [Fact]
    public void CustomerOnboardingOptions_HasNoBuiltInServer()
    {
        Assert.Equal(string.Empty, new CustomerOnboardingOptions().ServerInstance);
    }

    // --- CLI ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ProvisionSignupRequest_DryRun_WithoutAServer_Fails(string? configured)
    {
        using var host = BuildCliHost(configured);
        var reg = await SeedPaidRegistrationAsync(host, "clidry");

        var exitCode = await QuietAsync(() => CliCommands.ProvisionSignupRequestAsync(
            host, reg.Id, dryRun: true, force: false, sqlServer: null, sqlAuth: "trusted", ct: Ct));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task ProvisionSignupRequest_DryRun_WithAnOverride_Succeeds()
    {
        using var host = BuildCliHost(configuredServer: null);
        var reg = await SeedPaidRegistrationAsync(host, "clidryoverride");

        var exitCode = await QuietAsync(() => CliCommands.ProvisionSignupRequestAsync(
            host, reg.Id, dryRun: true, force: false, sqlServer: LocalDb, sqlAuth: "trusted", ct: Ct));

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddCustomer_WithoutAServer_StopsBeforeAnyDatabaseWork(string? configured)
    {
        using var host = BuildCliHost(configured);

        var exitCode = await QuietAsync(() => CliCommands.AddCustomerAsync(
            host,
            name: "No Server",
            slug: "no-server",
            domain: "no-server.wasla.local",
            adminEmail: "owner@no-server.test",
            adminPassword: "Fake-password-1",
            adminName: "Owner",
            sqlServer: null,
            sqlAuth: "trusted",
            contact: new TenantContactUpdate("+90 212 000 00 00", "Istanbul", null),
            ct: Ct));

        Assert.Equal(2, exitCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        Assert.Equal(0, await db.Tenants.CountAsync(Ct));
        Assert.Equal(0, await db.TenantMemberships.CountAsync(Ct));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private PendingRegistrationProvisioningService BuildService(
        RecordingOperations operations,
        string? optionsServer,
        string? customerDbServer)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TenantSqlServerConfiguration.CustomerDbKey] = customerDbServer })
            .Build();
        return new PendingRegistrationProvisioningService(
            _db,
            new FakeSecretManager(),
            Options.Create(new CustomerOnboardingOptions { ServerInstance = optionsServer ?? string.Empty, SqlAuth = "trusted" }),
            config,
            new NullServiceProvider(),
            operations,
            NullLogger<PendingRegistrationProvisioningService>.Instance);
    }

    private IHost BuildCliHost(string? configuredServer) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:CentralDb"] = "Server=fixture;Database=Wasla_Central;Trusted_Connection=True",
                        [TenantSqlServerConfiguration.CustomerDbKey] = configuredServer,
                        [TenantSqlServerConfiguration.OnboardingKey] = configuredServer
                    })
                    .Build());
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_connection));
            })
            .Build();

    private Task<PendingRegistration> SeedPaidRegistrationAsync(string slug) => SeedAsync(_db, slug);

    private static async Task<PendingRegistration> SeedPaidRegistrationAsync(IHost host, string slug)
    {
        using var scope = host.Services.CreateScope();
        return await SeedAsync(scope.ServiceProvider.GetRequiredService<CentralDbContext>(), slug);
    }

    private static async Task<PendingRegistration> SeedAsync(CentralDbContext db, string slug)
    {
        var reg = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = PendingRegistrationStatus.PaymentSucceeded,
            Slug = slug,
            BusinessName = "Business " + slug,
            PrimaryDomain = slug + ".wasla.local",
            DatabaseName = "Wasla_" + slug,
            BusinessPhone = "+905551112233",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Test Owner",
            OwnerEmail = slug + "@test.example",
            PasswordHash = "fake-hash",
            PlanCode = "starter",
            BillingPeriod = "Monthly",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.PendingRegistrations.Add(reg);
        await db.SaveChangesAsync(Ct);
        return reg;
    }

    private static async Task<int> QuietAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            return await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class RecordingOperations : ITenantDatabaseProvisioningOperations
    {
        public List<string> Servers { get; } = new();

        public Task<TenantDatabaseProvisioningWork> ProvisionAsync(
            PendingRegistration registration,
            string server,
            string sqlAuth,
            string databaseName,
            CancellationToken ct)
        {
            Servers.Add(server);
            return Task.FromResult(new TenantDatabaseProvisioningWork(
                $"Data Source={server};Initial Catalog={databaseName};Integrated Security=True",
                DateTime.UtcNow,
                OwnerCreated: true));
        }
    }

    private sealed class FakeSecretManager : Wasla.Application.Abstractions.Security.ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult(("fake-encrypted", 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult(string.Empty);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
