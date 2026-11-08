using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sms;
using Wasla.UnitTests.Orders;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// A throwaway CentralDb on a real SQL Server for the registration draft tests (WAS-192), migrated with this build's
/// central migrations. The server is <c>WASLA_TEST_SQLSERVER</c>, otherwise <c>(localdb)\MSSQLLocalDB</c>. Only a database
/// with the generated <c>Wasla_SignupTest_</c> prefix is ever dropped. The draft and verification services are the real
/// ones, with the fake SMS sender, one in-memory Data Protection key ring and a test clock shared by every caller.
/// </summary>
public sealed class SqlServerSignupDatabase : IAsyncLifetime
{
    public const string Password = "Fake-pass-192";

    private const string NamePrefix = "Wasla_SignupTest_";

    private readonly string _server = SqlServerTenantDatabase.ResolveServer();

    public string DatabaseName { get; } = NamePrefix + Guid.NewGuid().ToString("N")[..12];

    public SignupTestClock Clock { get; } = new(DateTime.UtcNow);

    public FakeSmsOutbox Outbox { get; } = new();

    public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    /// <summary>Each distinct <paramref name="instance"/> has its own connection pool, as separate Web processes would.</summary>
    public CentralDbContext Open(string instance = "Inspector", params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlServer(ConnectionString(DatabaseName, instance))
            .AddInterceptors(interceptors)
            .Options);

    public RegistrationDraftService Drafts(CentralDbContext db, SignupOptions? options = null) => new(
        db,
        new WaslaPlanCatalog(),
        new SignupReferenceDataService(db),
        new PendingRegistrationService(
            db,
            new WaslaPlanCatalog(),
            new SignupReferenceDataService(db),
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<PendingRegistrationService>.Instance),
        new FakeSmsSender(Outbox, Clock),
        Options.Create(options ?? new SignupOptions()),
        Clock,
        NullLogger<RegistrationDraftService>.Instance);

    public RegistrationPhoneVerificationService Phone(CentralDbContext db, SignupOptions? options = null) => new(
        db,
        new FakeSmsSender(Outbox, Clock),
        new SmsMessageTemplates(),
        DataProtection,
        Options.Create(options ?? new SignupOptions()),
        Clock,
        NullLogger<RegistrationPhoneVerificationService>.Instance);

    public static string NewMobileE164() => SignupDraftClient.NewMobileE164();

    /// <summary>A draft with every section saved for <paramref name="mobileE164"/>; returns its id.</summary>
    public async Task<Guid> SavedDraftAsync(string mobileE164, string? slug = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Open("Setup");
        var drafts = Drafts(db);
        var plan = await drafts.SavePlanAsync(null, new RegistrationPlanInput("Starter", "Monthly"), ct);
        Assert.True(plan.Succeeded, plan.ErrorCode);
        var id = plan.Draft!.Id;
        var token = SentinelApplicant.NewToken();
        var restaurant = await drafts.SaveRestaurantAsync(id, new RegistrationRestaurantInput(
            $"Bizname{token}", ["burger"], "Landline", "212 555 12 34", null, slug ?? $"s{token}".ToLowerInvariant(), "Germany",
            null, null, null, null, "Berlin", "Mitte", null, $"Streetq{token}", null, null, null, null, null, null), ct);
        Assert.True(restaurant.Succeeded, restaurant.ErrorCode);
        var owner = await drafts.SaveOwnerAsync(id, new RegistrationOwnerInput
        {
            FullName = $"Ownerq{token}",
            Email = $"owner.{token}@sentinel.test",
            MobilePhone = mobileE164,
            Password = Password,
            ConfirmPassword = Password
        }, ct);
        Assert.True(owner.Succeeded, owner.ErrorCode);
        return id;
    }

    /// <summary>Sends a code for the draft and returns it from the fake outbox.</summary>
    public async Task<string> SendAsync(Guid draftId, string mobileE164)
    {
        await using var db = Open("Setup");
        var sent = await Phone(db).SendCodeAsync(draftId, CultureInfo.GetCultureInfo("tr-TR"), TestContext.Current.CancellationToken);
        Assert.True(sent.Succeeded, sent.ErrorCode);
        return Outbox.LatestCodeTo(mobileE164)!;
    }

    public async ValueTask InitializeAsync()
    {
        await using (var master = new SqlConnection(ConnectionString("master", "Setup")))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}];";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = Open("Setup");
        await db.Database.MigrateAsync();
        // The business subtype catalog, once, before callers race.
        await new SignupReferenceDataService(db).GetActiveBusinessTypesAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        if (!DatabaseName.StartsWith(NamePrefix, StringComparison.Ordinal))
            return;

        await using var master = new SqlConnection(ConnectionString("master", "Setup"));
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private string ConnectionString(string database, string instance) => new SqlConnectionStringBuilder
    {
        DataSource = _server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ApplicationName = "Wasla.SignupTests." + instance,
        MaxPoolSize = 32,
        ConnectTimeout = 60
    }.ConnectionString;
}
