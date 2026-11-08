using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Serializes verification sends across every Web process (WAS-192): a transaction-owned SQL Server application lock in
/// CentralDb, taken before the draft, number and global send counts are read and released when the transaction that
/// records the send ends. Simultaneous sends therefore cannot all pass a limit that only one of them may use.
/// </summary>
/// <remarks>SQLite backs single-process unit tests only and runs without the lock.</remarks>
internal static class RegistrationPhoneVerificationSendLock
{
    public const string Resource = "Wasla.RegistrationPhoneVerification.Send";

    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>True when the lock is held (or not needed); false when it was not granted within <paramref name="timeout"/>.</summary>
    public static async Task<bool> TryAcquireAsync(CentralDbContext central, TimeSpan timeout, CancellationToken ct)
    {
        if (!central.Database.IsSqlServer())
        {
            if (string.Equals(central.Database.ProviderName, SqliteProvider, StringComparison.Ordinal))
                return true;
            throw new InvalidOperationException("Phone verification sends require CentralDb on SQL Server.");
        }

        var transaction = central.Database.CurrentTransaction
                          ?? throw new InvalidOperationException("The send lock is owned by a transaction; begin one first.");

        await using var command = central.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            "DECLARE @result int; " +
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
            "@LockOwner = N'Transaction', @LockTimeout = @timeout; " +
            "SELECT @result;";

        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.DbType = DbType.String;
        resource.Size = 255;
        resource.Value = Resource;
        command.Parameters.Add(resource);

        var timeoutParameter = command.CreateParameter();
        timeoutParameter.ParameterName = "@timeout";
        timeoutParameter.DbType = DbType.Int32;
        timeoutParameter.Value = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        command.Parameters.Add(timeoutParameter);

        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return result >= 0;
    }
}
