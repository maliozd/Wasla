using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// The SQLite connection pools one test helper owns: one per connection string it hands out. <see cref="Clear"/>
/// closes the idle connections of those pools only, so the helper can delete its files, and leaves the pools of test
/// classes running in parallel alone.
/// </summary>
/// <remarks>
/// Do not use <see cref="SqliteConnection.ClearAllPools"/> in test cleanup. It clears every pool in the process, and a
/// connection another thread is opening at that moment (active, owner not yet recorded) looks leaked to its pool, which
/// then disposes the native handle under that thread (<c>ObjectDisposedException</c> for <c>SQLitePCL.sqlite3</c>).
/// </remarks>
internal sealed class OwnedSqlitePools
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
    /// connection that is still open is closed for good when its owner closes it, so call this after every context
    /// the helper created has been disposed.
    /// </summary>
    public void Clear()
    {
        foreach (var connectionString in _connectionStrings.Keys)
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }
}
