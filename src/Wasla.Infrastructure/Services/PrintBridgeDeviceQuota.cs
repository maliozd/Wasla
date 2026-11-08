using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The active-device quota of one tenant, as the setup exchange, the move (WAS-91), and the Web panel's create and
/// reactivate read it. Each holds that tenant's setup lock
/// (<see cref="IPrintBridgeSetupTenantLock.AcquireNewDeviceExchangeLockAsync"/>) from this count until it commits, so for
/// those writers the lock, not key-range locks, keeps the count true (WAS-190). The CLI's
/// <c>generate-print-bridge-token</c> adds devices without the lock or the limit (a documented gap).
/// <para>
/// On SQL Server the count reads with <c>READCOMMITTEDLOCK</c>, also inside the setup's serializable transactions. Under
/// <c>Serializable</c> it scanned the clustered index of every tenant's devices and kept range locks up to its end, so two
/// new-device setups of different tenants deadlocked on their inserts. Without key-range locks that deadlock is gone; the
/// scan can still wait for another tenant's uncommitted device row until it commits.
/// </para>
/// </summary>
internal static class PrintBridgeDeviceQuota
{
    public static Task<int> CountActiveDevicesAsync(CentralDbContext centralDb, Guid tenantId, CancellationToken ct)
    {
        var devices = centralDb.Database.IsSqlServer()
            ? centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK) WHERE [TenantId] = {tenantId}")
            : centralDb.PrintBridgeDevices;

        return devices
            .AsNoTracking()
            .CountAsync(d => d.TenantId == tenantId && d.IsActive && d.RemovedAtUtc == null, ct);
    }
}
