using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Migrations;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// The tenant's operational mode: tenant state stored on the settings row, Live for every existing tenant (the
/// migration fills existing rows with Live, a missing row reads as Live), Setup only for a newly provisioned tenant.
/// </summary>
public sealed class TenantOperationalModeTests : IDisposable
{
    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheMigration_AddsARequiredColumn_ThatMakesEveryExistingTenantLive()
    {
        var migration = new AddTenantOperationalMode();

        var add = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("TenantOperationalSettings", add.Table);
        Assert.Equal(nameof(TenantOperationalSettings.OperationalMode), add.Name);
        Assert.Equal(typeof(int), add.ClrType);
        Assert.False(add.IsNullable);
        Assert.Equal((int)TenantOperationalMode.Live, add.DefaultValue);
        Assert.Equal(0, (int)TenantOperationalMode.Live);
        Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
    }

    [Fact]
    public void TheTenantAndCentralModels_HaveNoChangesWithoutAMigration()
    {
        // The same comparison as `dotnet ef migrations has-pending-model-changes`; no database is opened.
        using var tenant = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        using var central = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

        Assert.False(tenant.Database.HasPendingModelChanges());
        Assert.False(central.Database.HasPendingModelChanges());
        Assert.Contains(tenant.Database.GetMigrations(), id => id.EndsWith("_AddTenantOperationalMode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExistingTenantWithoutASettingsRow_IsLive()
    {
        Assert.Equal(TenantOperationalMode.Live, (await new TenantOperationalModeService(_tenants).GetAutomationStatusAsync(_tenantA, Ct)).Mode);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenantA));
    }

    [Fact]
    public async Task ASettingsRowCreatedLaterForAnExistingTenant_IsLive()
    {
        // Any settings page can create the row for a tenant that never had one; that must not stop the restaurant.
        await new OrderSyncSettingsService(_tenants).UpdateAsync(_tenantA, enabled: true, Ct);

        await using var db = await _tenants.CreateAsync(_tenantA, Ct);
        Assert.Equal(TenantOperationalMode.Live, (await db.TenantOperationalSettings.SingleAsync(Ct)).OperationalMode);
    }

    [Fact]
    public async Task ANewlyProvisionedTenant_StartsInSetup_WithTheDefaultSettings()
    {
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = await _tenants.CreateAsync(_tenantA, Ct))
            Assert.True(await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(db, now, Ct));

        Assert.Equal(TenantOperationalMode.Setup, (await new TenantOperationalModeService(_tenants).GetAutomationStatusAsync(_tenantA, Ct)).Mode);
        await using var check = await _tenants.CreateAsync(_tenantA, Ct);
        var settings = await check.TenantOperationalSettings.SingleAsync(Ct);
        Assert.Equal(TenantOperationalModes.SettingsId, settings.Id);
        Assert.True(settings.OrderSyncEnabled);
        Assert.False(settings.AutoApproveNewOrders);
        Assert.False(settings.AutoPrintReceiptOnAutoApprove);
        Assert.Equal(1, settings.ReceiptPrintCopyCount);
        Assert.Null(settings.SetupGuidanceCompletedAtUtc);
    }

    [Fact]
    public async Task RunningProvisioningAgain_NeverMovesALiveTenantBackToSetup()
    {
        await _tenants.SeedSettingsAsync(_tenantA, TenantOperationalMode.Live, autoApprove: true);

        await using (var db = await _tenants.CreateAsync(_tenantA, Ct))
            Assert.False(await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(db, DateTime.UtcNow, Ct));

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenantA));
        await using var check = await _tenants.CreateAsync(_tenantA, Ct);
        Assert.True((await check.TenantOperationalSettings.SingleAsync(Ct)).AutoApproveNewOrders, "existing settings are kept");
    }

    /// <summary>
    /// Both provisioning paths write the tenant database through SQL Server only (they create the database), so they
    /// are checked at source level: each seeds Setup after creating the Owner, in the same tenant database.
    /// </summary>
    [Fact]
    public void BothProvisioningPaths_StartTheNewTenantInSetup()
    {
        var signup = Read("src", "Wasla.Infrastructure", "Services", "TenantDatabaseProvisioningOperations.cs");
        var cli = Read("src", "Wasla.Cli", "CliCommands.cs");

        AssertSeedsAfterOwner(signup, "tenantDb.AppUsers.Add(new AppUser", "TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(tenantDb,");
        AssertSeedsAfterOwner(cli, "userDb.AppUsers.Add(appUser);", "TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(userDb,");
    }

    [Fact]
    public async Task Activation_IsConditional_Idempotent_AndTenantScoped()
    {
        await _tenants.SeedSettingsAsync(_tenantA, TenantOperationalMode.Setup);
        await _tenants.SeedSettingsAsync(_tenantB, TenantOperationalMode.Setup);

        await using (var db = await _tenants.CreateAsync(_tenantA, Ct))
        {
            Assert.True(await TenantOperationalModes.ActivateAsync(db, DateTime.UtcNow, Ct));
            Assert.False(await TenantOperationalModes.ActivateAsync(db, DateTime.UtcNow, Ct), "a second activation changes nothing");
        }

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenantA));
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenantB)); // another tenant is never touched

        // A tenant without a row is already Live, and activation never creates or rewrites anything for it.
        var tenantC = Guid.NewGuid();
        await using var empty = await _tenants.CreateAsync(tenantC, Ct);
        Assert.False(await TenantOperationalModes.ActivateAsync(empty, DateTime.UtcNow, Ct));
        Assert.Equal(0, await empty.TenantOperationalSettings.CountAsync(Ct));
    }

    [Fact]
    public void SetupIsAssignedOnlyToTheNewTenantRow_SoNoProductionFlowCanReturnATenantToSetup()
    {
        // Provisioning and the Development tenant reset both use TenantOperationalModes.NewTenantSettings.
        var assignment = new System.Text.RegularExpressions.Regex(
            @"OperationalMode\s*(=|,)\s*(Wasla\.Domain\.Enums\.)?TenantOperationalMode\.Setup\b");
        var assigning = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => assignment.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Equal(new[] { "TenantOperationalModeService.cs" }, assigning);
        var helper = Read("src", "Wasla.Infrastructure", "Services", "TenantOperationalModeService.cs");
        Assert.Single(assignment.Matches(helper));
        Assert.Contains("OperationalMode = TenantOperationalMode.Setup,", Block(helper, "NewTenantSettings(", "EnsureNewTenantStartsInSetupAsync("), StringComparison.Ordinal);
    }

    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + 1, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, start);
        return source[from..to];
    }

    public void Dispose() => _tenants.Dispose();

    private static void AssertSeedsAfterOwner(string source, string ownerAdded, string seeding)
    {
        var owner = source.IndexOf(ownerAdded, StringComparison.Ordinal);
        var seed = source.IndexOf(seeding, StringComparison.Ordinal);
        Assert.True(owner >= 0, ownerAdded);
        Assert.True(seed > owner, seeding);
        Assert.Equal(1, Count(source, "EnsureNewTenantStartsInSetupAsync("));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0; index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
