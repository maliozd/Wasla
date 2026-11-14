using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Orders;
using Wasla.UnitTests.Realtime;

namespace Wasla.UnitTests.Persistence;

/// <summary>
/// WAS-206: SQL Server keeps a session's isolation level after the transaction that set it, and the connection pool's
/// reset (<c>sp_reset_connection</c>) does not put it back. A serializable transaction must therefore hand its pooled
/// session back at read committed, or the next request's ordinary reads on that session run serializable and hold
/// key-range locks. Each test uses its own connection pool, so the next read gets the transaction's own physical
/// session back; the tests check that through the server's <c>connection_id</c> instead of assuming it.
/// </summary>
public sealed class SerializableTransactionSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const short ReadCommitted = 2;
    private const short Serializable = 4;
    private const string TenantName = "Isolation Restaurant";
    private static readonly Guid Tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The receipt path, unchanged: its serializable transaction commits a new receipt, then rolls back when it finds that
    /// receipt still active. After each, the next ordinary read on the same pool gets that physical session at read committed.
    /// </summary>
    [Fact]
    public async Task AQueuedReceipt_HandsItsPooledSessionBackReadCommitted_AfterCommitAndAfterRollback()
    {
        var instance = "Receipts." + Guid.NewGuid().ToString("N")[..8];
        var orderId = await SeedOrderAsync();
        var receipts = new ReceiptPrintJobService(
            database.Instance(instance),
            new DefaultReceiptTemplates(),
            NullTenantChangeNotifier.Instance,
            NullLogger<ReceiptPrintJobService>.Instance);

        Assert.True(await receipts.TryCreateReceiptJobAsync(Tenant, orderId, 1, TenantName, Ct));
        await AssertNextReadReusesTheSessionAtReadCommittedAsync(instance);

        Assert.False(await receipts.TryCreateReceiptJobAsync(Tenant, orderId, 1, TenantName, Ct));
        await AssertNextReadReusesTheSessionAtReadCommittedAsync(instance);
    }

    /// <summary>
    /// Every way a serializable transaction ends: it is serializable while it runs (its range read holds key-range locks,
    /// so nothing is weakened), and the next ordinary read on a new context gets its physical session at read committed.
    /// <c>serverEnded</c> is a transaction the server rolled back itself (as for a deadlock victim); <c>cancelled</c> is a
    /// command cancelled inside it.
    /// </summary>
    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("dispose")]
    [InlineData("exception")]
    [InlineData("serverEnded")]
    [InlineData("cancelled")]
    public async Task ASerializableTransaction_KeepsItsRangeLocks_AndHandsItsSessionBackReadCommitted(string end)
    {
        await SeedOrderAsync();
        var instance = "Serializable." + end + "." + Guid.NewGuid().ToString("N")[..8];
        SessionState? during = null;
        await using (var db = database.Open(instance))
        {
            try
            {
                await using var tx = await SerializableTransaction.BeginAsync(db.Database, Ct);
                during = await SessionAsync(db);
                Assert.Equal(Serializable, during.Isolation);
                _ = await db.Orders.CountAsync(Ct);
                Assert.Contains("RangeS-S", await KeyLockModesAsync(db));

                switch (end)
                {
                    case "commit":
                        await tx.CommitAsync(Ct);
                        break;
                    case "rollback":
                        await tx.RollbackAsync(Ct);
                        break;
                    case "dispose":
                        break;
                    case "exception":
                        throw new InvalidOperationException("The test failed the transaction.");
                    case "serverEnded":
                        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT 1 / 0;", Ct));
                        Assert.Equal(0, await TranCountAsync(db));
                        break;
                    case "cancelled":
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
                        {
                            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
                            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:10'", cts.Token));
                        }
                        break;
                }
            }
            catch (InvalidOperationException e) when (e.Message == "The test failed the transaction.")
            {
            }
        }

        await AssertNextReadReusesTheSessionAtReadCommittedAsync(instance, during!.ConnectionId);
    }

    /// <summary>
    /// The level is set back right before the commit, and the transaction is still fully protected there: at EF's commit
    /// hook, after the reset and before COMMIT, the session already reports read committed while it still holds its
    /// key-range locks, and an order inserted into the range it read waits for it.
    /// </summary>
    [Fact]
    public async Task RightBeforeItCommits_ASerializableTransaction_StillHoldsItsRangeLocks_WithItsSessionAlreadyReadCommitted()
    {
        await SeedOrderAsync();
        var atCommit = new AtCommit(this);
        await using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(database.ConnectionString("AtCommit." + Guid.NewGuid().ToString("N")[..8]))
            .AddInterceptors(atCommit)
            .Options);

        await using (var tx = await SerializableTransaction.BeginAsync(db.Database, Ct))
        {
            _ = await db.Orders.CountAsync(Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.True(atCommit.Reached, "The commit hook did not run.");
        Assert.Equal(ReadCommitted, atCommit.Isolation);
        Assert.Contains("RangeS-S", atCommit.KeyLockModes);
        Assert.True(atCommit.InsertBlocked, "An order inserted into the range the transaction read did not wait for it.");
    }

    private async Task<bool> InsertIsBlockedAsync()
    {
        await using var other = database.Open("OtherWriter");
        await other.Database.OpenConnectionAsync(Ct);
        await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 500", Ct);
        other.Orders.Add(NewOrder());
        try
        {
            await other.SaveChangesAsync(Ct);
            return false;
        }
        catch (Exception e) when (LockTimeout(e))
        {
            // EF's SQL Server strategy reports the lock timeout (1222) as a transient failure around the update error.
            return true;
        }
    }

    private static bool LockTimeout(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is SqlException { Number: 1222 })
                return true;
        }
        return false;
    }

    private sealed class AtCommit(SerializableTransactionSqlServerTests owner) : DbTransactionInterceptor
    {
        public bool Reached { get; private set; }
        public short Isolation { get; private set; }
        public List<string> KeyLockModes { get; } = [];
        public bool InsertBlocked { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Reached = true;
            await using (var command = transaction.Connection!.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
                Isolation = (short)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            await using (var command = transaction.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT DISTINCT request_mode FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type = 'KEY'";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    KeyLockModes.Add(reader.GetString(0));
            }
            InsertBlocked = await owner.InsertIsBlockedAsync();
            return result;
        }
    }

    /// <summary>
    /// A session killed by the server during the transaction, so that setting the level back is the first command to find it
    /// dead. Ending without a commit raises nothing of its own; a commit fails with the server's error before committing.
    /// Either way the broken connection is not handed out again: the next read gets a new session at read committed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASerializableTransactionWhoseSessionIsKilled_IsNotReused_AndOnlyACommitReportsTheBrokenSession(bool commit)
    {
        var instance = "Killed." + commit + "." + Guid.NewGuid().ToString("N")[..8];
        SessionState? during = null;
        await using (var db = database.Open(instance))
        {
            await using var tx = await SerializableTransaction.BeginAsync(db.Database, Ct);
            during = await SessionAsync(db);
            await using (var inspector = database.Open("Inspector"))
                await inspector.Database.ExecuteSqlAsync($"DECLARE @kill nvarchar(30) = CONCAT(N'KILL ', {during.Spid}); EXEC (@kill);", Ct);
            Assert.Equal(System.Data.ConnectionState.Open, db.Database.GetDbConnection().State);

            if (commit)
                await Assert.ThrowsAsync<SqlException>(() => tx.CommitAsync(Ct));
        }

        await using var next = database.Open(instance);
        var session = await SessionAsync(next);
        Assert.NotEqual(during!.ConnectionId, session.ConnectionId);
        Assert.Equal(ReadCommitted, session.Isolation);
    }

    private async Task AssertNextReadReusesTheSessionAtReadCommittedAsync(string instance, Guid expected)
    {
        await using var next = database.Open(instance);
        var session = await SessionAsync(next);

        Assert.True(expected == session.ConnectionId, "The next read did not get the pooled session back, so nothing was checked.");
        Assert.Equal(ReadCommitted, session.Isolation);
    }

    private static async Task<List<string>> KeyLockModesAsync(TenantDbContext db) =>
        await db.Database.SqlQueryRaw<string>("""
            SELECT DISTINCT request_mode AS [Value] FROM sys.dm_tran_locks
            WHERE request_session_id = @@SPID AND resource_type = 'KEY'
            """).ToListAsync(Ct);

    private static async Task<int> TranCountAsync(TenantDbContext db) =>
        (await db.Database.SqlQueryRaw<int>("SELECT @@TRANCOUNT AS [Value]").ToListAsync(Ct)).Single();

    // The pool of this instance holds exactly one physical session (the one the service used); the next ordinary read on a
    // new context of the same pool must get it, and must run read committed.
    private async Task AssertNextReadReusesTheSessionAtReadCommittedAsync(string instance)
    {
        var pooled = await PooledConnectionsAsync(instance);
        var idle = Assert.Single(pooled);

        await using var next = database.Open(instance);
        var session = await SessionAsync(next);

        Assert.True(idle == session.ConnectionId, "The next read did not get the pooled session back, so nothing was checked.");
        Assert.Equal(ReadCommitted, session.Isolation);
    }

    internal static async Task<SessionState> SessionAsync(TenantDbContext db) =>
        (await db.Database.SqlQueryRaw<SessionState>("""
            SELECT CAST(s.session_id AS int) AS Spid, c.connection_id AS ConnectionId, s.transaction_isolation_level AS Isolation
            FROM sys.dm_exec_sessions s JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
            WHERE s.session_id = @@SPID
            """).ToListAsync(Ct)).Single();

    private async Task<List<Guid>> PooledConnectionsAsync(string instance)
    {
        var program = "Wasla.ConcurrencyTests." + instance;
        await using var inspector = database.Open("Inspector");
        return await inspector.Database.SqlQuery<Guid>($"""
            SELECT c.connection_id AS [Value]
            FROM sys.dm_exec_sessions s JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
            WHERE s.program_name = {program}
            """).ToListAsync(Ct);
    }

    private async Task<Guid> SeedOrderAsync()
    {
        await using var db = database.Open();
        var order = NewOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.Id;
    }

    private static Order NewOrder()
    {
        var orderId = Guid.NewGuid();
        var externalId = $"ISO-{orderId:N}";
        return new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalId,
            ExternalOrderCode = $"TY-{externalId[..14]}",
            IdempotencyKey = $"TrendyolYemek:{externalId}",
            InternalStatus = OrderStatus.Preparing,
            PlatformStatus = "Picking",
            CustomerName = "Isolation Customer",
            CustomerPhone = "+905555555555",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            AcceptedAt = DateTime.UtcNow.AddMinutes(-3),
            RawPayloadJson = "{}",
            ChangeVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    internal sealed record SessionState(int Spid, Guid ConnectionId, short Isolation);
}
