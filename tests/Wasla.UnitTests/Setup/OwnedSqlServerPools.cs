using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// The SQL Server connection pools of one throwaway test database: one per connection string its fixture hands out.
/// <see cref="Clear"/> discards the pooled connections of those pools only, so the fixture can drop its database, and
/// leaves the pools of test classes running in parallel alone. <see cref="OwnedSqlitePools"/> does the same for SQLite.
/// </summary>
/// <remarks>
/// Do not use <see cref="SqlConnection.ClearAllPools"/> in test cleanup. It clears every pool in the process: a session
/// another test class has just returned to its pool is closed, and one that class is using is not pooled again when it
/// is closed. A test that checks which physical session its pool hands back then gets a new one (WAS-229).
/// </remarks>
internal sealed class OwnedSqlServerPools
{
    private readonly ConcurrentDictionary<string, byte> _connectionStrings = new(StringComparer.Ordinal);

    /// <summary>Records the pool of <paramref name="connectionString"/> as owned and returns the string unchanged.</summary>
    public string Own(string connectionString)
    {
        _connectionStrings.TryAdd(connectionString, 0);
        return connectionString;
    }

    /// <summary>
    /// Clears the owned pools. Pools are keyed by the exact connection string, so this reaches no other pool. A
    /// connection that is still open is closed for good when its owner closes it.
    /// </summary>
    public void Clear()
    {
        foreach (var connectionString in _connectionStrings.Keys)
        {
            using var connection = new SqlConnection(connectionString);
            SqlConnection.ClearPool(connection);
        }
    }
}
