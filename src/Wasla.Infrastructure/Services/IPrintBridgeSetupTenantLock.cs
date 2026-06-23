using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public interface IPrintBridgeSetupTenantLock
{
    Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct);
}

public sealed class SqlServerPrintBridgeSetupTenantLock : IPrintBridgeSetupTenantLock
{
    private const int LockTimeoutMilliseconds = 10_000;

    private readonly CentralDbContext _centralDb;

    public SqlServerPrintBridgeSetupTenantLock(CentralDbContext centralDb)
    {
        _centralDb = centralDb;
    }

    public async Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct)
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

        AddParameter(command, "@Resource", $"Wasla:PrintBridgeSetup:NewDevice:{tenantId:N}");
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
