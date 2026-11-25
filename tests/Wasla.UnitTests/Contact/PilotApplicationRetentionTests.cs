using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The pilot application retention rule and cleanup (WAS-222) over a real SQLite CentralDb: 90 days after the last
/// meaningful activity whatever the status, an inclusive UTC boundary, recorded activity restarting the period, a running
/// notification attempt holding the row, contact form messages never touched, a dry run changing nothing, repeats and
/// batches, failures reported and retried, and destruction records without application content kept three years.
/// SQL Server locking is covered by <see cref="PilotApplicationRetentionSqlServerTests"/>.
/// </summary>
public sealed class PilotApplicationRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly CentralTestDatabase _central = new();

    public PilotApplicationRetentionTests()
    {
        using var db = _central.CreateContext();
        db.Cities.AddRange(PilotProvinceSeed.All());
        db.SaveChanges();
    }

    public void Dispose() => _central.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- The rule ------------------------------------------------------------------------------------

    [Fact]
    public void TheBoundary_IsNinetyDaysAfterTheLastActivity_Inclusive()
    {
        var last = new DateTime(2026, 12, 1, 8, 30, 0, DateTimeKind.Utc);

        Assert.Equal(90, PilotApplicationRetention.PeriodDays);
        Assert.Equal(last.AddDays(90), PilotApplicationRetention.DeleteOnOrAfter(last));
        Assert.False(PilotApplicationRetention.IsExpired(last, last.AddDays(90) - Tick));
        Assert.True(PilotApplicationRetention.IsExpired(last, last.AddDays(90)));
        Assert.True(PilotApplicationRetention.IsExpired(last, last.AddDays(90) + Tick));
    }

    [Theory]
    [InlineData(false, 1, false)]       // no claim: nothing runs
    [InlineData(true, 0, true)]         // a claim taken just now
    [InlineData(true, 14 * 60 + 59, true)]
    [InlineData(true, 15 * 60, false)]  // 15 minutes: the attempt ended at its 3-minute deadline long ago
    [InlineData(true, 3600, false)]
    public void ANotificationClaim_HoldsTheRow_OnlyWhileItsAttemptCanStillRun(bool claimed, int secondsAgo, bool active)
    {
        Assert.Equal(active, PilotApplicationRetention.HasActiveOperation(
            claimed ? Guid.NewGuid() : null, Now.AddSeconds(-secondsAgo), Now.AddDays(-200), Now));
    }

    [Fact]
    public async Task TheQueryForm_AndTheInMemoryForm_SelectTheSameRows()
    {
        await using var db = _central.CreateContext();
        var cases = new List<(Guid Id, bool Expected)>();
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

    // --- Which rows the cleanup deletes --------------------------------------------------------------

    [Fact]
    public async Task ExpiredApplications_OfEveryStatus_AreDeleted_RecentOnesAndContactMessagesAreKept()
    {
        Guid unanswered, contacted, accepted, declined, activeConversation, recentNew, contactMessage;
        await using (var db = _central.CreateContext())
        {
            unanswered = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-90), PilotApplicationStatus.New, ct: Ct);
            contacted = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-120), PilotApplicationStatus.Contacted, Now.AddDays(-150), ct: Ct);
            accepted = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-95), PilotApplicationStatus.Accepted, Now.AddDays(-130), ct: Ct);
            declined = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-91), PilotApplicationStatus.Declined, Now.AddDays(-100), ct: Ct);
            // Received long ago, but the team spoke to the restaurant 10 days ago: still in conversation.
            activeConversation = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-10), PilotApplicationStatus.Contacted, Now.AddDays(-300), ct: Ct);
            recentNew = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-89), PilotApplicationStatus.New, ct: Ct);
            contactMessage = await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-1000), Ct);
        }

        var result = await RunAsync(dryRun: false);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { unanswered, contacted, accepted, declined }.Order(), result.Deleted.Order());
        await using var check = _central.CreateContext();
        Assert.Equal(new[] { activeConversation, recentNew, contactMessage }.Order(), (await check.ContactMessages.Select(m => m.Id).ToListAsync(Ct)).Order());
        var records = await check.DataDestructionRecords.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(result.Deleted.Order(), records.Select(r => r.SubjectId).Order());
        Assert.All(records, r =>
        {
            Assert.Equal((DataDestructionSubject.PilotApplication, DataDestructionMethod.RetentionExpired, DataDestructionResult.Deleted), (r.Subject, r.Method, r.Result));
            Assert.Equal((DataDestructionActor.RetentionJob, (Guid?)null), (r.Actor, r.ActorUserId));
            Assert.Equal((Now, Now.AddYears(3)), (r.OccurredAtUtc, r.RetainUntilUtc));
        });
    }

    [Fact]
    public async Task TheBoundary_KeepsARowOneTickBeforeNinetyDays_AndDeletesItAtNinetyDays()
    {
        Guid before, at, after;
        await using (var db = _central.CreateContext())
        {
            before = await PilotRetentionTestData.AddPilotAsync(db, Now - PilotApplicationRetention.Period + Tick, ct: Ct);
            at = await PilotRetentionTestData.AddPilotAsync(db, Now - PilotApplicationRetention.Period, ct: Ct);
            after = await PilotRetentionTestData.AddPilotAsync(db, Now - PilotApplicationRetention.Period - Tick, ct: Ct);
        }

        var result = await RunAsync(dryRun: false);

        Assert.Equal(new[] { at, after }.Order(), result.Deleted.Order());
        await using var check = _central.CreateContext();
        Assert.Equal(before, await check.ContactMessages.Select(m => m.Id).SingleAsync(Ct));

        // One tick later the kept row's period has ended too.
        var later = await RunAsync(dryRun: false, Now + Tick);
        Assert.Equal(before, Assert.Single(later.Deleted));
    }

    [Fact]
    public async Task TheCleanup_ComparesUtc_WhateverTheClocksOffset()
    {
        // The clock reads 01:00 on 2 March in UTC+3, which is 22:00 on 1 March UTC: only the UTC instant counts.
        var clock = new ManualClock(new DateTimeOffset(2027, 3, 2, 1, 0, 0, TimeSpan.FromHours(3)));
        var utcNow = clock.GetUtcNow().UtcDateTime;
        Guid dueInUtc, dueOnlyInLocalTime;
        await using (var db = _central.CreateContext())
        {
            dueInUtc = await PilotRetentionTestData.AddPilotAsync(db, utcNow - PilotApplicationRetention.Period, ct: Ct);
            // Ninety days before the local wall time 01:00 would be three hours later than the UTC boundary.
            dueOnlyInLocalTime = await PilotRetentionTestData.AddPilotAsync(db, new DateTime(2027, 3, 2, 1, 0, 0, DateTimeKind.Utc) - PilotApplicationRetention.Period, ct: Ct);
        }

        await using var context = _central.CreateContext();
        var result = await Cleanup(context, clock).RunAsync(new PilotRetentionRunRequest(false), Ct);

        Assert.Equal(utcNow, result.NowUtc);
        Assert.Equal(DateTimeKind.Utc, result.NowUtc.Kind);
        Assert.Equal(dueInUtc, Assert.Single(result.Deleted));
        await using var check = _central.CreateContext();
        Assert.Equal(dueOnlyInLocalTime, await check.ContactMessages.Select(m => m.Id).SingleAsync(Ct));
    }

    [Fact]
    public async Task RecordedActivity_RestartsTheNinetyDays_AndACorrectionDoesNot()
    {
        Guid contactedLater, statusChanged, corrected;
        await using (var db = _central.CreateContext())
        {
            contactedLater = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);
            statusChanged = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);
            corrected = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);
        }

        // The team acts on the first two just before the cleanup runs; the third is only corrected on request.
        var clock = new ManualClock(new DateTimeOffset(Now));
        await using (var admin = _central.CreateContext())
        {
            var service = AdminService(admin, clock);
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.RecordContactAsync(contactedLater, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.SetStatusAsync(statusChanged, PilotApplicationStatus.Accepted, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.CorrectAsync(corrected, Correction("Düzeltilmiş Restoran"), Ct));
        }

        var first = await RunAsync(dryRun: false);
        Assert.Equal(corrected, Assert.Single(first.Deleted));

        // Ninety days after the recorded activity both are deleted, one tick before neither is.
        Assert.Empty((await RunAsync(dryRun: false, Now.AddDays(90) - Tick)).Deleted);
        Assert.Equal(new[] { contactedLater, statusChanged }.Order(), (await RunAsync(dryRun: false, Now.AddDays(90))).Deleted.Order());
    }

    [Fact]
    public async Task ARunningNotificationAttempt_HoldsTheApplication_AndAStuckClaimDoesNot()
    {
        Guid running, stuck;
        await using (var db = _central.CreateContext())
        {
            running = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-200), ct: Ct);
            await PilotRetentionTestData.ClaimNotificationAsync(db, running, Now.AddMinutes(-2), Ct);
            stuck = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-200), ct: Ct);
            await PilotRetentionTestData.ClaimNotificationAsync(db, stuck, Now.AddHours(-5), Ct);
        }

        var result = await RunAsync(dryRun: false);

        Assert.Equal(stuck, Assert.Single(result.Deleted));
        Assert.DoesNotContain(running, result.Expired.Select(e => e.Id));

        // Once the attempt can no longer be running, the next run deletes it.
        var later = await RunAsync(dryRun: false, Now.AddMinutes(14));
        Assert.Equal(running, Assert.Single(later.Deleted));
    }

    // --- Dry run, repeats and batches ------------------------------------------------------------------

    [Fact]
    public async Task ADryRun_ChangesNothing_AndReportsIdsStatusesAndDatesOnly()
    {
        var expected = new List<Guid>();
        await using (var db = _central.CreateContext())
        {
            expected.Add(await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-150), PilotApplicationStatus.Declined, ct: Ct));
            expected.Add(await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-95), ct: Ct));
            await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-5), ct: Ct);
            db.DataDestructionRecords.Add(DataDestructionRecord.Create(
                DataDestructionSubject.PilotApplication, Guid.NewGuid(), DataDestructionMethod.RetentionExpired, DataDestructionResult.Deleted,
                DataDestructionActor.RetentionJob, null, Now.AddYears(-4)));
            await db.SaveChangesAsync(Ct);
        }

        var before = await SnapshotAsync();
        var result = await RunAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(expected, result.Expired.Select(e => e.Id));
        Assert.Equal(PilotApplicationStatus.Declined, result.Expired[0].Status);
        Assert.Equal(Now.AddDays(-150).AddDays(90), result.Expired[0].DeleteOnOrAfterUtc);
        Assert.Empty(result.Deleted);
        Assert.Equal(1, result.DestructionRecordsPastRetention);
        Assert.Equal(before, await SnapshotAsync());

        var json = JsonSerializer.Serialize(result);
        Assert.All(PilotRetentionTestData.PersonalValues, value => Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARepeatedRun_DeletesNothingMore_AndRecordsNothingMore()
    {
        await using (var db = _central.CreateContext())
        {
            for (var i = 0; i < 3; i++)
                await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100 - i), ct: Ct);
        }

        var first = await RunAsync(dryRun: false);
        var second = await RunAsync(dryRun: false);

        Assert.Equal(3, first.Deleted.Count);
        Assert.Empty(second.Expired);
        Assert.Empty(second.Deleted);
        Assert.True(second.Succeeded);
        await using var check = _central.CreateContext();
        Assert.Equal(3, await check.DataDestructionRecords.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreRowsThanOneBatch_AreAllHandled_InBoundedReads(bool sameActivityTime)
    {
        const int rows = 25;
        const int batch = 4;
        var ids = new List<Guid>();
        await using (var db = _central.CreateContext())
        {
            for (var i = 0; i < rows; i++)
                ids.Add(await PilotRetentionTestData.AddPilotAsync(db, sameActivityTime ? Now.AddDays(-100) : Now.AddDays(-100).AddMinutes(-i), ct: Ct));
            await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-1), ct: Ct);
        }

        foreach (var dryRun in new[] { true, false })
        {
            var reads = new CountExpiredReads();
            await using var db = _central.CreateContextWith(reads);
            var result = await Cleanup(db, new ManualClock(new DateTimeOffset(Now))).RunAsync(new PilotRetentionRunRequest(dryRun, batch), Ct);

            // Every row once, never one twice, in rounds of at most four: six full rounds and one partial.
            Assert.Equal(ids.Order(), result.Expired.Select(e => e.Id).Order());
            Assert.Equal(rows, result.Expired.Select(e => e.Id).Distinct().Count());
            Assert.Equal((rows + batch - 1) / batch, reads.Reads);
            Assert.Equal(dryRun ? 0 : rows, result.Deleted.Count);
        }

        await using var check = _central.CreateContext();
        Assert.Equal(1, await check.ContactMessages.CountAsync(Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PilotRetentionRun.MaxBatchSize + 1)]
    public async Task ABatchSizeOutOfRange_IsRefused(int batchSize)
    {
        await using var db = _central.CreateContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Cleanup(db, new ManualClock(new DateTimeOffset(Now))).RunAsync(new PilotRetentionRunRequest(false, batchSize), Ct));
    }

    // --- Failures ------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedDeletion_IsLoggedRecordedAndReported_TheOthersContinue_AndTheNextRunDeletesIt()
    {
        Guid failing, other;
        await using (var db = _central.CreateContext())
        {
            failing = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-120), ct: Ct);
            other = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-110), ct: Ct);
        }

        var logger = new CapturingLogger<PilotApplicationRetentionCleanup>();
        var failure = new FailDeleteOf(failing);
        PilotRetentionRunResult first;
        await using (var db = _central.CreateContextWith(failure))
            first = await new PilotApplicationRetentionCleanup(db, new ManualClock(new DateTimeOffset(Now)), logger).RunAsync(new PilotRetentionRunRequest(false), Ct);

        Assert.False(first.Succeeded);
        Assert.Equal(failing, Assert.Single(first.Failed));
        Assert.Equal(other, Assert.Single(first.Deleted));
        Assert.Equal(1, failure.Failures);
        await using (var check = _central.CreateContext())
        {
            Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == failing, Ct));
            var failed = await check.DataDestructionRecords.SingleAsync(r => r.SubjectId == failing, Ct);
            Assert.Equal(DataDestructionResult.Failed, failed.Result);
        }

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains(failing.ToString(), StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException", error.Text, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("Failed=1", StringComparison.Ordinal));
        Assert.All(logger.Entries, e => Assert.All(PilotRetentionTestData.PersonalValues, v => Assert.DoesNotContain(v, e.Text, StringComparison.OrdinalIgnoreCase)));

        var retry = await RunAsync(dryRun: false);
        Assert.True(retry.Succeeded);
        Assert.Equal(failing, Assert.Single(retry.Deleted));
        await using var after = _central.CreateContext();
        Assert.Equal(
            new[] { DataDestructionResult.Deleted, DataDestructionResult.Failed }.Order(),
            (await after.DataDestructionRecords.Where(r => r.SubjectId == failing).Select(r => r.Result).ToListAsync(Ct)).Order());
    }

    /// <summary>
    /// A pilot application with a last activity but no status cannot be stored (CK_ContactMessages_PilotFields). If one
    /// exists anyway (the constraint bypassed), it is reported as failed with a failed destruction record, not deleted, and
    /// the run carries on with the other rows instead of stopping on it.
    /// </summary>
    [Fact]
    public async Task APilotApplicationWithoutAStatus_IsRefusedByTheDatabase_AndIfPresent_IsReportedNotDeleted_TheOthersContinue()
    {
        Guid malformed, valid;
        await using (var db = _central.CreateContext())
        {
            malformed = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-120), ct: Ct);
            valid = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-110), ct: Ct);

            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync(
                $"UPDATE \"ContactMessages\" SET \"PilotStatus\" = NULL WHERE \"Id\" = {malformed}", Ct));

            await db.Database.OpenConnectionAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE \"ContactMessages\" SET \"PilotStatus\" = NULL WHERE \"Id\" = {malformed}", Ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = OFF;", Ct);
        }

        var before = await SnapshotAsync();
        var dryRun = await RunAsync(dryRun: true);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(malformed, Assert.Single(dryRun.Failed));
        Assert.Null(dryRun.Expired.Single(c => c.Id == malformed).Status);
        Assert.False(dryRun.Succeeded);

        var logger = new CapturingLogger<PilotApplicationRetentionCleanup>();
        PilotRetentionRunResult run;
        await using (var db = _central.CreateContext())
            run = await new PilotApplicationRetentionCleanup(db, new ManualClock(new DateTimeOffset(Now)), logger).RunAsync(new PilotRetentionRunRequest(false, 1), Ct);

        Assert.False(run.Succeeded);
        Assert.False(run.StoppedEarly);
        Assert.Equal(malformed, Assert.Single(run.Failed));
        Assert.Equal(valid, Assert.Single(run.Deleted));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains(malformed.ToString(), StringComparison.Ordinal) && e.Text.Contains("no pilot status", StringComparison.Ordinal));
        Assert.All(logger.Entries, e => Assert.All(PilotRetentionTestData.PersonalValues, v => Assert.DoesNotContain(v, e.Text, StringComparison.OrdinalIgnoreCase)));
        await using var check = _central.CreateContext();
        Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == malformed, Ct));
        Assert.Equal(DataDestructionResult.Failed, (await check.DataDestructionRecords.SingleAsync(r => r.SubjectId == malformed, Ct)).Result);
    }

    /// <summary>
    /// A contact form message whose pilot columns hold expired values (by mistake or on purpose) is still not a pilot
    /// application: only the topic decides. Without the topic check in the rule this row would be selected and deleted.
    /// </summary>
    [Fact]
    public async Task AContactMessageWithPilotValues_IsNeverSelectedOrDeleted_BecauseOfItsTopic()
    {
        Guid contact;
        await using (var db = _central.CreateContext())
        {
            contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-400), Ct);
            await db.ContactMessages.Where(m => m.Id == contact).ExecuteUpdateAsync(
                s => s.SetProperty(m => m.PilotStatus, (PilotApplicationStatus?)PilotApplicationStatus.Declined)
                    .SetProperty(m => m.PilotLastActivityAtUtc, (DateTime?)Now.AddDays(-400)),
                Ct);
            Assert.Empty(await db.ContactMessages.AsNoTracking().Where(PilotApplicationRetention.ExpiredAt(Now)).ToListAsync(Ct));
        }

        var dryRun = await RunAsync(dryRun: true);
        var run = await RunAsync(dryRun: false);

        Assert.Empty(dryRun.Expired);
        Assert.Empty(run.Expired);
        Assert.True(run.Succeeded);
        await using var check = _central.CreateContext();
        Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == contact, Ct));
        Assert.False(await check.DataDestructionRecords.AnyAsync(Ct));
        Assert.Equal(
            PilotApplicationChangeOutcome.NotFound,
            await AdminService(check, new ManualClock(new DateTimeOffset(Now))).DeleteOnRequestAsync(contact, Guid.NewGuid(), Ct));
        Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == contact, Ct));
    }

    /// <summary>
    /// When a later batch read fails for good, or the removal of old destruction records fails, the run stops but returns
    /// and logs what it already did: the applications deleted before the failure, each with its destruction record.
    /// </summary>
    [Theory]
    [InlineData("batch")]
    [InlineData("records")]
    public async Task ARunThatFailsPartway_ReturnsWhatItAlreadyDeleted_AndReportsTheStop(string failingStep)
    {
        var ids = new List<Guid>();
        await using (var db = _central.CreateContext())
        {
            for (var i = 0; i < 5; i++)
                ids.Add(await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-120).AddMinutes(i), ct: Ct));
        }

        var failure = failingStep == "batch"
            ? new FailReads(PilotApplicationRetentionCleanup.ExpiredBatchTag, read => read == 2 ? new InvalidOperationException("Simulated lost connection.") : null)
            : new FailReads("FROM \"DataDestructionRecords\"", _ => new InvalidOperationException("Simulated lost connection."));
        var logger = new CapturingLogger<PilotApplicationRetentionCleanup>();
        PilotRetentionRunResult run;
        await using (var db = _central.CreateContextWith(failure))
            run = await new PilotApplicationRetentionCleanup(db, new ManualClock(new DateTimeOffset(Now)), logger).RunAsync(new PilotRetentionRunRequest(false, 2), Ct);

        var deletedBeforeTheStop = failingStep == "batch" ? ids.Take(2).ToList() : ids;
        Assert.True(run.StoppedEarly);
        Assert.False(run.Succeeded);
        Assert.Equal("InvalidOperationException", run.StopReason);
        Assert.Equal(deletedBeforeTheStop, run.Deleted);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains($"Deleted={deletedBeforeTheStop.Count}", StringComparison.Ordinal) && e.Text.Contains("StoppedEarly=True", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Text.Contains("Simulated lost connection", StringComparison.Ordinal));
        await using var check = _central.CreateContext();
        Assert.Equal(deletedBeforeTheStop.Order(), (await check.DataDestructionRecords.Select(r => r.SubjectId).ToListAsync(Ct)).Order());
        Assert.Equal(ids.Except(deletedBeforeTheStop).Order(), (await check.ContactMessages.Select(m => m.Id).ToListAsync(Ct)).Order());

        var next = await RunAsync(dryRun: false);
        Assert.True(next.Succeeded);
        Assert.Equal(ids.Except(deletedBeforeTheStop).Order(), next.Deleted.Order());
    }

    /// <summary>
    /// A batch read chosen as a SQL Server deadlock victim (1205) is read again, each retry logged, at most
    /// <see cref="PilotApplicationRetentionCleanup.MaxReadAttempts"/> times in all; then the run stops and says so. Other
    /// errors are not retried.
    /// </summary>
    [Theory]
    [InlineData(SqlServerErrors.DeadlockVictim, 2, 3, true)]
    [InlineData(SqlServerErrors.DeadlockVictim, 3, 3, false)]
    [InlineData(SqlServerErrors.DeadlockVictim, 100, 3, false)]
    [InlineData(4060, 1, 1, false)]
    public async Task ABatchReadLosingADeadlock_IsReadAgain_AFewTimesOnly(int errorNumber, int failingReads, int expectedReads, bool succeeds)
    {
        Guid id;
        await using (var db = _central.CreateContext())
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-120), ct: Ct);

        var failure = new FailReads(PilotApplicationRetentionCleanup.ExpiredBatchTag, read => read <= failingReads ? SqlServerErrors.Create(errorNumber) : null);
        var logger = new CapturingLogger<PilotApplicationRetentionCleanup>();
        PilotRetentionRunResult run;
        await using (var db = _central.CreateContextWith(failure))
            run = await new PilotApplicationRetentionCleanup(db, new ManualClock(new DateTimeOffset(Now)), logger).RunAsync(new PilotRetentionRunRequest(false), Ct);

        Assert.Equal(expectedReads, failure.Reads);
        Assert.Equal(succeeds, run.Succeeded);
        var retries = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Text.Contains("lost a deadlock", StringComparison.Ordinal)).ToList();
        Assert.Equal(errorNumber == SqlServerErrors.DeadlockVictim ? Math.Min(failingReads, expectedReads - 1) : 0, retries.Count);
        if (succeeds)
        {
            Assert.Equal(id, Assert.Single(run.Deleted));
        }
        else
        {
            Assert.Empty(run.Deleted);
            Assert.Equal($"SqlException (SQL Server error {errorNumber})", run.StopReason);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains($"SqlErrorNumber={errorNumber}", StringComparison.Ordinal));
        }
    }

    // --- Destruction records -----------------------------------------------------------------------

    [Fact]
    public async Task ADestructionRecord_HoldsIdentifiersAndCodesOnly()
    {
        Guid id;
        await using (var db = _central.CreateContext())
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), PilotApplicationStatus.Declined, ct: Ct);

        await RunAsync(dryRun: false);

        Assert.Equal(
            new[] { "Actor", "ActorUserId", "Id", "Method", "OccurredAtUtc", "Result", "RetainUntilUtc", "Subject", "SubjectId" },
            typeof(DataDestructionRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order());
        await using var check = _central.CreateContext();
        var record = await check.DataDestructionRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(id, record.SubjectId);
        // No province either: only these columns exist, and none is a plate code (a GUID may contain any two digits).
        var json = JsonSerializer.Serialize(record);
        Assert.All(PilotRetentionTestData.PersonalValues.Append("tr-TR"), value => Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DestructionRecords_AreKeptThreeYears_ThenTheCleanupRemovesThem()
    {
        await using (var db = _central.CreateContext())
            await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), ct: Ct);
        await RunAsync(dryRun: false);

        var dueAt = Now.AddYears(DataDestructionRecord.RetentionYears);
        Assert.Equal(0, (await RunAsync(dryRun: false, dueAt - Tick)).DestructionRecordsPastRetention);
        Assert.Equal(1, (await RunAsync(dryRun: true, dueAt)).DestructionRecordsPastRetention);
        await using (var check = _central.CreateContext())
            Assert.Equal(1, await check.DataDestructionRecords.CountAsync(Ct));

        Assert.Equal(1, (await RunAsync(dryRun: false, dueAt)).DestructionRecordsPastRetention);
        await using var after = _central.CreateContext();
        Assert.False(await after.DataDestructionRecords.AnyAsync(Ct));
    }

    [Fact]
    public void ADestructionRecord_RefusesLocalTimesAndAnUnnamedAdministrator()
    {
        Assert.Throws<ArgumentException>(() => DataDestructionRecord.Create(
            DataDestructionSubject.PilotApplication, Guid.NewGuid(), DataDestructionMethod.DataSubjectRequest, DataDestructionResult.Deleted,
            DataDestructionActor.CentralAdmin, null, Now));
        Assert.Throws<ArgumentException>(() => DataDestructionRecord.Create(
            DataDestructionSubject.PilotApplication, Guid.NewGuid(), DataDestructionMethod.RetentionExpired, DataDestructionResult.Deleted,
            DataDestructionActor.RetentionJob, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => DataDestructionRecord.Create(
            DataDestructionSubject.PilotApplication, Guid.NewGuid(), DataDestructionMethod.RetentionExpired, DataDestructionResult.Deleted,
            DataDestructionActor.RetentionJob, null, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private async Task<PilotRetentionRunResult> RunAsync(bool dryRun, DateTime? at = null)
    {
        await using var db = _central.CreateContext();
        return await Cleanup(db, new ManualClock(new DateTimeOffset(at ?? Now))).RunAsync(new PilotRetentionRunRequest(dryRun), Ct);
    }

    private static PilotApplicationRetentionCleanup Cleanup(CentralDbContext db, TimeProvider clock) =>
        new(db, clock, NullLogger<PilotApplicationRetentionCleanup>.Instance);

    private static CentralAdminPilotApplicationService AdminService(CentralDbContext db, TimeProvider clock) =>
        new(db, new TurkishProvinceCatalog(db), clock, NullLogger<CentralAdminPilotApplicationService>.Instance);

    private static PilotApplicationCorrection Correction(string restaurant) =>
        new(restaurant, PilotRetentionTestData.ContactName, "corrected@retention.test", "5329876543", "06", "Corrected note");

    private async Task<string> SnapshotAsync()
    {
        await using var db = _central.CreateContext();
        var rows = await db.ContactMessages.AsNoTracking().OrderBy(m => m.Id).ToListAsync(Ct);
        var records = await db.DataDestructionRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
        return JsonSerializer.Serialize(new { rows, records });
    }
}
