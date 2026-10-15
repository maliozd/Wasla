using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The claim on one order's lifecycle transition (WAS-104). Held as a SQL Server application lock in the tenant database
/// (<c>sp_getapplock</c>, session-owned) on the context's own connection, from before the order's status is read until
/// the action has called the provider and written its result. It is shared by every Web and Worker process using that
/// database, so of several simultaneous actions on one order exactly one reaches the provider. A second action does not
/// wait: it is refused before reading the order. The lock ends with the session, so a crashed process cannot leave it
/// held, and no transaction or row lock stays open during the provider call.
/// </summary>
internal sealed class OrderActionLock : IAsyncDisposable
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly TenantDbContext? _db;
    private readonly string? _resource;

    private OrderActionLock(TenantDbContext? db, string? resource)
    {
        _db = db;
        _resource = resource;
    }

    /// <summary>The application lock resource of an order. Application locks are per database, so per tenant.</summary>
    internal static string ResourceFor(Guid orderId) => $"Wasla.OrderAction.{orderId:D}";

    /// <summary>The claim, or null when another action on the order holds it.</summary>
    public static async Task<OrderActionLock?> TryAcquireAsync(TenantDbContext db, Guid orderId, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
        {
            // SQLite only backs single-process unit tests; any other provider is not supported here.
            if (string.Equals(db.Database.ProviderName, SqliteProvider, StringComparison.Ordinal))
                return new OrderActionLock(null, null);
            throw new InvalidOperationException("Order lifecycle actions require the tenant database on SQL Server.");
        }

        var resource = ResourceFor(orderId);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(
                db,
                "DECLARE @result int; " +
                "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
                "@LockOwner = N'Session', @LockTimeout = 0; " +
                "SELECT @result;",
                resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (result >= 0)
                return new OrderActionLock(db, resource);
        }
        catch
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }

        await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is null)
            return;

        var connection = _db.Database.GetDbConnection();
        try
        {
            await using var command = CreateCommand(
                _db,
                "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';",
                _resource!);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // A pooled connection keeps its session, and with it the lock. Discard the physical connection instead,
            // which ends the session and releases the lock. The action's outcome is already decided.
            if (connection is SqlConnection sqlConnection)
                SqlConnection.ClearPool(sqlConnection);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static DbCommand CreateCommand(TenantDbContext db, string sql, string resource)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Size = 255;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        return command;
    }
}
