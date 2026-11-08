using System.Data.Common;
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
        var hold = new HoldBeforeDeviceWrite();
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
            notWaiting = await WaitForApplicationLockAsync(secondSession, firstSession, secondTask);
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
    /// Null once SQL Server reports <paramref name="waiter"/> waiting for an application lock held by
    /// <paramref name="holder"/>; otherwise what was seen instead.
    /// </summary>
    private async Task<string?> WaitForApplicationLockAsync(int waiter, int holder, Task writer)
    {
        await using var inspector = database.Open("LockInspector");
        var until = DateTime.UtcNow + Deadline;
        var seen = "nothing";
        while (DateTime.UtcNow < until)
        {
            if (writer.IsCompleted)
                return $"The second writer finished instead of waiting for the first writer's tenant lock (last seen: {seen}).";

            var waits = await inspector.Database
                .SqlQuery<string>($"""
                    SELECT CONCAT(wait_type, ' on ', wait_resource) AS [Value] FROM sys.dm_exec_requests
                    WHERE session_id = {waiter} AND blocking_session_id = {holder}
                    """)
                .ToListAsync(Ct);
            if (waits.Any(w => w.Contains(" on APPLICATION", StringComparison.Ordinal)))
                return null;
            if (waits.Count > 0)
                seen = string.Join(", ", waits);
            await Task.Delay(10, Ct);
        }

        return $"The second writer never waited for the first writer's tenant lock (last seen: {seen}).";
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

    /// <summary>Holds its context once, right before its first device write, until released.</summary>
    private sealed class HoldBeforeDeviceWrite : DbCommandInterceptor
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
            if (IsDeviceWrite(command) && Interlocked.Exchange(ref _held, 1) == 0)
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
