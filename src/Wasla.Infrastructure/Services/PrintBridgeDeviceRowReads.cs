using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The device reads of a setup, an exchange and a move (WAS-91) that can run while another request retires or changes the
/// same device rows. On SQL Server they use the clustered index only, with <c>READCOMMITTEDLOCK</c> also inside the
/// setup's serializable transactions.
/// <para>
/// A read through a secondary index keeps its shared lock on the index key while it looks up the row. A writer locks the
/// row first and then the index key when it changes an indexed column (<c>RemovedAtUtc</c> or <c>TokenHash</c>), so the
/// two waited for each other and the read lost the deadlock (WAS-203). A read of one index holds one row's lock at a time,
/// so it can only wait until the writer commits. <c>READCOMMITTEDLOCK</c> takes no key-range locks, which is what keeps
/// new-device setups of different tenants from deadlocking (WAS-190).
/// </para>
/// <para>
/// The price is a scan of the device table, which can wait for another tenant's uncommitted device row until it commits.
/// These reads run only during setups, moves and the Web panel's device create and reactivate.
/// </para>
/// </summary>
internal static class PrintBridgeDeviceRowReads
{
    public static IQueryable<PrintBridgeDevice> ByInstallation(CentralDbContext centralDb, Guid installationId) =>
        centralDb.Database.IsSqlServer()
            ? centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK, INDEX(1)) WHERE [InstallationId] = {installationId}")
            : centralDb.PrintBridgeDevices;

    public static IQueryable<PrintBridgeDevice> ByTenant(CentralDbContext centralDb, Guid tenantId) =>
        centralDb.Database.IsSqlServer()
            ? centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK, INDEX(1)) WHERE [TenantId] = {tenantId}")
            : centralDb.PrintBridgeDevices;

    public static IQueryable<PrintBridgeDevice> ByTokenHash(CentralDbContext centralDb, string tokenHash) =>
        centralDb.Database.IsSqlServer()
            ? centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK, INDEX(1)) WHERE [TokenHash] = {tokenHash}")
            : centralDb.PrintBridgeDevices;
}
