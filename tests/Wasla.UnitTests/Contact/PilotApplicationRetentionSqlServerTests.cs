using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The pilot application retention cleanup and the deletion on request on a real SQL Server CentralDb migrated with this
/// build's migrations (WAS-222). Every caller has its own context and connection pool, as two scheduled runs or a run and
/// an administrator would. The held cases stop one caller at a chosen command and wait until SQL Server reports the other
/// blocked by that caller's session (<c>sys.dm_exec_requests</c>), never a fixed sleep. Fictional data only.
/// </summary>
public sealed class PilotApplicationRetentionSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminId = Guid.Parse("1b2c3d4e-5f60-4a7b-8c9d-0e1f2a3b4c5d");

    private readonly SqlServerCentralDatabase _db;

    public PilotApplicationRetentionSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoRunsAtOnce_DeleteAndRecordEveryExpiredApplicationOnce()
    {
        await ClearAsync();
        var expired = new List<Guid>();
        Guid recent;
        await using (var db = _db.Open("Seed"))
        {
            for (var i = 0; i < 60; i++)
                expired.Add(await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-91).AddMinutes(-(i % 7)), ct: Ct));
            recent = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-30), ct: Ct);
        }

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            await using var db = _db.Open($"Run{i}");
            await go.Task;
            return await Cleanup(db).RunAsync(new PilotRetentionRunRequest(false, 7), Ct);
        }, Ct)).ToList();
        go.SetResult();
        var results = await Task.WhenAll(runs).WaitAsync(Deadline, Ct);

        Assert.All(results, r => Assert.True(r.Succeeded));
        var deleted = results.SelectMany(r => r.Deleted).ToList();
        Assert.Equal(expired.Order(), deleted.Order());
        Assert.Empty(results[0].Deleted.Intersect(results[1].Deleted));
        await using var check = _db.Open();
        Assert.Equal(recent, await check.ContactMessages.Select(m => m.Id).SingleAsync(Ct));
        var records = await check.DataDestructionRecords.AsNoTracking().Select(r => r.SubjectId).ToListAsync(Ct);
        Assert.Equal(expired.Order(), records.Order());
    }

    [Fact]
    public async Task ARunHoldingItsDelete_MakesASecondRunWait_WhichThenFindsNothingToDelete()
    {
        await ClearAsync();
        Guid id;
        await using (var db = _db.Open("Seed"))
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);

        var hold = new HoldCommand(IsContactMessageDelete, afterExecution: true);
        await using var firstDb = _db.Open("FirstRun", hold);
        var firstSession = await SessionIdAsync(firstDb);
        var first = Task.Run(() => Cleanup(firstDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await hold.Reached.WaitAsync(Deadline, Ct);

        // The first run deleted the row and has not committed. The second run read it as expired before that, or waits to
        // read it; either way its own delete must wait for the first run's lock instead of deleting it a second time.
        await using var secondDb = _db.Open("SecondRun");
        var second = Task.Run(() => Cleanup(secondDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await WaitUntilBlockedByAsync(firstSession, second);

        hold.Release();
        var firstResult = await first.WaitAsync(Deadline, Ct);
        var secondResult = await second.WaitAsync(Deadline, Ct);

        Assert.Equal(id, Assert.Single(firstResult.Deleted));
        Assert.Empty(secondResult.Deleted);
        Assert.True(secondResult.Succeeded);
        await using var check = _db.Open();
        Assert.Equal(id, await check.DataDestructionRecords.Select(r => r.SubjectId).SingleAsync(Ct));
    }

    [Fact]
    public async Task AContactRecordedAfterTheRunReadTheRow_ButBeforeItsDelete_KeepsTheApplication()
    {
        await ClearAsync();
        Guid id;
        await using (var db = _db.Open("Seed"))
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), PilotApplicationStatus.Contacted, ct: Ct);

        // The run has read the row as expired and stops right before its delete.
        var hold = new HoldCommand(IsContactMessageDelete, afterExecution: false);
        await using var runDb = _db.Open("Run", hold);
        var run = Task.Run(() => Cleanup(runDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await hold.Reached.WaitAsync(Deadline, Ct);

        await using (var adminDb = _db.Open("Admin"))
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await AdminService(adminDb, Now.AddMinutes(1)).RecordContactAsync(id, Ct));

        hold.Release();
        var result = await run.WaitAsync(Deadline, Ct);

        Assert.Equal(id, Assert.Single(result.Expired).Id);
        Assert.Equal(id, Assert.Single(result.NoLongerExpired));
        Assert.Empty(result.Deleted);
        await using var check = _db.Open();
        Assert.Equal(Now.AddMinutes(1), (await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct)).PilotLastActivityAtUtc);
        Assert.False(await check.DataDestructionRecords.AnyAsync(Ct));
    }

    [Fact]
    public async Task AnUncommittedStatusChange_MakesTheRunWait_AndThenKeepTheApplication()
    {
        await ClearAsync();
        Guid id;
        await using (var db = _db.Open("Seed"))
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);

        await using var adminDb = _db.Open("Admin");
        var adminSession = await SessionIdAsync(adminDb);
        await using var transaction = await adminDb.Database.BeginTransactionAsync(Ct);
        Assert.Equal(PilotApplicationChangeOutcome.Changed, await AdminService(adminDb, Now).SetStatusAsync(id, PilotApplicationStatus.Accepted, Ct));

        await using var runDb = _db.Open("Run");
        var run = Task.Run(() => Cleanup(runDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await WaitUntilBlockedByAsync(adminSession, run);

        await transaction.CommitAsync(Ct);
        var result = await run.WaitAsync(Deadline, Ct);

        Assert.Empty(result.Deleted);
        await using var check = _db.Open();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
        Assert.Equal((PilotApplicationStatus.Accepted, Now), (row.PilotStatus!.Value, row.PilotLastActivityAtUtc!.Value));
    }

    [Fact]
    public async Task ARunHoldingItsDelete_MakesAContactRecordWait_WhichThenFindsTheApplicationGone()
    {
        await ClearAsync();
        Guid id;
        await using (var db = _db.Open("Seed"))
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);

        var hold = new HoldCommand(IsContactMessageDelete, afterExecution: true);
        await using var runDb = _db.Open("Run", hold);
        var runSession = await SessionIdAsync(runDb);
        var run = Task.Run(() => Cleanup(runDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await hold.Reached.WaitAsync(Deadline, Ct);

        await using var adminDb = _db.Open("Admin");
        var record = Task.Run(() => AdminService(adminDb, Now).RecordContactAsync(id, Ct), Ct);
        await WaitUntilBlockedByAsync(runSession, record);

        hold.Release();
        Assert.Equal(id, Assert.Single((await run.WaitAsync(Deadline, Ct)).Deleted));
        Assert.Equal(PilotApplicationChangeOutcome.NotFound, await record.WaitAsync(Deadline, Ct));
    }

    [Fact]
    public async Task ADeletionOnRequestDuringARunsDelete_WaitsAndThenFindsItGone_OneRecord()
    {
        await ClearAsync();
        Guid id;
        await using (var db = _db.Open("Seed"))
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);

        var hold = new HoldCommand(IsContactMessageDelete, afterExecution: true);
        await using var runDb = _db.Open("Run", hold);
        var runSession = await SessionIdAsync(runDb);
        var run = Task.Run(() => Cleanup(runDb).RunAsync(new PilotRetentionRunRequest(false), Ct), Ct);
        await hold.Reached.WaitAsync(Deadline, Ct);

        await using var adminDb = _db.Open("Admin");
        var delete = Task.Run(() => AdminService(adminDb, Now).DeleteOnRequestAsync(id, AdminId, Ct), Ct);
        await WaitUntilBlockedByAsync(runSession, delete);

        hold.Release();
        await run.WaitAsync(Deadline, Ct);
        Assert.Equal(PilotApplicationChangeOutcome.NotFound, await delete.WaitAsync(Deadline, Ct));
        await using var check = _db.Open();
        var only = await check.DataDestructionRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((id, DataDestructionMethod.RetentionExpired), (only.SubjectId, only.Method));
    }

    [Fact]
    public async Task TheQueryForm_AndTheInMemoryForm_SelectTheSameRows_OnSqlServer()
    {
        await ClearAsync();
        var cases = new List<(Guid Id, bool Expected)>();
        await using var db = _db.Open("Seed");
        foreach (var days in new[] { 0, 89, 90, 91, 400 })
        {
            foreach (var claimSecondsAgo in new int?[] { null, 60, 15 * 60 })
            {
                var last = Now.AddDays(-days);
                var id = await PilotRetentionTestData.AddPilotAsync(db, last, ct: Ct);
                if (claimSecondsAgo is { } ago)
                    await PilotRetentionTestData.ClaimNotificationAsync(db, id, Now.AddSeconds(-ago), Ct);
                var active = claimSecondsAgo is { } a && PilotApplicationRetention.HasActiveOperation(Guid.NewGuid(), Now.AddSeconds(-a), last, Now);
                cases.Add((id, PilotApplicationRetention.IsExpired(last, Now) && !active));
            }
        }

        await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-400), Ct);

        var selected = await db.ContactMessages.AsNoTracking().Where(PilotApplicationRetention.ExpiredAt(Now)).Select(m => m.Id).ToListAsync(Ct);
        Assert.Equal(cases.Where(c => c.Expected).Select(c => c.Id).Order(), selected.Order());
    }

    private async Task ClearAsync()
    {
        await using var db = _db.Open("Setup");
        await db.DataDestructionRecords.ExecuteDeleteAsync(Ct);
        await db.ContactMessages.ExecuteDeleteAsync(Ct);
        if (!await db.Cities.AnyAsync(c => c.PlateCode == "34", Ct))
        {
            db.Cities.AddRange(PilotProvinceSeed.All(idOffset: 1000).Select(c => new City
            {
                CountryCode = c.CountryCode,
                Name = c.Name,
                PlateCode = c.PlateCode,
                SortOrder = c.SortOrder,
                IsActive = true
            }));
            await db.SaveChangesAsync(Ct);
        }
    }

    private static PilotApplicationRetentionCleanup Cleanup(CentralDbContext db) =>
        new(db, new ManualClock(new DateTimeOffset(Now)), NullLogger<PilotApplicationRetentionCleanup>.Instance);

    private static CentralAdminPilotApplicationService AdminService(CentralDbContext db, DateTime now) =>
        new(db, new TurkishProvinceCatalog(db), new ManualClock(new DateTimeOffset(now)), NullLogger<CentralAdminPilotApplicationService>.Instance);

    private static bool IsContactMessageDelete(DbCommand command) =>
        command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
        && command.CommandText.Contains("[ContactMessages]", StringComparison.Ordinal);

    /// <summary>The session id of the context's own connection, which stays open for the test.</summary>
    private static async Task<int> SessionIdAsync(CentralDbContext db)
    {
        await db.Database.OpenConnectionAsync(Ct);
        return await db.Database.SqlQueryRaw<int>("SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync(Ct);
    }

    /// <summary>Waits until SQL Server reports a request blocked by <paramref name="session"/>; fails if the caller finished first.</summary>
    private async Task WaitUntilBlockedByAsync(int session, Task caller)
    {
        await using var inspector = _db.Open("Inspector");
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            Assert.False(caller.IsCompleted, "The second caller finished instead of waiting for the row lock.");
            var blocked = await inspector.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {session}")
                .SingleAsync(Ct);
            if (blocked > 0)
                return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The second caller was never blocked by the session holding the row.");
    }

    /// <summary>Holds the context once at the first command <paramref name="match"/> accepts, before or after it runs.</summary>
    private sealed class HoldCommand(Func<DbCommand, bool> match, bool afterExecution) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterExecution)
                await HoldIfMatchAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (afterExecution)
                await HoldIfMatchAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task HoldIfMatchAsync(DbCommand command, CancellationToken ct)
        {
            if (match(command) && Interlocked.Exchange(ref _held, 1) == 0)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(Deadline, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// The <c>AddPilotApplicationRetention</c> migration on a real SQL Server (WAS-222): applications stored before it start as
/// <c>New</c>, last active when received; contact form messages stay null; it rolls back and forward cleanly.
/// </summary>
public sealed class PilotApplicationRetentionMigrationSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private const string Previous = "20261121032923_AddPilotApplications";

    private readonly SqlServerCentralDatabase _db;

    public PilotApplicationRetentionMigrationSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EarlierPilotApplications_StartAsNew_LastActiveWhenReceived_AndTheMigrationRollsBack()
    {
        await using var db = _db.Open("Setup");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous, Ct);

        var pilot = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var received = new DateTime(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO [ContactMessages] ([Id], [FullName], [RestaurantName], [Email], [Phone], [Topic], [Message], [CityPlateCode], [Culture], [PrivacyNoticeAcknowledged], [CreatedAtUtc], [NotificationStatus], [NotificationAttemptedAtUtc], [NotificationClaimId])
            VALUES ({pilot}, N'Migration Contact', N'Migration Restaurant', N'migration@retention.test', '5321112233', 5, N'', '34', 'tr-TR', 0, {received}, 1, {received}, NULL),
                   ({contact}, N'Migration Sender', N'Migration Shop', N'sender@retention.test', NULL, 4, N'Hello', NULL, 'en-US', 1, {received}, 1, {received}, NULL)
            """, Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);
        db.ChangeTracker.Clear();
        var rows = await db.ContactMessages.AsNoTracking().Where(m => m.Id == pilot || m.Id == contact).ToDictionaryAsync(m => m.Id, Ct);
        Assert.Equal(((PilotApplicationStatus?)PilotApplicationStatus.New, (DateTime?)received), (rows[pilot].PilotStatus, rows[pilot].PilotLastActivityAtUtc));
        Assert.Equal(((PilotApplicationStatus?)null, (DateTime?)null), (rows[contact].PilotStatus, rows[contact].PilotLastActivityAtUtc));

        await migrator.MigrateAsync(Previous, Ct);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'DataDestructionRecords'").SingleAsync(Ct));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('ContactMessages') AND name IN ('PilotStatus', 'PilotLastActivityAtUtc')").SingleAsync(Ct));

        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        await db.Database.ExecuteSqlAsync($"DELETE FROM [ContactMessages] WHERE [Id] IN ({pilot}, {contact})", Ct);
    }
}
