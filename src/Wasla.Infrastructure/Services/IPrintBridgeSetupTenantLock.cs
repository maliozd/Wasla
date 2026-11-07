using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Transaction-owned SQL Server application locks that serialize Print Bridge setup writes. Callers that need both take
/// the installation lock first and then the tenant lock, so no two setup operations wait for each other in reverse
/// order. Each method returns the <c>sp_getapplock</c> result: negative when the lock was not granted.
/// </summary>
public interface IPrintBridgeSetupTenantLock
{
    /// <summary>Serializes device creation, quota checks and session endings within one tenant.</summary>
    Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Serializes every registration change of one desktop installation across all tenants (WAS-91), so two setup
    /// operations can never both bind it.
    /// </summary>
    Task<int> AcquireInstallationLockAsync(Guid installationId, CancellationToken ct);
}

public sealed class SqlServerPrintBridgeSetupTenantLock : IPrintBridgeSetupTenantLock
{
    private const int LockTimeoutMilliseconds = 10_000;

    private readonly CentralDbContext _centralDb;

    public SqlServerPrintBridgeSetupTenantLock(CentralDbContext centralDb)
    {
        _centralDb = centralDb;
    }

    public Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct) =>
        AcquireAsync($"Wasla:PrintBridgeSetup:NewDevice:{tenantId:N}", ct);

    public Task<int> AcquireInstallationLockAsync(Guid installationId, CancellationToken ct) =>
        AcquireAsync($"Wasla:PrintBridgeSetup:Installation:{installationId:N}", ct);

    private async Task<int> AcquireAsync(string resource, CancellationToken ct)
    {
        if (!_centralDb.Database.IsSqlServer())
            return 0;

        var connection = _centralDb.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = _centralDb.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";

        AddParameter(command, "@Resource", resource);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Transaction");
        AddParameter(command, "@LockTimeout", LockTimeoutMilliseconds);

        var returnValue = command.CreateParameter();
        returnValue.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(returnValue);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return returnValue.Value is int value ? value : -999;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
