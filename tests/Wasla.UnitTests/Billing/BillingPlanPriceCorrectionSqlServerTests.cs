using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// A plan price correction racing a checkout start on a real SQL Server (GitHub #64). Each writer has its own context
/// and connection pool. The contested windows are held open by pausing one writer right after it took the price row lock
/// (or wrote the row), and waiting until SQL Server reports the other writer blocked by that session; the free race starts
/// both at one barrier. Whatever the order, a subscription's snapshot equals its price row, and a price that a
/// subscription references is never corrected. SQLite cannot prove this; see docs/operations/testing.md.
/// </summary>
public sealed class BillingPlanPriceCorrectionSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly SqlServerCentralDatabase _db;

    public BillingPlanPriceCorrectionSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACheckoutThatLockedThePrice_MakesACorrectionWait_AndThenRefuseAsReferenced()
    {
        var (planCode, price) = await SeedPriceAsync();
        var registration = await SeedRegistrationAsync(planCode);
        var pause = new PauseAfter(IsLockWrite);
        await using var checkoutDb = _db.Open("Checkout", pause);
        var checkoutSession = await SessionIdAsync(checkoutDb);

        var checkout = Task.Run(() => _db.Store(checkoutDb).StartForRegistrationAsync(registration, BillingTestData.Sandbox, Ct), Ct);
        await pause.Reached.WaitAsync(Deadline, Ct);

        // The checkout holds the price row and has not inserted its subscription yet. A correction now finds no
        // reference in its first look, and must wait at the row lock instead of changing the price.
        await using var adminDb = _db.Open("Admin");
        var correction = Task.Run(() => Service(adminDb).CorrectAsync(Command(price, "77700"), BillingTestData.AdminId, Ct), Ct);
        await WaitUntilBlockedByAsync(checkoutSession, correction);

        pause.Resume();
        var started = await checkout.WaitAsync(Deadline, Ct);
        var corrected = await correction.WaitAsync(Deadline, Ct);

        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        Assert.Equal((BillingPlanPriceCorrectionOutcome.Referenced, 1), (corrected.Outcome, corrected.ReferenceCount));
        await AssertSnapshotMatchesRowAsync(started.SubscriptionId!.Value, price.Id, expectedAmount: price.Amount, expectedRevision: 1);
        await using var check = _db.Open();
        Assert.False(await check.BillingPlanPriceCorrections.AnyAsync(c => c.PlanPriceId == price.Id, Ct));
    }

    [Fact]
    public async Task ACorrectionThatWroteThePrice_MakesACheckoutWait_AndThenPriceFromTheCorrectedRow()
    {
        var (planCode, price) = await SeedPriceAsync();
        var registration = await SeedRegistrationAsync(planCode);
        var pause = new PauseAfter(command => IsPlanPriceUpdate(command) && command.CommandText.Contains("[Amount] =", StringComparison.Ordinal));
        await using var adminDb = _db.Open("Admin", pause);
        var adminSession = await SessionIdAsync(adminDb);

        var correction = Task.Run(() => Service(adminDb).CorrectAsync(Command(price, "77700"), BillingTestData.AdminId, Ct), Ct);
        await pause.Reached.WaitAsync(Deadline, Ct);

        // The correction changed the row and has not committed. A checkout for that plan must not keep the old price.
        await using var checkoutDb = _db.Open("Checkout");
        var checkout = Task.Run(() => _db.Store(checkoutDb).StartForRegistrationAsync(registration, BillingTestData.Sandbox, Ct), Ct);
        await WaitUntilBlockedByAsync(adminSession, checkout);

        pause.Resume();
        var corrected = await correction.WaitAsync(Deadline, Ct);
        var started = await checkout.WaitAsync(Deadline, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, corrected.Outcome);
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        await AssertSnapshotMatchesRowAsync(started.SubscriptionId!.Value, price.Id, expectedAmount: 777.00m, expectedRevision: 2);
    }

    [Fact]
    public async Task ACorrectionAndACheckoutStartedTogether_AlwaysLeaveTheSnapshotEqualToItsPriceRow()
    {
        var outcomes = new List<BillingPlanPriceCorrectionOutcome>();
        for (var round = 0; round < 8; round++)
        {
            var (planCode, price) = await SeedPriceAsync();
            var registration = await SeedRegistrationAsync(planCode);
            await using var adminDb = _db.Open("RaceAdmin");
            await using var checkoutDb = _db.Open("RaceCheckout");
            await adminDb.Database.OpenConnectionAsync(Ct);
            await checkoutDb.Database.OpenConnectionAsync(Ct);

            using var barrier = new Barrier(2);
            var correction = Task.Run(async () =>
            {
                barrier.SignalAndWait(Ct);
                return await Service(adminDb).CorrectAsync(Command(price, "88800"), BillingTestData.AdminId, Ct);
            }, Ct);
            var checkout = Task.Run(async () =>
            {
                barrier.SignalAndWait(Ct);
                return await _db.Store(checkoutDb).StartForRegistrationAsync(registration, BillingTestData.Sandbox, Ct);
            }, Ct);
            var corrected = await correction.WaitAsync(Deadline, Ct);
            var started = await checkout.WaitAsync(Deadline, Ct);

            Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
            Assert.Contains(corrected.Outcome, new[] { BillingPlanPriceCorrectionOutcome.Corrected, BillingPlanPriceCorrectionOutcome.Referenced });
            outcomes.Add(corrected.Outcome);
            var wasCorrected = corrected.Outcome == BillingPlanPriceCorrectionOutcome.Corrected;
            await AssertSnapshotMatchesRowAsync(
                started.SubscriptionId!.Value,
                price.Id,
                expectedAmount: wasCorrected ? 888.00m : price.Amount,
                expectedRevision: wasCorrected ? 2 : 1);
            await using var check = _db.Open();
            Assert.Equal(wasCorrected ? 1 : 0, await check.BillingPlanPriceCorrections.CountAsync(c => c.PlanPriceId == price.Id, Ct));
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Correction outcomes: {string.Join(", ", outcomes)}");
    }

    [Fact]
    public async Task TheDatabase_KeepsCorrectionsPositive_AndTheRevisionOfExistingPricesStartsAtOne()
    {
        await using var db = _db.Open();
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [BillingPlanPriceCorrections] ([Id],[PlanPriceId],[Revision],[CorrectedByCentralAdminUserId],[CorrectedAtUtc],[PreviousAmount],[PreviousCurrency],[PreviousVatIncluded],[PreviousEffectiveFromUtc],[NewAmount],[NewCurrency],[NewVatIncluded],[NewEffectiveFromUtc]) " +
            "VALUES (NEWID(), NEWID(), 2, NEWID(), '2026-10-22', 1.00, 'TRY', 1, '2026-10-22', 0, 'TRY', 1, '2026-10-22')",
            Ct));
        Assert.Equal(547, error.Number);

        // The seeded price was inserted by EF with revision 1; a row inserted without the column gets the default 1 too.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [BillingPlanPrices] ([Id],[PlanCode],[BillingPeriod],[Amount],[Currency],[VatIncluded],[EffectiveFromUtc],[CreatedByCentralAdminUserId],[CreatedAtUtc]) " +
            "VALUES ('c0000000-0000-0000-0000-00000000c064', 'ProPlus', 'Yearly', 100.00, 'TRY', 1, '2030-01-01', NEWID(), '2026-10-22')",
            Ct);
        Assert.Equal(1, await db.BillingPlanPrices.Where(p => p.Id == new Guid("c0000000-0000-0000-0000-00000000c064")).Select(p => p.Revision).SingleAsync(Ct));
        Assert.Equal(1, await db.BillingPlanPrices.Where(p => p.Id == _db.PlanPrice.Id).Select(p => p.Revision).SingleAsync(Ct));
    }

    private static bool IsPlanPriceUpdate(DbCommand command) =>
        command.Transaction is not null
        && command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
        && command.CommandText.Contains("FROM [BillingPlanPrices]", StringComparison.Ordinal);

    /// <summary>The row lock write: an update of the price row that sets the revision to itself.</summary>
    private static bool IsLockWrite(DbCommand command) =>
        IsPlanPriceUpdate(command)
        && !command.CommandText.Contains("[Amount] =", StringComparison.Ordinal)
        && !command.CommandText.Contains("+ 1", StringComparison.Ordinal);

    private BillingPlanPriceAdminService Service(CentralDbContext db) =>
        new(db, new WaslaPlanCatalog(), _db.Time, NullLogger<BillingPlanPriceAdminService>.Instance);

    /// <summary>A correction of the amount that keeps the price's date (in effect since yesterday).</summary>
    private static BillingPlanPriceCorrectionCommand Command(BillingPlanPrice price, string kurus) =>
        new(price.Id, price.Revision, kurus, price.VatIncluded,
            BillingPlanPriceInput.TurkeyDate(price.EffectiveFromUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// A Starter price in effect, newer than any earlier one: each call takes a later minute of yesterday, so the
    /// latest seeded price is the one checkout uses.
    /// </summary>
    private async Task<(string PlanCode, BillingPlanPrice Price)> SeedPriceAsync()
    {
        await using var db = _db.Open("Setup");
        var latest = await db.BillingPlanPrices.Where(p => p.PlanCode == "Starter").MaxAsync(p => (DateTime?)p.EffectiveFromUtc, Ct);
        var effective = latest?.AddMinutes(1) ?? BillingTestData.Now.AddDays(-1);
        var price = BillingPlanPrice.Create("Starter", "Monthly", new BillingPrice(123.45m, "TRY"), true, effective, BillingTestData.AdminId, BillingTestData.Now.AddDays(-1));
        db.BillingPlanPrices.Add(price);
        await db.SaveChangesAsync(Ct);
        return ("Starter", price);
    }

    private async Task<Guid> SeedRegistrationAsync(string planCode)
    {
        await using var db = _db.Open("Setup");
        var registration = BillingTestData.Registration(planCode: planCode);
        db.PendingRegistrations.Add(registration);
        await db.SaveChangesAsync(Ct);
        return registration.Id;
    }

    private async Task AssertSnapshotMatchesRowAsync(Guid subscriptionId, Guid planPriceId, decimal expectedAmount, int expectedRevision)
    {
        await using var db = _db.Open();
        var subscription = await db.BillingSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subscriptionId, Ct);
        var row = await db.BillingPlanPrices.AsNoTracking().SingleAsync(p => p.Id == planPriceId, Ct);
        Assert.Equal(planPriceId, subscription.PlanPriceId);
        Assert.Equal(row.Price, subscription.Price);
        Assert.Equal((expectedAmount, expectedRevision), (row.Amount, row.Revision));
    }

    /// <summary>The session id of the context's own connection, which stays open for the test.</summary>
    private static async Task<int> SessionIdAsync(CentralDbContext db)
    {
        await db.Database.OpenConnectionAsync(Ct);
        return await db.Database.SqlQueryRaw<int>("SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync(Ct);
    }

    /// <summary>Waits until SQL Server reports a request blocked by <paramref name="session"/>; fails if the writer finished first.</summary>
    private async Task WaitUntilBlockedByAsync(int session, Task writer)
    {
        await using var inspector = _db.Open("Inspector");
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            Assert.False(writer.IsCompleted, "The second writer finished instead of waiting for the price row lock.");
            var blocked = await inspector.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {session}")
                .SingleAsync(Ct);
            if (blocked > 0)
                return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The second writer was never blocked by the session holding the price row.");
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
/// The correction audit migration on a real SQL Server (GitHub #64): its rollback refuses while a correction is recorded
/// and changes nothing, and with none it rolls back and forward cleanly.
/// </summary>
public sealed class BillingPlanPriceCorrectionMigrationSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private readonly SqlServerCentralDatabase _db;

    public BillingPlanPriceCorrectionMigrationSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rollback_RefusesWhileACorrectionIsRecorded_AndWorksOnceThereIsNone()
    {
        await using var db = _db.Open("Setup");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        var migrations = db.Database.GetMigrations().ToList();
        var corrections = migrations.Single(m => m.EndsWith("_AddBillingPlanPriceCorrections", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(corrections) - 1];
        var migrator = db.GetService<IMigrator>();

        var service = new BillingPlanPriceAdminService(db, new WaslaPlanCatalog(), _db.Time, NullLogger<BillingPlanPriceAdminService>.Instance);
        var corrected = await service.CorrectAsync(
            new(_db.PlanPrice.Id, 1, "54321", true, BillingPlanPriceInput.TurkeyDate(_db.PlanPrice.EffectiveFromUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            BillingTestData.AdminId,
            Ct);
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, corrected.Outcome);

        var refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, Ct));
        Assert.Equal(50064, refused.Number);
        Assert.Contains(corrections, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(1, await db.BillingPlanPriceCorrections.CountAsync(Ct));
        Assert.Equal(2, await db.BillingPlanPrices.Where(p => p.Id == _db.PlanPrice.Id).Select(p => p.Revision).SingleAsync(Ct));

        await db.BillingPlanPriceCorrections.ExecuteDeleteAsync(Ct);
        await migrator.MigrateAsync(previous, Ct);
        Assert.DoesNotContain(corrections, await db.Database.GetAppliedMigrationsAsync(Ct));
        // The price itself survives the rollback, with its corrected amount.
        Assert.Equal(543.21m, await db.Database.SqlQuery<decimal>($"SELECT [Amount] AS [Value] FROM [BillingPlanPrices] WHERE [Id] = {_db.PlanPrice.Id}").SingleAsync(Ct));

        await migrator.MigrateAsync(null, Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal(1, await db.BillingPlanPrices.Where(p => p.Id == _db.PlanPrice.Id).Select(p => p.Revision).SingleAsync(Ct));
    }
}
