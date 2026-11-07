using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Registration drafts and owner phone verification under concurrency on a real SQL Server (WAS-192). Every caller has
/// its own context and connection pool, as separate Web processes would. Free races start every caller at one barrier;
/// the submit window is held open by pausing one caller right after it claimed the draft and waiting until SQL Server
/// reports the other caller blocked by that session. SQLite cannot prove this; see docs/operations/testing.md.
/// </summary>
public sealed class RegistrationDraftSqlServerTests : IClassFixture<SqlServerSignupDatabase>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly SqlServerSignupDatabase _db;

    public RegistrationDraftSqlServerTests(SqlServerSignupDatabase db)
    {
        _db = db;
        // Each test starts with empty rolling send windows: earlier tests' sends are older than a day.
        _db.Clock.Advance(TimeSpan.FromDays(2));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Verify ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SimultaneousWrongCodes_NeverUseMoreThanFiveAttempts_AndLockTheNumber()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var draftId = await _db.SavedDraftAsync(mobile);
        var code = await _db.SendAsync(draftId, mobile);

        var results = await RaceAsync(12, db => _db.Phone(db).VerifyCodeAsync(draftId, Wrong(code), Ct));

        Assert.All(results, r => Assert.Contains(r.ErrorCode, new[] { SignupErrorCodes.PhoneCodeIncorrect, SignupErrorCodes.PhoneLocked }));
        Assert.InRange(results.Count(r => r.ErrorCode == SignupErrorCodes.PhoneCodeIncorrect), 0, 4);
        await using var check = _db.Open();
        var row = await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct);
        Assert.Equal(5, row.FailedAttempts);
        Assert.NotNull(row.LockedUntilUtc);
        Assert.Null(row.ProtectedCode);
        var draft = await check.RegistrationDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId, Ct);
        Assert.Null(draft.PhoneVerifiedAtUtc);

        // The right code no longer verifies: it was dropped with the lock.
        await using var late = _db.Open("Late");
        Assert.Equal(SignupErrorCodes.PhoneLocked, (await _db.Phone(late).VerifyCodeAsync(draftId, code, Ct)).ErrorCode);
    }

    [Fact]
    public async Task SimultaneousChecksOfTheRightCode_ConsumeItExactlyOnce()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var draftId = await _db.SavedDraftAsync(mobile);
        var code = await _db.SendAsync(draftId, mobile);

        var results = await RaceAsync(8, db => _db.Phone(db).VerifyCodeAsync(draftId, code, Ct));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[]
        {
            SignupErrorCodes.PhoneAlreadyVerified, SignupErrorCodes.PhoneLocked, SignupErrorCodes.PhoneNoActiveCode
        }));
        await using var check = _db.Open();
        var row = await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct);
        Assert.Null(row.ProtectedCode);
        Assert.NotNull(row.VerifiedAtUtc);
        Assert.InRange(row.FailedAttempts, 0, 5);
        var draft = await check.RegistrationDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId, Ct);
        Assert.Equal(mobile, draft.PhoneVerifiedNumber);
        Assert.Equal(row.VerifiedAtUtc, draft.PhoneVerifiedAtUtc);
    }

    [Fact]
    public async Task ChecksOfTheRightCodeRacingANumberChange_NeverDeadlock_AndNeverVerifyTheNewNumber()
    {
        // The check stores the proof and consumes the code, a number change updates the draft and clears its codes: both
        // lock the draft first and then the verification row, so they wait for each other instead of deadlocking.
        for (var round = 0; round < 6; round++)
        {
            var mobile = SqlServerSignupDatabase.NewMobileE164();
            var draftId = await _db.SavedDraftAsync(mobile);
            var code = await _db.SendAsync(draftId, mobile);
            var other = SqlServerSignupDatabase.NewMobileE164();

            var results = await RaceAsync(5, (db, i) => i == 0
                ? _db.Phone(db).ChangeNumberAsync(draftId, other, Ct)
                : _db.Phone(db).VerifyCodeAsync(draftId, code, Ct));

            Assert.True(results[0].Succeeded, results[0].ErrorCode);
            await using var check = _db.Open();
            var draft = await check.RegistrationDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId, Ct);
            Assert.Equal(other, draft.OwnerMobilePhone);
            Assert.NotEqual(other, draft.PhoneVerifiedNumber);
            Assert.False(await check.RegistrationPhoneVerifications.AnyAsync(r => r.DraftId == draftId && r.ProtectedCode != null, Ct));
        }
    }

    // --- Send -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SimultaneousSendsForOneDraft_SendOneCode_AndTheOthersWaitForTheResend()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var draftId = await _db.SavedDraftAsync(mobile);

        var results = await RaceAsync(8, db => _db.Phone(db).SendCodeAsync(draftId, Turkish, Ct));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(SignupErrorCodes.PhoneResendNotAvailable, r.ErrorCode));
        Assert.Single(_db.Outbox.MessagesTo(mobile));
        await using var check = _db.Open();
        Assert.Equal(1, (await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct)).SendCount);
        Assert.Equal(1, await check.RegistrationPhoneVerificationSends.CountAsync(s => s.DraftId == draftId, Ct));
    }

    [Fact]
    public async Task SimultaneousSendsOfOneDraftAtItsLimit_SendNoMoreThanTheDraftLimit()
    {
        var options = new SignupOptions();
        options.PhoneVerification.MaxSendsPerDraft = 2;
        var first = SqlServerSignupDatabase.NewMobileE164();
        var draftId = await _db.SavedDraftAsync(first);
        await _db.SendAsync(draftId, first);
        var second = SqlServerSignupDatabase.NewMobileE164();
        await using (var setup = _db.Open("Setup"))
            Assert.True((await _db.Phone(setup).ChangeNumberAsync(draftId, second, Ct)).Succeeded);

        var results = await RaceAsync(8, db => _db.Phone(db, options).SendCodeAsync(draftId, Turkish, Ct));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[]
        {
            SignupErrorCodes.PhoneResendNotAvailable, SignupErrorCodes.PhoneSendLimitReached
        }));
        await using var check = _db.Open();
        Assert.Equal(2, await check.RegistrationPhoneVerifications.Where(r => r.DraftId == draftId).SumAsync(r => r.SendCount, Ct));
        Assert.Equal(2, await check.RegistrationPhoneVerificationSends.CountAsync(s => s.DraftId == draftId, Ct));
    }

    [Fact]
    public async Task SimultaneousSendsToOneNumberFromManyDrafts_StayWithinThePerNumberLimit()
    {
        var options = new SignupOptions();
        options.PhoneVerification.MaxSendsPerNumberPerHour = 3;
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
            drafts.Add(await _db.SavedDraftAsync(mobile));

        var results = await RaceAsync(8, (db, i) => _db.Phone(db, options).SendCodeAsync(drafts[i], Turkish, Ct));

        Assert.Equal(3, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(SignupErrorCodes.PhoneSendLimitReached, r.ErrorCode));
        Assert.Equal(3, _db.Outbox.MessagesTo(mobile).Count);
        await using var check = _db.Open();
        Assert.Equal(3, await check.RegistrationPhoneVerificationSends.CountAsync(s => s.PhoneNumber == mobile, Ct));
    }

    [Fact]
    public async Task SimultaneousSendsToManyNumbers_StayWithinTheGlobalLimit()
    {
        var options = new SignupOptions();
        options.PhoneVerification.MaxSendsPerHour = 4;
        var numbers = Enumerable.Range(0, 8).Select(_ => SqlServerSignupDatabase.NewMobileE164()).ToArray();
        var drafts = new List<Guid>();
        foreach (var number in numbers)
            drafts.Add(await _db.SavedDraftAsync(number));
        var from = _db.Clock.GetUtcNow().UtcDateTime;

        var results = await RaceAsync(8, (db, i) => _db.Phone(db, options).SendCodeAsync(drafts[i], Turkish, Ct));

        Assert.Equal(4, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r =>
        {
            Assert.Equal(SignupErrorCodes.PhoneCapacityReached, r.ErrorCode);
            Assert.NotNull(r.RetryAfter);
        });
        Assert.Equal(4, numbers.Sum(n => _db.Outbox.MessagesTo(n).Count));
        await using var check = _db.Open();
        Assert.Equal(4, await check.RegistrationPhoneVerificationSends.CountAsync(s => s.SentAtUtc >= from, Ct));
    }

    // --- Submit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SimultaneousSubmitsOfOneDraft_CreateOneRegistration_AndAllAnswerWithIt()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var slug = $"race{SentinelApplicant.NewToken()}".ToLowerInvariant();
        var draftId = await VerifiedDraftAsync(mobile, slug);

        var results = await RaceAsync(8, db => _db.Drafts(db).SubmitAsync(draftId, Ct));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorCode));
        var id = results[0].RegistrationId!.Value;
        Assert.All(results, r => Assert.Equal(id, r.RegistrationId));
        Assert.Single(results, r => !r.AlreadySubmitted);
        await using var check = _db.Open();
        var registration = await check.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Slug == slug, Ct);
        Assert.Equal(id, registration.Id);
        Assert.Equal(mobile, registration.OwnerPhone);
        Assert.NotNull(registration.OwnerPhoneVerifiedAtUtc);
        Assert.Equal(id, (await check.RegistrationDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId, Ct)).PendingRegistrationId);
    }

    [Fact]
    public async Task ASubmitHoldingTheDraft_MakesASecondSubmitWait_AndThenAnswerWithTheSameRegistration()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var slug = $"held{SentinelApplicant.NewToken()}".ToLowerInvariant();
        var draftId = await VerifiedDraftAsync(mobile, slug);
        var pause = new PauseAfter(IsDraftClaim);
        await using var firstDb = _db.Open("FirstSubmit", pause);
        var firstSession = await SessionIdAsync(firstDb);

        var first = Task.Run(() => _db.Drafts(firstDb).SubmitAsync(draftId, Ct), Ct);
        await pause.Reached.WaitAsync(Deadline, Ct);

        // The first submit has claimed the draft and not inserted its registration yet.
        await using var secondDb = _db.Open("SecondSubmit");
        var second = Task.Run(() => _db.Drafts(secondDb).SubmitAsync(draftId, Ct), Ct);
        await WaitUntilBlockedByAsync(firstSession, second);

        pause.Resume();
        var firstResult = await first.WaitAsync(Deadline, Ct);
        var secondResult = await second.WaitAsync(Deadline, Ct);

        Assert.True(firstResult.Succeeded, firstResult.ErrorCode);
        Assert.False(firstResult.AlreadySubmitted);
        Assert.True(secondResult.Succeeded, secondResult.ErrorCode);
        Assert.True(secondResult.AlreadySubmitted);
        Assert.Equal(firstResult.RegistrationId, secondResult.RegistrationId);
        await using var check = _db.Open();
        Assert.Equal(1, await check.PendingRegistrations.CountAsync(r => r.Slug == slug, Ct));
    }

    // --- Cleanup --------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_OnSqlServer_DeletesExpiredDraftsWithTheirRows_AndOldSends()
    {
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var expired = await _db.SavedDraftAsync(mobile);
        await _db.SendAsync(expired, mobile);
        _db.Clock.Advance(TimeSpan.FromDays(4));
        var liveMobile = SqlServerSignupDatabase.NewMobileE164();
        var live = await _db.SavedDraftAsync(liveMobile);
        await _db.SendAsync(live, liveMobile);

        await using var db = _db.Open("Cleanup");
        await new RegistrationDraftCleanup(db, _db.Clock, NullLogger<RegistrationDraftCleanup>.Instance).RunOnceAsync(Ct);

        await using var check = _db.Open();
        Assert.False(await check.RegistrationDrafts.AnyAsync(d => d.Id == expired, Ct));
        Assert.False(await check.RegistrationPhoneVerifications.AnyAsync(r => r.DraftId == expired, Ct));
        Assert.False(await check.RegistrationPhoneVerificationSends.AnyAsync(s => s.DraftId == expired, Ct));
        Assert.True(await check.RegistrationDrafts.AnyAsync(d => d.Id == live, Ct));
        Assert.True(await check.RegistrationPhoneVerifications.AnyAsync(r => r.DraftId == live, Ct));
        Assert.True(await check.RegistrationPhoneVerificationSends.AnyAsync(s => s.DraftId == live, Ct));
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private async Task<Guid> VerifiedDraftAsync(string mobile, string slug)
    {
        var draftId = await _db.SavedDraftAsync(mobile, slug);
        var code = await _db.SendAsync(draftId, mobile);
        await using var db = _db.Open("Setup");
        Assert.True((await _db.Phone(db).VerifyCodeAsync(draftId, code, Ct)).Succeeded);
        return draftId;
    }

    private Task<T[]> RaceAsync<T>(int callers, Func<CentralDbContext, Task<T>> action) =>
        RaceAsync(callers, (db, _) => action(db));

    /// <summary>Opens one context per caller (each its own pool), then releases every caller at once.</summary>
    private async Task<T[]> RaceAsync<T>(int callers, Func<CentralDbContext, int, Task<T>> action)
    {
        var contexts = Enumerable.Range(0, callers).Select(i => _db.Open($"Caller{i}")).ToArray();
        try
        {
            foreach (var context in contexts)
                await context.Database.OpenConnectionAsync(Ct);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = contexts
                .Select((context, i) => Task.Run(async () =>
                {
                    await gate.Task;
                    return await action(context, i);
                }, Ct))
                .ToArray();
            gate.SetResult();
            return await Task.WhenAll(tasks).WaitAsync(Deadline, Ct);
        }
        finally
        {
            foreach (var context in contexts)
                await context.DisposeAsync();
        }
    }

    private static string Wrong(string code) =>
        ((int.Parse(code, CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The submit's claim of the draft: the conditional update that sets the submission time.</summary>
    private static bool IsDraftClaim(DbCommand command) =>
        command.Transaction is not null
        && command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
        && command.CommandText.Contains("[RegistrationDrafts]", StringComparison.Ordinal)
        && command.CommandText.Contains("[SubmittedAtUtc] =", StringComparison.Ordinal);

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
            Assert.False(caller.IsCompleted, "The second submit finished instead of waiting for the claimed draft.");
            var blocked = await inspector.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {session}")
                .SingleAsync(Ct);
            if (blocked > 0)
                return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The second submit was never blocked by the session holding the draft.");
    }

    /// <summary>Holds the context once, right after the first command <paramref name="match"/> accepts has executed.</summary>
    private sealed class PauseAfter(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (match(command) && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }
}

/// <summary>
/// The <c>AddRegistrationDrafts</c> migration on a real SQL Server (WAS-192): its tables, keys and constraints, and its
/// rollback, which refuses with error 50192 while a registration holds owner phone verification evidence.
/// </summary>
public sealed class RegistrationDraftMigrationSqlServerTests : IClassFixture<SqlServerSignupDatabase>
{
    private const string Migration = "_AddRegistrationDrafts";
    private readonly SqlServerSignupDatabase _db;

    public RegistrationDraftMigrationSqlServerTests(SqlServerSignupDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMigration_CreatesTheTablesKeysAndConstraints()
    {
        await using var db = _db.Open("Setup");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        var tables = await Strings(db, "SELECT name AS [Value] FROM sys.tables WHERE name LIKE 'Registration%'");
        Assert.Equal(new[] { "RegistrationDrafts", "RegistrationPhoneVerificationSends", "RegistrationPhoneVerifications" }, tables.Order(StringComparer.Ordinal));

        var checks = await Strings(db, "SELECT name AS [Value] FROM sys.check_constraints WHERE name IN ('CK_PendingRegistrations_OwnerPhoneVerified', 'CK_RegistrationPhoneVerifications_SendCount', 'CK_RegistrationPhoneVerifications_FailedAttempts')");
        Assert.Equal(3, checks.Count);

        var unique = await Strings(db, "SELECT name AS [Value] FROM sys.indexes WHERE is_unique = 1 AND name IN ('IX_RegistrationDrafts_PendingRegistrationId', 'IX_RegistrationPhoneVerifications_DraftId_PhoneNumber')");
        Assert.Equal(2, unique.Count);
        Assert.Equal(1, await Int(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_RegistrationDrafts_PendingRegistrationId' AND has_filter = 1"));

        Assert.Equal("CASCADE", (await Strings(db, "SELECT delete_referential_action_desc AS [Value] FROM sys.foreign_keys WHERE name = 'FK_RegistrationPhoneVerifications_RegistrationDrafts_DraftId'")).Single());
        Assert.Equal("SET_NULL", (await Strings(db, "SELECT delete_referential_action_desc AS [Value] FROM sys.foreign_keys WHERE name = 'FK_RegistrationDrafts_PendingRegistrations_PendingRegistrationId'")).Single());
        Assert.Equal(0, await Int(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('RegistrationPhoneVerificationSends')"));

        // A verification time without the verified number is refused by the database itself.
        var registration = BillingTestData.Registration();
        registration.OwnerPhone = null;
        registration.OwnerPhoneVerifiedAtUtc = DateTime.UtcNow;
        db.PendingRegistrations.Add(registration);
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(547, Assert.IsType<SqlException>(refused.InnerException).Number);
    }

    [Fact]
    public async Task Rollback_RefusesWhileVerificationEvidenceExists_AndWorksOnceItIsCleared()
    {
        await using var db = _db.Open("Setup");
        var migrations = db.Database.GetMigrations().ToList();
        var drafts = migrations.Single(m => m.EndsWith(Migration, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(drafts) - 1];
        var migrator = db.GetService<IMigrator>();

        var verified = BillingTestData.Registration();
        verified.OwnerPhone = "+905321234567";
        verified.OwnerPhoneVerifiedAtUtc = DateTime.UtcNow;
        db.PendingRegistrations.Add(verified);
        await db.SaveChangesAsync(Ct);
        var mobile = SqlServerSignupDatabase.NewMobileE164();
        var draftId = await _db.SavedDraftAsync(mobile);
        await _db.SendAsync(draftId, mobile);

        var refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, Ct));
        Assert.Equal(50192, refused.Number);
        Assert.Contains(drafts, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.True(await db.RegistrationDrafts.AnyAsync(d => d.Id == draftId, Ct));

        // Without evidence the rollback drops the draft tables and the column, drafts included.
        await db.PendingRegistrations.Where(r => r.OwnerPhoneVerifiedAtUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerPhoneVerifiedAtUtc, (DateTime?)null), Ct);
        await migrator.MigrateAsync(previous, Ct);
        Assert.DoesNotContain(drafts, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(0, await Int(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name LIKE 'Registration%'"));
        Assert.Equal(0, await Int(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('PendingRegistrations') AND name = 'OwnerPhoneVerifiedAtUtc'"));

        await migrator.MigrateAsync(null, Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.False(await db.RegistrationDrafts.AnyAsync(Ct));
    }

    private static async Task<List<string>> Strings(CentralDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<string>(sql).ToListAsync(Ct);

    private static async Task<int> Int(CentralDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<int>(sql).SingleAsync(Ct);
}
