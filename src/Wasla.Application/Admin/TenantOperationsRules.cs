using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Domain.Enums;

namespace Wasla.Application.Admin;

/// <summary>
/// Reads the free-text <c>Tenant.LastMigrationResult</c> that the CLI and provisioning write ("Success" or
/// "Failed: …"). Only the outcome is exposed; the failure text is a raw exception message and is never shown.
/// </summary>
public static class TenantMigrationRecords
{
    public const string SuccessValue = "Success";
    public const string FailedPrefix = "Failed";

    public static TenantMigrationRecord Classify(string? lastMigrationResult)
    {
        if (string.IsNullOrWhiteSpace(lastMigrationResult))
            return TenantMigrationRecord.NotRecorded;

        if (lastMigrationResult.StartsWith(FailedPrefix, StringComparison.Ordinal))
            return TenantMigrationRecord.Failed;

        return string.Equals(lastMigrationResult, SuccessValue, StringComparison.Ordinal)
            ? TenantMigrationRecord.Succeeded
            : TenantMigrationRecord.Unknown;
    }
}

/// <summary>Compares the migrations a tenant database has applied with the migrations this build ships.</summary>
public static class TenantMigrationStatuses
{
    public static TenantMigrationStatus Compare(IEnumerable<string> applied, IEnumerable<string> expected)
    {
        var appliedList = applied.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var expectedList = expected.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var appliedSet = appliedList.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expectedList.ToHashSet(StringComparer.Ordinal);

        var pending = expectedList.Count(id => !appliedSet.Contains(id));
        var unknownToBuild = appliedList.Any(id => !expectedSet.Contains(id));

        var state = expectedList.Count == 0
            ? TenantMigrationState.Unknown
            : unknownToBuild
                ? TenantMigrationState.DatabaseAhead
                : pending > 0 ? TenantMigrationState.Pending : TenantMigrationState.Current;

        return new TenantMigrationStatus(
            state,
            appliedList.Count,
            expectedList.Count,
            pending,
            appliedList.LastOrDefault(),
            expectedList.LastOrDefault());
    }
}

public static class ProviderConnectionHealthRules
{
    /// <summary>
    /// Admin presentation threshold: an active connection without a successful sync for this long is shown as stale.
    /// The Worker cycle is 15 seconds and the default connection interval 30 seconds, so a healthy connection is far
    /// below it; it is not a Worker setting.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public static ProviderConnectionHealthState Classify(
        bool isActive,
        bool tenantOrderSyncEnabled,
        DateTime? lastSuccessfulSyncUtc,
        int consecutiveFailures,
        DateTime? circuitOpenUntilUtc,
        DateTime nowUtc)
    {
        if (!isActive)
            return ProviderConnectionHealthState.Disabled;
        if (!tenantOrderSyncEnabled)
            return ProviderConnectionHealthState.SyncOff;
        if (circuitOpenUntilUtc is { } openUntil && openUntil > nowUtc)
            return ProviderConnectionHealthState.CircuitOpen;
        if (consecutiveFailures > 0)
            return ProviderConnectionHealthState.Failing;
        if (lastSuccessfulSyncUtc is null)
            return ProviderConnectionHealthState.NeverSynced;

        return nowUtc - lastSuccessfulSyncUtc.Value > StaleAfter
            ? ProviderConnectionHealthState.Stale
            : ProviderConnectionHealthState.Healthy;
    }
}

public enum TenantOperationsGuidanceSeverity
{
    Critical = 0,
    Warning = 1,
    Info = 2
}

/// <summary>Observations an operator should look into. Each maps to a localized, non-destructive next step.</summary>
public enum TenantOperationsGuidanceCode
{
    TenantInactive,
    DatabaseNotConfigured,
    DatabaseUnreachable,
    DatabaseTimedOut,
    DatabaseConfigurationUnreadable,
    DatabaseReadFailed,
    MigrationsPending,
    DatabaseAheadOfApplication,
    LastMigrationFailed,
    HealthReadIncomplete,
    TenantInSetup,
    NoActiveOwner,
    NoPlatformConnections,
    AllConnectionsDisabled,
    OrderSyncDisabled,
    ConnectionCircuitOpen,
    ConnectionFailing,
    ConnectionStale,
    ConnectionNeverSynced,
    NoRecentOrders,
    NoPrintBridgeDevice,
    AutoReceiptWithoutDevice,
    PrintBridgeOffline,
    PrintJobsQueuedWithoutOnlineDevice,
    PrintJobsFailed,
    RegistrationNotProvisioned,
    AllClear
}

public sealed record TenantOperationsGuidance(
    TenantOperationsGuidanceCode Code,
    TenantOperationsGuidanceSeverity Severity);

/// <summary>
/// Turns what the detail page observed into concise next steps. It only reads its inputs; it never repairs anything.
/// </summary>
public static class TenantOperationsGuidanceBuilder
{
    public static IReadOnlyList<TenantOperationsGuidance> Build(
        TenantOperationsDetail detail,
        TenantOperationalHealth health)
    {
        var items = new List<TenantOperationsGuidance>();
        void Add(TenantOperationsGuidanceCode code, TenantOperationsGuidanceSeverity severity) =>
            items.Add(new TenantOperationsGuidance(code, severity));

        if (!detail.IsActive)
            Add(TenantOperationsGuidanceCode.TenantInactive, TenantOperationsGuidanceSeverity.Warning);

        if (detail.MigrationRecord == TenantMigrationRecord.Failed)
            Add(TenantOperationsGuidanceCode.LastMigrationFailed, TenantOperationsGuidanceSeverity.Critical);

        if (detail.Registration is { Status: not PendingRegistrationStatus.Provisioned })
            Add(TenantOperationsGuidanceCode.RegistrationNotProvisioned, TenantOperationsGuidanceSeverity.Info);

        switch (health.Database)
        {
            case TenantDatabaseState.NotConfigured:
                Add(TenantOperationsGuidanceCode.DatabaseNotConfigured, TenantOperationsGuidanceSeverity.Critical);
                break;
            case TenantDatabaseState.Unreachable:
                Add(TenantOperationsGuidanceCode.DatabaseUnreachable, TenantOperationsGuidanceSeverity.Critical);
                break;
            case TenantDatabaseState.TimedOut:
                Add(TenantOperationsGuidanceCode.DatabaseTimedOut, TenantOperationsGuidanceSeverity.Critical);
                break;
            case TenantDatabaseState.ConfigurationUnreadable:
                Add(TenantOperationsGuidanceCode.DatabaseConfigurationUnreadable, TenantOperationsGuidanceSeverity.Critical);
                break;
            case TenantDatabaseState.Failed:
                Add(TenantOperationsGuidanceCode.DatabaseReadFailed, TenantOperationsGuidanceSeverity.Critical);
                break;
        }

        if (health.IsReachable)
            AddTenantDatabaseGuidance(detail, health, Add);

        AddPrintBridgeGuidance(detail, health, Add);

        if (items.Count == 0)
            Add(TenantOperationsGuidanceCode.AllClear, TenantOperationsGuidanceSeverity.Info);

        return items
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.Code)
            .ToList();
    }

    private static void AddTenantDatabaseGuidance(
        TenantOperationsDetail detail,
        TenantOperationalHealth health,
        Action<TenantOperationsGuidanceCode, TenantOperationsGuidanceSeverity> add)
    {
        if (!health.IsComplete)
            add(TenantOperationsGuidanceCode.HealthReadIncomplete, TenantOperationsGuidanceSeverity.Warning);

        switch (health.Migrations?.State)
        {
            case TenantMigrationState.Pending:
                add(TenantOperationsGuidanceCode.MigrationsPending, TenantOperationsGuidanceSeverity.Critical);
                break;
            case TenantMigrationState.DatabaseAhead:
                add(TenantOperationsGuidanceCode.DatabaseAheadOfApplication, TenantOperationsGuidanceSeverity.Warning);
                break;
        }

        if (health.Automation is { } automation)
        {
            if (automation.Mode == TenantOperationalMode.Setup)
                add(TenantOperationsGuidanceCode.TenantInSetup, TenantOperationsGuidanceSeverity.Info);
            if (!automation.OrderSyncConfigured)
                add(TenantOperationsGuidanceCode.OrderSyncDisabled, TenantOperationsGuidanceSeverity.Warning);
        }

        if (health.Users is { ActiveOwners: 0 })
            add(TenantOperationsGuidanceCode.NoActiveOwner, TenantOperationsGuidanceSeverity.Warning);

        if (health.Connections is { } connections)
        {
            if (connections.Count == 0)
            {
                add(TenantOperationsGuidanceCode.NoPlatformConnections, TenantOperationsGuidanceSeverity.Warning);
            }
            else if (connections.All(connection => !connection.IsActive))
            {
                add(TenantOperationsGuidanceCode.AllConnectionsDisabled, TenantOperationsGuidanceSeverity.Warning);
            }

            var states = connections.Select(connection => connection.State).ToHashSet();
            if (states.Contains(ProviderConnectionHealthState.CircuitOpen))
                add(TenantOperationsGuidanceCode.ConnectionCircuitOpen, TenantOperationsGuidanceSeverity.Critical);
            if (states.Contains(ProviderConnectionHealthState.Failing))
                add(TenantOperationsGuidanceCode.ConnectionFailing, TenantOperationsGuidanceSeverity.Warning);
            if (states.Contains(ProviderConnectionHealthState.Stale) && detail.IsActive)
                add(TenantOperationsGuidanceCode.ConnectionStale, TenantOperationsGuidanceSeverity.Warning);
            if (states.Contains(ProviderConnectionHealthState.NeverSynced) && detail.IsActive)
                add(TenantOperationsGuidanceCode.ConnectionNeverSynced, TenantOperationsGuidanceSeverity.Info);

            var anySyncing = connections.Any(connection =>
                connection.State is ProviderConnectionHealthState.Healthy or ProviderConnectionHealthState.Stale);
            if (anySyncing && health.Orders is { ReceivedLast7Days: 0 })
                add(TenantOperationsGuidanceCode.NoRecentOrders, TenantOperationsGuidanceSeverity.Info);
        }
    }

    private static void AddPrintBridgeGuidance(
        TenantOperationsDetail detail,
        TenantOperationalHealth health,
        Action<TenantOperationsGuidanceCode, TenantOperationsGuidanceSeverity> add)
    {
        var activeDevices = detail.PrintBridgeDevices.Where(device => device.IsActive).ToList();
        var anyOnline = activeDevices.Any(device => device.ConnectionStatus == PrintBridgeConnectionStatus.Connected);
        var autoReceiptOn = health.Automation?.AutoReceipt == AutomationState.Active;

        if (activeDevices.Count == 0)
        {
            add(
                autoReceiptOn
                    ? TenantOperationsGuidanceCode.AutoReceiptWithoutDevice
                    : TenantOperationsGuidanceCode.NoPrintBridgeDevice,
                autoReceiptOn ? TenantOperationsGuidanceSeverity.Warning : TenantOperationsGuidanceSeverity.Info);
        }
        else if (!anyOnline && activeDevices.Any(device => device.ConnectionStatus != PrintBridgeConnectionStatus.NeverConnected))
        {
            add(TenantOperationsGuidanceCode.PrintBridgeOffline, TenantOperationsGuidanceSeverity.Warning);
        }

        if (health.PrintJobs is { } jobs)
        {
            if (jobs.Queued > 0 && !anyOnline)
                add(TenantOperationsGuidanceCode.PrintJobsQueuedWithoutOnlineDevice, TenantOperationsGuidanceSeverity.Warning);
            if (jobs.FailedLast24Hours > 0)
                add(TenantOperationsGuidanceCode.PrintJobsFailed, TenantOperationsGuidanceSeverity.Warning);
        }
    }
}
