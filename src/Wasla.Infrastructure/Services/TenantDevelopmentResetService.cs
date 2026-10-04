using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.DevelopmentTools;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// TEMPORARY Development tool (see <see cref="ITenantDevelopmentResetService"/>). Everything runs inside two
/// local transactions, one per database, opened before the first change: a failing step rolls both back, so
/// the tenant is never left half reset. The Central part touches only this tenant's Print Bridge devices and
/// setup sessions (the operational data the setup checklist reads); tenant identity, membership, subscription,
/// checkout and provisioning rows are never read or written.
/// </summary>
public sealed class TenantDevelopmentResetService : ITenantDevelopmentResetService
{
    /// <summary>Recorded on unfinished Print Bridge setup sessions so the desktop app's pairing stops.</summary>
    public const string SetupSessionFailureReason = "development_tenant_reset";

    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly CentralDbContext _central;
    private readonly IPrintBridgeSetupTenantLock _setupLock;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantDevelopmentResetService> _logger;

    public TenantDevelopmentResetService(
        ITenantDbContextFactory tenantDbs,
        CentralDbContext central,
        IPrintBridgeSetupTenantLock setupLock,
        TimeProvider time,
        ILogger<TenantDevelopmentResetService> logger)
    {
        _tenantDbs = tenantDbs;
        _central = central;
        _setupLock = setupLock;
        _time = time;
        _logger = logger;
    }

    public async Task<TenantDevelopmentResetResult> ResetAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var centralTx = await _central.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var tenantDb = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var tenantTx = await tenantDb.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var centralCommitted = false;
        try
        {
            // Central first: it may wait for the Print Bridge pairing lock, and it must not do that while
            // holding locks on the tenant's order tables (the Live Screen and Worker would block meanwhile).
            var devices = await RemovePrintBridgeAsync(tenantId, now, ct).ConfigureAwait(false);
            var tenant = await ResetTenantDatabaseAsync(tenantDb, now, ct).ConfigureAwait(false);

            // Both databases are changed but not committed. Commit Central first, then the tenant database,
            // without cancellation so a closing request cannot split them. Only a failure between the two
            // commits could leave Print Bridge reset on its own (the devices removed, the rest untouched); that is
            // reported separately, and running the reset again completes it.
            await centralTx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            centralCommitted = true;
            await tenantTx.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            var result = tenant with { Succeeded = true, PrintBridgeDevices = devices };
            _logger.LogWarning(
                "Development tenant reset completed. TenantId={TenantId}, Orders={Orders}, PrintJobs={PrintJobs}, PlatformConnections={PlatformConnections}, PrintBridgeDevices={PrintBridgeDevices}, GuidedSetupStates={GuidedSetupStates}, PracticeOrders={PracticeOrders}",
                tenantId,
                result.Orders,
                result.PrintJobs,
                result.PlatformConnections,
                result.PrintBridgeDevices,
                result.GuidedSetupStates,
                result.PracticeOrders);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!centralCommitted)
                await TryRollbackAsync(centralTx).ConfigureAwait(false);
            await TryRollbackAsync(tenantTx).ConfigureAwait(false);

            if (centralCommitted)
            {
                _logger.LogError(
                    "Development tenant reset only partly applied: Print Bridge devices were removed, the tenant database was rolled back. Run the reset again. TenantId={TenantId}, ExceptionType={ExceptionType}",
                    tenantId,
                    ex.GetType().Name);
                return TenantDevelopmentResetResult.PrintBridgeOnly;
            }

            _logger.LogError(
                "Development tenant reset failed and was rolled back. TenantId={TenantId}, ExceptionType={ExceptionType}",
                tenantId,
                ex.GetType().Name);
            return TenantDevelopmentResetResult.Failed;
        }
    }

    /// <summary>
    /// Deletes in dependency order. Every per-user table cascades from AppUsers, so users are never deleted;
    /// their per-user rows are deleted directly instead. A missing UserNotificationSettings row is exactly what
    /// provisioning leaves. The tenant's settings row is replaced by the one provisioning writes: the canonical
    /// defaults (sync on, no automatic approval or printing, one receipt copy, default template) in Setup mode, so
    /// the test tenant is not live again until someone completes or skips guided setup. This full tenant reset is the
    /// only place outside provisioning that returns a tenant to Setup.
    /// </summary>
    private static async Task<TenantDevelopmentResetResult> ResetTenantDatabaseAsync(TenantDbContext db, DateTime now, CancellationToken ct)
    {
        var printJobs = await db.PrintJobs.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.OrderItemOptions.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.OrderItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var orders = await db.Orders.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Sync history and cursors; the connection rows hold the encrypted credentials and circuit state.
        await db.SyncLogs.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.IntegrationErrors.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var connections = await db.PlatformConnections.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Onboarding and training for every user: all of them become NotStarted again.
        var practice = await db.GuidedDemoSessions.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var guided = await db.UserGuidedSetupStates.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.UserProductTourCompletions.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Back to post-provisioning defaults: no notification rows, and the new-tenant settings row (Setup).
        await db.UserNotificationSettings.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TenantOperationalSettings.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.TenantOperationalSettings.Add(TenantOperationalModes.NewTenantSettings(now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new TenantDevelopmentResetResult(
            Succeeded: false,
            Orders: orders,
            PrintJobs: printJobs,
            PlatformConnections: connections,
            GuidedSetupStates: guided,
            PracticeOrders: practice);
    }

    /// <summary>
    /// Removes the tenant's Print Bridge devices the way PrintBridgeDeviceManagementService.RemoveDeviceAsync
    /// does (removed time, inactive, a fresh random token hash so the old token stops authenticating) and fails
    /// unfinished setup sessions. Holds the same per-tenant lock as a new-device pairing, so no pairing can add a
    /// device halfway through. The per-device active-print-job check is not needed: the print jobs are deleted.
    /// </summary>
    private async Task<int> RemovePrintBridgeAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        var lockResult = await _setupLock.AcquireNewDeviceExchangeLockAsync(tenantId, ct).ConfigureAwait(false);
        if (lockResult < 0)
            throw new InvalidOperationException("The Print Bridge setup lock could not be acquired.");

        var devices = await _central.PrintBridgeDevices
            .Where(device => device.TenantId == tenantId && device.RemovedAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var device in devices)
        {
            device.RemovedAtUtc = now;
            device.IsActive = false;
            device.TokenHash = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());
            device.UpdatedAt = now;
        }

        await _central.SaveChangesAsync(ct).ConfigureAwait(false);

        await _central.PrintBridgeSetupSessions
            .Where(session => session.TenantId == tenantId && session.CompletedAtUtc == null && session.FailedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(session => session.FailedAtUtc, now)
                .SetProperty(session => session.FailureReason, SetupSessionFailureReason)
                .SetProperty(session => session.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        return devices.Count;
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already committed or the connection is gone; disposing the transaction finishes the cleanup.
        }
    }
}
