using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Admin;
using Wasla.Domain.Enums;
using Wasla.Web.Areas.Admin.Models.TenantOperations;

namespace Wasla.UnitTests.Admin;

public sealed class TenantOperationsRulesTests
{
    private static readonly string[] Plans = ["Starter", "Pro", "ProPlus", "Enterprise"];
    private static readonly DateTime Now = TenantSeed.Now;

    // Tenant list query allowlist -----------------------------------------------------------

    [Fact]
    public void ListQuery_AcceptsOnlyAllowlistedSortTokens_AndFallsBackForAnythingElse()
    {
        Assert.Equal(TenantListSortField.Name, Create(sort: "name").Sort);
        Assert.Equal(TenantListSortField.UpdatedAt, Create(sort: "UPDATED").Sort);

        foreach (var hostile in new[] { "EncryptedConnectionString", "Name desc; DROP TABLE Tenants", "1", "Tenant.Name", "", " " })
        {
            var query = Create(sort: hostile);
            Assert.Equal(TenantListSortField.CreatedAt, query.Sort);
            Assert.Equal(TenantListSortDirection.Descending, query.Direction);
        }
    }

    [Fact]
    public void ListQuery_RejectsNumericEnumValues_ForFilters()
    {
        var query = Create(status: "2", migration: "2");

        Assert.Equal(TenantListStatusFilter.All, query.Status);
        Assert.Equal(TenantMigrationRecordFilter.All, query.Migration);
        Assert.False(query.HasFilters);
    }

    [Fact]
    public void ListQuery_UsesNaturalDirectionPerColumn_UnlessGiven()
    {
        Assert.Equal(TenantListSortDirection.Ascending, Create(sort: "name").Direction);
        Assert.Equal(TenantListSortDirection.Ascending, Create(sort: "slug").Direction);
        Assert.Equal(TenantListSortDirection.Descending, Create(sort: "created").Direction);
        Assert.Equal(TenantListSortDirection.Descending, Create(sort: "name", dir: "desc").Direction);
        Assert.Equal(TenantListSortDirection.Ascending, Create(sort: "name", dir: "sideways").Direction);
    }

    [Fact]
    public void ListQuery_BoundsPageAndPageSize()
    {
        Assert.Equal(TenantListQuery.DefaultPageSize, Create(pageSize: 5000).PageSize);
        Assert.Equal(TenantListQuery.DefaultPageSize, Create(pageSize: -1).PageSize);
        Assert.Equal(50, Create(pageSize: 50).PageSize);
        Assert.Equal(1, Create(page: 0).Page);
        Assert.Equal(1, Create(page: -9).Page);
        Assert.Equal(7, Create(page: 7).Page);
    }

    [Fact]
    public void ListQuery_NormalizesSearch_AndPlan()
    {
        Assert.Null(Create(search: "   ").Search);
        Assert.Equal("kebap", Create(search: "  kebap\u0000\t ").Search);
        Assert.Equal(TenantListQuery.MaxSearchLength, Create(search: new string('a', 500)).Search!.Length);

        Assert.Equal("Pro", Create(plan: "pro").Plan);
        Assert.Equal(TenantListQuery.NoPlan, Create(plan: "NONE").Plan);
        Assert.Null(Create(plan: "Platinum").Plan);
        Assert.True(Create(plan: "pro").HasFilters);
    }

    // Migration records and comparison --------------------------------------------------------

    [Theory]
    [InlineData(null, TenantMigrationRecord.NotRecorded)]
    [InlineData("  ", TenantMigrationRecord.NotRecorded)]
    [InlineData("Success", TenantMigrationRecord.Succeeded)]
    [InlineData("Failed: timeout", TenantMigrationRecord.Failed)]
    [InlineData("Something else", TenantMigrationRecord.Unknown)]
    public void MigrationRecord_IsClassifiedWithoutExposingText(string? value, TenantMigrationRecord expected) =>
        Assert.Equal(expected, TenantMigrationRecords.Classify(value));

    [Fact]
    public void CliAndProvisioning_WriteTheMigrationResultValuesTheAdminClassifies()
    {
        var cli = File.ReadAllText(RepoFile("src", "Wasla.Cli", "CliCommands.cs"));
        var provisioning = File.ReadAllText(RepoFile("src", "Wasla.Infrastructure", "Services", "PendingRegistrationProvisioningService.cs"));

        Assert.Contains("\"Failed: \" + SanitizeMigrationError(ex)", cli, StringComparison.Ordinal);
        Assert.Contains("LastMigrationResult = \"Success\"", cli.Replace("tracked.", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("LastMigrationResult = \"Success\"", provisioning, StringComparison.Ordinal);
        Assert.StartsWith(TenantMigrationRecords.FailedPrefix, "Failed: x", StringComparison.Ordinal);
        Assert.Equal("Success", TenantMigrationRecords.SuccessValue);
    }

    [Fact]
    public void MigrationComparison_DistinguishesCurrentPendingAheadAndUnknown()
    {
        string[] expected = ["20250101_A", "20250201_B", "20250301_C"];

        var current = TenantMigrationStatuses.Compare(expected, expected);
        Assert.Equal(TenantMigrationState.Current, current.State);
        Assert.Equal("20250301_C", current.LatestAppliedMigration);

        var behind = TenantMigrationStatuses.Compare(expected[..1], expected);
        Assert.Equal(TenantMigrationState.Pending, behind.State);
        Assert.Equal(2, behind.PendingCount);
        Assert.Equal("20250101_A", behind.LatestAppliedMigration);
        Assert.Equal("20250301_C", behind.LatestExpectedMigration);

        var ahead = TenantMigrationStatuses.Compare([.. expected, "20251001_FromNewerBuild"], expected);
        Assert.Equal(TenantMigrationState.DatabaseAhead, ahead.State);

        Assert.Equal(TenantMigrationState.Unknown, TenantMigrationStatuses.Compare(expected, []).State);
    }

    // Provider connection health -------------------------------------------------------------

    [Fact]
    public void ProviderHealth_StaleBoundaryIsTenMinutes()
    {
        Assert.Equal(ProviderConnectionHealthState.Healthy, Classify(lastSuccess: Now - ProviderConnectionHealthRules.StaleAfter));
        Assert.Equal(ProviderConnectionHealthState.Stale, Classify(lastSuccess: Now - ProviderConnectionHealthRules.StaleAfter - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ProviderHealth_PrefersTheMostSpecificCause()
    {
        Assert.Equal(ProviderConnectionHealthState.Disabled, Classify(isActive: false, circuitOpenUntil: Now.AddMinutes(1)));
        Assert.Equal(ProviderConnectionHealthState.SyncOff, Classify(syncEnabled: false, failures: 3));
        Assert.Equal(ProviderConnectionHealthState.CircuitOpen, Classify(failures: 5, circuitOpenUntil: Now.AddMinutes(4)));
        Assert.Equal(ProviderConnectionHealthState.Failing, Classify(failures: 5, circuitOpenUntil: Now.AddSeconds(-1)));
        Assert.Equal(ProviderConnectionHealthState.NeverSynced, Classify(lastSuccess: null));
    }

    // Print Bridge presence (canonical thresholds) ----------------------------------------------

    [Theory]
    [InlineData(0, "Admin.Ops.PrintBridge.Online")]
    [InlineData(60, "Admin.Ops.PrintBridge.Online")]
    [InlineData(61, "Admin.Ops.PrintBridge.Stale")]
    [InlineData(300, "Admin.Ops.PrintBridge.Stale")]
    [InlineData(301, "Admin.Ops.PrintBridge.Offline")]
    public void PrintBridgePresence_UsesTheCanonicalBoundaries(int ageSeconds, string expectedLabel)
    {
        var status = PrintBridgeConnectionStatusCalculator.Calculate(true, Now.AddSeconds(-ageSeconds), Now);

        var badge = AdminOperationsPresenter.PrintBridge(status);

        Assert.Equal(expectedLabel, badge.LabelKey);
        Assert.False(string.IsNullOrWhiteSpace(badge.Icon));
    }

    [Fact]
    public void PrintBridgePresence_NeverConnectedAndDisabled_AreNotOffline()
    {
        Assert.Equal("Admin.Ops.PrintBridge.NeverConnected", AdminOperationsPresenter.PrintBridge(PrintBridgeConnectionStatusCalculator.Calculate(true, null, Now)).LabelKey);
        Assert.Equal("Admin.Ops.PrintBridge.Disabled", AdminOperationsPresenter.PrintBridge(PrintBridgeConnectionStatusCalculator.Calculate(false, Now, Now)).LabelKey);
    }

    // Guidance ---------------------------------------------------------------------------------

    [Fact]
    public void Guidance_ForAHealthyLiveTenant_IsAllClear()
    {
        var guidance = TenantOperationsGuidanceBuilder.Build(Detail(devices: [Device(PrintBridgeConnectionStatus.Connected)]), Health());

        var item = Assert.Single(guidance);
        Assert.Equal(TenantOperationsGuidanceCode.AllClear, item.Code);
    }

    [Fact]
    public void Guidance_ForAnUnreachableDatabase_IsCriticalAndSkipsTenantDatabaseRules()
    {
        var health = TenantOperationalHealth.Unavailable(TenantDatabaseState.Unreachable, TenantProviderMode.Mock, Now);

        var guidance = TenantOperationsGuidanceBuilder.Build(Detail(devices: [Device(PrintBridgeConnectionStatus.Connected)]), health);

        var item = Assert.Single(guidance);
        Assert.Equal(TenantOperationsGuidanceCode.DatabaseUnreachable, item.Code);
        Assert.Equal(TenantOperationsGuidanceSeverity.Critical, item.Severity);
    }

    [Fact]
    public void Guidance_OrdersBySeverity_AndExplainsSetupPendingMigrationsAndOfflinePrinting()
    {
        var health = Health(
            migrations: new TenantMigrationStatus(TenantMigrationState.Pending, 1, 2, 1, "A", "B"),
            automation: new TenantAutomationStatus(TenantOperationalMode.Setup, true, true, true),
            printJobs: new TenantPrintJobSummary(Queued: 2, FailedLast24Hours: 0));

        var guidance = TenantOperationsGuidanceBuilder.Build(Detail(devices: [Device(PrintBridgeConnectionStatus.Disconnected)]), health);
        var codes = guidance.Select(g => g.Code).ToList();

        Assert.Equal(TenantOperationsGuidanceCode.MigrationsPending, codes[0]);
        Assert.Contains(TenantOperationsGuidanceCode.TenantInSetup, codes);
        Assert.Contains(TenantOperationsGuidanceCode.PrintBridgeOffline, codes);
        Assert.Contains(TenantOperationsGuidanceCode.PrintJobsQueuedWithoutOnlineDevice, codes);
        Assert.DoesNotContain(TenantOperationsGuidanceCode.AllClear, codes);
        Assert.Equal(guidance.OrderBy(g => g.Severity).Select(g => g.Severity), guidance.Select(g => g.Severity));
    }

    [Fact]
    public void Guidance_AutoReceiptWithoutAnyDevice_IsAWarning()
    {
        var guidance = TenantOperationsGuidanceBuilder.Build(Detail(devices: []), Health());

        Assert.Contains(guidance, g => g.Code == TenantOperationsGuidanceCode.AutoReceiptWithoutDevice
            && g.Severity == TenantOperationsGuidanceSeverity.Warning);
    }

    [Fact]
    public void Guidance_EveryCode_HasAResourceInEveryCulture()
    {
        foreach (var code in Enum.GetValues<TenantOperationsGuidanceCode>())
            Assert.Contains($"name=\"{AdminOperationsPresenter.GuidanceKey(code)}\"", File.ReadAllText(RepoFile("src", "Wasla.Web", "Resources", "SharedResource.ar-SA.resx")), StringComparison.Ordinal);
    }

    // Helpers ------------------------------------------------------------------------------------

    private static TenantListQuery Create(
        string? search = null, string? status = null, string? migration = null, string? plan = null,
        string? sort = null, string? dir = null, int? page = null, int? pageSize = null) =>
        TenantListQuery.Create(search, status, migration, plan, sort, dir, page, pageSize, Plans);

    private static ProviderConnectionHealthState Classify(
        bool isActive = true,
        bool syncEnabled = true,
        DateTime? lastSuccess = null,
        int failures = 0,
        DateTime? circuitOpenUntil = null) =>
        ProviderConnectionHealthRules.Classify(isActive, syncEnabled, lastSuccess, failures, circuitOpenUntil, Now);

    private static TenantPrintBridgeDeviceSummary Device(PrintBridgeConnectionStatus status) =>
        new("Kitchen", true, Now, "1.0.0", status);

    private static TenantOperationsDetail Detail(IReadOnlyList<TenantPrintBridgeDeviceSummary> devices) =>
        new(Guid.NewGuid(), "Kebap", "kebap", "kebap.wasla.local", "Wasla_Tenant_kebap", true, Now, Now, true,
            TenantMigrationRecord.Succeeded, Now, null, null, devices, Now);

    private static TenantOperationalHealth Health(
        TenantMigrationStatus? migrations = null,
        TenantAutomationStatus? automation = null,
        TenantPrintJobSummary? printJobs = null) =>
        new(
            TenantDatabaseState.Reachable,
            migrations ?? new TenantMigrationStatus(TenantMigrationState.Current, 2, 2, 0, "B", "B"),
            automation ?? new TenantAutomationStatus(TenantOperationalMode.Live, true, true, true),
            new TenantGuidedSetupSummary(0, 1, 0),
            new TenantUserSummary(2, 1),
            TenantProviderMode.Mock,
            [new TenantPlatformConnectionHealth(FoodPlatform.TrendyolYemek, "S1", true, ProviderClientKind.Mock, Now, Now, null, 0, null, ProviderConnectionHealthState.Healthy)],
            new TenantOrderActivity(3, 10, 1, Now),
            printJobs ?? new TenantPrintJobSummary(0, 0),
            Now);

    internal static string RepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("Repository root was not found."), .. segments]);
    }
}
