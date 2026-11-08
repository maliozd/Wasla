using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// How the Print Bridge races on a real SQL Server (WAS-91, WAS-190) start their callers. Each caller has its own
/// DbContext and connection pool, as separate requests or Web instances would, and opens its connection before anything
/// starts, so a slow connection never decides the order. Every wait is bounded; a caller that fails while getting ready
/// fails the race at once instead of leaving the others waiting.
/// </summary>
internal sealed class PrintBridgeSqlServerRace(SqlServerCentralDatabase database)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs every call at once. The start gate opens only when every caller has opened its connection and reached the
    /// gate. With <paramref name="meetAtDeviceWrite"/>, each caller then also waits right before its first Print Bridge
    /// device write until every caller still running has reached its own: all of them have taken their locks and read
    /// what they read, so their transactions overlap where a lock conflict would show.
    /// </summary>
    public async Task<T[]> RaceAsync<T>(bool meetAtDeviceWrite, params Func<CentralDbContext, Task<T>>[] calls)
    {
        var meeting = new DeviceWriteMeeting(calls.Length);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = calls.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        var tasks = calls.Select((call, index) => Task.Run(async () =>
        {
            var gate = new MeetBeforeDeviceWrite(meeting);
            try
            {
                await using var db = meetAtDeviceWrite ? database.Open($"Racer{index}", gate) : database.Open($"Racer{index}");
                try
                {
                    await db.Database.OpenConnectionAsync(Ct);
                    ready[index].TrySetResult();
                }
                catch (Exception ex)
                {
                    ready[index].TrySetException(ex);
                    throw;
                }

                await start.Task.WaitAsync(Deadline, Ct);
                return await call(db);
            }
            finally
            {
                // A caller that ends without writing a device (refused, or failed) no longer holds the others back.
                gate.Leave();
            }
        }, Ct)).ToArray();

        try
        {
            await Task.WhenAll(ready.Select(r => r.Task)).WaitAsync(Deadline, Ct);
        }
        catch
        {
            start.TrySetCanceled();
            await Task.WhenAll(tasks).WaitAsync(Deadline, Ct).ContinueWith(_ => { }, TaskScheduler.Default);
            throw;
        }

        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(Deadline, Ct);
    }

    /// <summary>
    /// Makes two writers compete for one protected section. <paramref name="first"/> runs until right before its first
    /// device write: it holds its locks and has counted the quota. Then <paramref name="second"/> starts, and the first
    /// is held until SQL Server reports the second waiting for an application lock (<c>sp_getapplock</c>) that the
    /// first's session holds. Fails if the first ends without writing or the second ends instead of waiting.
    /// </summary>
    public async Task<(T First, T Second)> ContendAsync<T>(
        Func<CentralDbContext, Task<T>> first,
        Func<CentralDbContext, Task<T>> second)
    {
        var hold = new HoldBeforeCommand(IsDeviceWrite);
        await using var firstDb = database.Open("Holder", hold);
        await using var secondDb = database.Open("Waiter");
        var firstSession = await SessionIdAsync(firstDb);
        var secondSession = await SessionIdAsync(secondDb);

        var firstTask = Task.Run(() => first(firstDb), Ct);
        if (await Task.WhenAny(hold.Reached, firstTask).WaitAsync(Deadline, Ct) != hold.Reached)
            Assert.Fail($"The first writer ended before writing a device: {await firstTask}.");

        var secondTask = Task.Run(() => second(secondDb), Ct);
        string? notWaiting;
        try
        {
            notWaiting = await WaitForLockAsync(secondSession, firstSession, secondTask, " on APPLICATION", "the first writer's tenant lock");
        }
        finally
        {
            hold.Release();
        }

        await Task.WhenAll(firstTask, secondTask).WaitAsync(Deadline, Ct).ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(firstTask.IsCompleted && secondTask.IsCompleted, "A writer did not finish in time.");
        var result = (await firstTask, await secondTask);
        Assert.True(notWaiting is null, $"{notWaiting} First: {result.Item1}; second: {result.Item2}.");
        return result;
    }

    /// <summary>
    /// Overlaps <paramref name="read"/> with a writer that changes one device row in two steps, in the lock order of a real
    /// update of an indexed column: the row first, then the index key (WAS-203). The writer locks the row, the read runs
    /// until SQL Server reports it waiting for that row, and only then does the writer change the indexed column and
    /// commit. A read that kept a lock on the index key meanwhile deadlocks with the writer here, every time: the writer's
    /// deadlock priority makes the read the victim, as it was in the captured deadlocks, and <paramref name="read"/>
    /// throws. <paramref name="readFrom"/> starts the writer at that command of the read instead of before the read.
    /// </summary>
    public async Task<T> ReadDuringTwoStepDeviceWriteAsync<T>(
        Guid deviceId,
        Func<CentralDbContext, Task> changeIndexedColumn,
        Func<DbCommand, bool>? readFrom,
        Func<CentralDbContext, Task<T>> read)
    {
        var hold = readFrom is null ? null : new HoldBeforeCommand(readFrom);
        await using var readerDb = hold is null ? database.Open("Reader") : database.Open("Reader", hold);
        await using var writerDb = database.Open("RowWriter");
        var readerSession = await SessionIdAsync(readerDb);
        var writerSession = await SessionIdAsync(writerDb);
        await writerDb.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY HIGH", Ct);

        var reader = hold is null ? null : Task.Run(() => read(readerDb), Ct);
        if (hold is not null && await Task.WhenAny(hold.Reached, reader!).WaitAsync(Deadline, Ct) != hold.Reached)
            Assert.Fail($"The read ended before the command it was held at: {await reader!}.");

        string? notWaiting;
        await using (var transaction = await writerDb.Database.BeginTransactionAsync(Ct))
        {
            await writerDb.Database.ExecuteSqlAsync(
                $"UPDATE [PrintBridgeDevices] SET [UpdatedAt] = SYSUTCDATETIME() WHERE [Id] = {deviceId}",
                Ct);
            hold?.Release();
            reader ??= Task.Run(() => read(readerDb), Ct);
            notWaiting = await WaitForLockAsync(readerSession, writerSession, reader, " on KEY", "the writer's device row");
            await changeIndexedColumn(writerDb);
            await transaction.CommitAsync(Ct);
        }

        var result = await reader.WaitAsync(Deadline, Ct);
        Assert.True(notWaiting is null, $"{notWaiting} The read answered: {result}.");
        return result;
    }

    /// <summary>A Print Bridge device read that locks its rows and selects them by <paramref name="column"/>.</summary>
    public static bool IsLockingDeviceReadBy(DbCommand command, string column) =>
        Regex.IsMatch(command.CommandText, $@"READCOMMITTEDLOCK.*?\)\s+WHERE \[{column}\] =");

    /// <summary>
    /// Null once SQL Server reports <paramref name="waiter"/> waiting, on a resource named by
    /// <paramref name="resource"/>, for <paramref name="holder"/>; otherwise what was seen instead.
    /// </summary>
    private async Task<string?> WaitForLockAsync(int waiter, int holder, Task writer, string resource, string description)
    {
        await using var inspector = database.Open("LockInspector");
        var until = DateTime.UtcNow + Deadline;
        var seen = "nothing";
        while (DateTime.UtcNow < until)
        {
            if (writer.IsCompleted)
                return $"The caller finished instead of waiting for {description} (last seen: {seen}).";

            var waits = await inspector.Database
                .SqlQuery<string>($"""
                    SELECT CONCAT(wait_type, ' on ', wait_resource) AS [Value] FROM sys.dm_exec_requests
                    WHERE session_id = {waiter} AND blocking_session_id = {holder}
                    """)
                .ToListAsync(Ct);
            if (waits.Any(w => w.Contains(resource, StringComparison.Ordinal)))
                return null;
            if (waits.Count > 0)
                seen = string.Join(", ", waits);
            await Task.Delay(10, Ct);
        }

        return $"The caller never waited for {description} (last seen: {seen}).";
    }

    private static async Task<int> SessionIdAsync(CentralDbContext db)
    {
        await db.Database.OpenConnectionAsync(Ct);
        return await db.Database.SqlQueryRaw<int>("SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync(Ct);
    }

    /// <summary>The first command that adds or changes a Print Bridge device: the insert or update SaveChanges sends.</summary>
    private static bool IsDeviceWrite(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO [PrintBridgeDevices]", StringComparison.Ordinal)
        || command.CommandText.Contains("UPDATE [PrintBridgeDevices]", StringComparison.Ordinal);

    /// <summary>Holds its context once, right before the first command that matches, until released.</summary>
    private sealed class HoldBeforeCommand(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Reached => _reached.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task HoldAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (match(command) && Interlocked.Exchange(ref _held, 1) == 0)
            {
                _reached.TrySetResult();
                await _released.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens when every caller still running has reached its first device write.</summary>
    private sealed class DeviceWriteMeeting(int callers)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected = callers;
        private int _arrived;

        public Task Arrive()
        {
            Interlocked.Increment(ref _arrived);
            Check();
            return _all.Task;
        }

        public void Leave()
        {
            Interlocked.Decrement(ref _expected);
            Check();
        }

        private void Check()
        {
            if (Volatile.Read(ref _arrived) >= Volatile.Read(ref _expected))
                _all.TrySetResult();
        }
    }

    /// <summary>One caller's place in a <see cref="DeviceWriteMeeting"/>.</summary>
    private sealed class MeetBeforeDeviceWrite(DeviceWriteMeeting meeting) : DbCommandInterceptor
    {
        private int _arrived;

        /// <summary>The caller ended; if it never arrived, the others stop waiting for it.</summary>
        public void Leave()
        {
            if (Interlocked.Exchange(ref _arrived, 1) == 0)
                meeting.Leave();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MeetAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MeetAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task MeetAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (IsDeviceWrite(command) && Interlocked.Exchange(ref _arrived, 1) == 0)
                await meeting.Arrive().WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
        }
    }
}
