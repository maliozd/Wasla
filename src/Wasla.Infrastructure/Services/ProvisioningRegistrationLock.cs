using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// One provisioning attempt per registration at a time, across every Web and CLI process (WAS-93). Held as a SQL Server
/// application lock in CentralDb (<c>sp_getapplock</c>, session-owned) on the context's own connection, from before
/// the registration is read until provisioning returns. A second attempt does not wait: it is refused before any
/// database work, so two attempts never migrate or seed the same tenant database at once. The lock ends with the
/// session, so a crashed process cannot leave it held.
/// </summary>
internal sealed class ProvisioningRegistrationLock : IAsyncDisposable
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly CentralDbContext? _central;
    private readonly string? _resource;

    private ProvisioningRegistrationLock(CentralDbContext? central, string? resource)
    {
        _central = central;
        _resource = resource;
    }

    /// <summary>The lock, or null when another attempt holds it.</summary>
    public static async Task<ProvisioningRegistrationLock?> TryAcquireAsync(
        CentralDbContext central,
        Guid registrationId,
        CancellationToken ct)
    {
        if (!central.Database.IsSqlServer())
        {
            // SQLite only backs single-process unit tests; any other provider is not supported here.
            if (string.Equals(central.Database.ProviderName, SqliteProvider, StringComparison.Ordinal))
                return new ProvisioningRegistrationLock(null, null);
            throw new InvalidOperationException("Provisioning requires CentralDb on SQL Server.");
        }

        var resource = $"Wasla.ProvisionRegistration.{registrationId:D}";
        await central.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(
                central,
                "DECLARE @result int; " +
                "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
                "@LockOwner = N'Session', @LockTimeout = 0; " +
                "SELECT @result;",
                resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (result >= 0)
                return new ProvisioningRegistrationLock(central, resource);
        }
        catch
        {
            await central.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }

        await central.Database.CloseConnectionAsync().ConfigureAwait(false);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_central is null)
            return;

        var connection = _central.Database.GetDbConnection();
        try
        {
            await using var command = CreateCommand(
                _central,
                "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';",
                _resource!);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // A pooled connection keeps its session, and with it the lock. Discard the physical connection instead,
            // which ends the session and releases the lock. The provisioning outcome is already decided.
            if (connection is SqlConnection sqlConnection)
                SqlConnection.ClearPool(sqlConnection);
        }
        finally
        {
            await _central.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static DbCommand CreateCommand(CentralDbContext central, string sql, string resource)
    {
        var command = central.Database.GetDbConnection().CreateCommand();
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
