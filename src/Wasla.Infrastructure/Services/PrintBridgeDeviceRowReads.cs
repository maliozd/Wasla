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
/// These reads run only during setups and moves, and for Central Admin's tenant detail (<see cref="ByTenant"/>), which
/// needs one bounded page and one aggregate of a tenant's devices without reading a list of their ids. Measured on SQL
/// Server 2025 (WAS-205): about 43 pages per 1,000 device rows, 1 ms at 3,900 devices and 4.5 ms at 39,000.
/// </para>
/// <para>
/// The frequent reads (device authentication on every poll, the devices page, setup status and Central Admin's tenant
/// list) use two statements instead (WAS-205): the ids from one index alone (<see cref="IdByTokenHashAsync"/>,
/// <see cref="ManageableIdsAsync(CentralDbContext, Guid, CancellationToken)"/>), then the rows by key alone
/// (<see cref="ByIds"/>). Neither statement holds an index key while it waits for a row, so a writer can only make them
/// wait. The second statement's rows are as of after the first: callers check the token, tenant and removal again on them.
/// </para>
/// </summary>
internal static class PrintBridgeDeviceRowReads
{
    /// <summary>The id of the device with this token hash, from <c>IX_PrintBridgeDevices_TokenHash</c> alone; empty if none.</summary>
    public static Task<Guid> IdByTokenHashAsync(CentralDbContext centralDb, string tokenHash, CancellationToken ct) =>
        centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.TokenHash == tokenHash)
            .Select(d => d.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The ids of a tenant's devices that are not removed, from <c>IX_PrintBridgeDevices_TenantId_RemovedAtUtc</c> alone.</summary>
    public static Task<List<Guid>> ManageableIdsAsync(CentralDbContext centralDb, Guid tenantId, CancellationToken ct) =>
        centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.RemovedAtUtc == null)
            .Select(d => d.Id)
            .ToListAsync(ct);

    /// <summary>The ids of these tenants' devices that are not removed, from <c>IX_PrintBridgeDevices_TenantId_RemovedAtUtc</c> alone.</summary>
    public static Task<List<Guid>> ManageableIdsAsync(CentralDbContext centralDb, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct) =>
        centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => tenantIds.Contains(d.TenantId) && d.RemovedAtUtc == null)
            .Select(d => d.Id)
            .ToListAsync(ct);

    /// <summary>Devices by key alone. Filter only by key here: another predicate can bring a secondary index back.</summary>
    public static IQueryable<PrintBridgeDevice> ByIds(CentralDbContext centralDb, IReadOnlyCollection<Guid> ids) =>
        centralDb.PrintBridgeDevices.Where(d => ids.Contains(d.Id));

    public static IQueryable<PrintBridgeDevice> ByInstallation(CentralDbContext centralDb, Guid installationId) =>
        centralDb.Database.IsSqlServer()
            ? centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK, INDEX(1)) WHERE [InstallationId] = {installationId}")
            : centralDb.PrintBridgeDevices;

    /// <summary>
    /// A tenant's devices through the clustered index alone. Callers filter by tenant and removal again (on SQLite this is
    /// the whole table) and let the server cut and aggregate, so only the rows they return leave the database.
    /// </summary>
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
