using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// Billing persistence on a real SQL Server (WAS-147): the migration applies, the database constraints hold without
/// the application, and simultaneous writers from separate contexts and connection pools converge to one correct row.
/// SQLite cannot prove these; see docs/operations/testing.md for the server these tests use.
/// </summary>
public sealed class BillingSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private const int Writers = 8;
    private readonly SqlServerCentralDatabase _db;

    public BillingSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SimultaneousStarts_ForOneRegistration_CreateOneSubscription()
    {
        var registration = await SeedRegistrationAsync();

        var results = await RaceAsync(store =>
            store.StartForRegistrationAsync(registration, BillingTestData.Sandbox, Ct));

        Assert.Single(results, r => r.Outcome == BillingStoreOutcome.Applied);
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { BillingStoreOutcome.Applied, BillingStoreOutcome.Unchanged }));
        Assert.Single(results.Select(r => r.SubscriptionId).Distinct());
        await using var check = _db.Open();
        Assert.Equal(1, await check.BillingSubscriptions.CountAsync(s => s.PendingRegistrationId == registration, Ct));
    }

    [Fact]
    public async Task SimultaneousIdenticalPayments_AreRecordedOnce_AndLeaveTheSubscriptionRowUnchanged()
    {
        var id = await StartBoundAsync();
        var revision = await RevisionAsync(id);
        var payment = BillingTestData.Payment("pay-" + Guid.NewGuid().ToString("N")[..8], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var results = await RaceAsync(store => store.RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, payment, Ct));

        Assert.Single(results, r => r == BillingStoreOutcome.Applied);
        Assert.All(results, r => Assert.Contains(r, new[] { BillingStoreOutcome.Applied, BillingStoreOutcome.Unchanged }));
        await using var check = _db.Open();
        Assert.Equal(1, await check.BillingPayments.CountAsync(p => p.SubscriptionId == id, Ct));
        Assert.Equal(revision, await RevisionAsync(id));
    }

    [Fact]
    public async Task SimultaneousStatusWrites_NeverLeaveAnOlderObservationInPlace()
    {
        for (var round = 0; round < 5; round++)
        {
            var id = await StartBoundAsync();
            var t1 = BillingTestData.Now.AddMinutes(1);
            await _db.Store(_db.Open("Setup")).ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Active, t1, Ct);

            // Half the writers report PastDue observed at T2, half Expired observed at T3, all at once.
            var results = await RaceAsync((store, i) => store.ApplyProviderStatusAsync(
                id,
                BillingTestData.Sandbox,
                i % 2 == 0 ? SubscriptionStatus.PastDue : SubscriptionStatus.Expired,
                i % 2 == 0 ? t1.AddMinutes(1) : t1.AddMinutes(2),
                Ct));

            Assert.DoesNotContain(BillingStoreOutcome.ConcurrencyConflict, results);
            await using var check = _db.Open();
            var stored = await check.BillingSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
            Assert.Equal(SubscriptionStatus.Expired, stored.Status);
            Assert.Equal(t1.AddMinutes(2), stored.StatusObservedAtUtc);

            // Exactly one Expired writer applies, the rest see it repeated. A PastDue writer applies only if it got in
            // first; after Expired it is stale.
            var expired = results.Where((_, i) => i % 2 == 1).ToArray();
            var pastDue = results.Where((_, i) => i % 2 == 0).ToArray();
            Assert.Single(expired, r => r == BillingStoreOutcome.Applied);
            Assert.All(expired, r => Assert.Contains(r, new[] { BillingStoreOutcome.Applied, BillingStoreOutcome.Unchanged }));
            Assert.True(pastDue.Count(r => r == BillingStoreOutcome.Applied) <= 1);
            Assert.All(pastDue, r => Assert.Contains(r, new[] { BillingStoreOutcome.Applied, BillingStoreOutcome.Unchanged, BillingStoreOutcome.Stale }));
        }
    }

    [Fact]
    public async Task TheDatabase_RefusesInvalidRows_WithoutTheApplication()
    {
        var registration = await SeedRegistrationAsync();
        await using var db = _db.Open();

        // Trialing is a tenant value, not a billing state; Active needs a provider reference; amounts are positive.
        await AssertRefusedAsync(db, InsertSql(registration, status: (int)SubscriptionStatus.Trialing), 547);
        await AssertRefusedAsync(db, InsertSql(registration, status: (int)SubscriptionStatus.Active), 547);
        await AssertRefusedAsync(db, InsertSql(registration, amount: "0"), 547);
        await AssertRefusedAsync(db, InsertSql(registration, environment: 3), 547);
        await AssertRefusedAsync(db, InsertSql(Guid.NewGuid()), 547);
        await AssertRefusedAsync(db, InsertSql(registration, planPriceId: Guid.NewGuid()), 547);

        // Plan prices: positive, Monthly or Yearly, one per plan, period and effective time, kept while referenced.
        await AssertRefusedAsync(db, InsertPlanPriceSql("0"), 547);
        await AssertRefusedAsync(db, InsertPlanPriceSql("100.00", period: "Weekly"), 547);
        await AssertRefusedAsync(
            db,
            InsertPlanPriceSql("100.00", effectiveFrom: _db.PlanPrice.EffectiveFromUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")),
            2601);

        // Two pending rows without provider reference or tenant are fine; a second row for the registration is not.
        var other = await SeedRegistrationAsync();
        await db.Database.ExecuteSqlRawAsync(InsertSql(registration), Ct);
        await db.Database.ExecuteSqlRawAsync(InsertSql(other), Ct);
        await AssertRefusedAsync(db, InsertSql(registration), 2601);

        await AssertRefusedAsync(db, $"DELETE FROM [BillingPlanPrices] WHERE [Id] = '{_db.PlanPrice.Id}'", 547);

        // A payment's environment is its subscription's.
        var id = await StartBoundAsync();
        await AssertRefusedAsync(
            db,
            $"INSERT INTO [BillingPayments] ([Id],[SubscriptionId],[Provider],[Environment],[ExternalPaymentReference],[Amount],[Currency],[PeriodStartUtc],[PeriodEndUtc],[VerifiedAtUtc],[CreatedAtUtc]) " +
            $"VALUES (NEWID(), '{id}', 1, 2, 'pay-x', 999.00, 'TRY', '2026-10-01', '2026-11-01', '2026-10-01', '2026-10-01')",
            547);
    }

    private async Task<T[]> RaceAsync<T>(Func<Wasla.Infrastructure.Services.BillingSubscriptionStore, Task<T>> write) =>
        await RaceAsync((store, _) => write(store));

    private async Task<T[]> RaceAsync<T>(Func<Wasla.Infrastructure.Services.BillingSubscriptionStore, int, Task<T>> write)
    {
        var contexts = Enumerable.Range(0, Writers).Select(i => _db.Open("Writer" + i)).ToArray();
        try
        {
            // Open every connection first so the barrier releases writers, not connection setup.
            foreach (var context in contexts)
                await context.Database.OpenConnectionAsync(Ct);

            using var barrier = new Barrier(Writers);
            var tasks = contexts.Select((context, i) => Task.Run(async () =>
            {
                barrier.SignalAndWait(Ct);
                return await write(_db.Store(context), i);
            }, Ct));
            return await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var context in contexts)
                await context.DisposeAsync();
        }
    }

    private async Task<Guid> SeedRegistrationAsync()
    {
        await using var db = _db.Open("Setup");
        var registration = BillingTestData.Registration();
        db.PendingRegistrations.Add(registration);
        await db.SaveChangesAsync(Ct);
        return registration.Id;
    }

    private async Task<Guid> StartBoundAsync()
    {
        var registration = await SeedRegistrationAsync();
        await using var db = _db.Open("Setup");
        var store = _db.Store(db);
        var id = (await store.StartForRegistrationAsync(registration, BillingTestData.Sandbox, Ct)).SubscriptionId!.Value;
        Assert.Equal(BillingStoreOutcome.Applied, await store.BindProviderReferencesAsync(id, BillingTestData.Sandbox, "sub-" + id.ToString("N"), null, Ct));
        return id;
    }

    private async Task<int> RevisionAsync(Guid id)
    {
        await using var db = _db.Open();
        return await db.BillingSubscriptions.Where(s => s.Id == id).Select(s => s.Revision).SingleAsync(Ct);
    }

    private string InsertSql(
        Guid registration,
        int status = (int)SubscriptionStatus.Pending,
        string amount = "123.45",
        int environment = 1,
        Guid? planPriceId = null) =>
        "INSERT INTO [BillingSubscriptions] ([Id],[PendingRegistrationId],[Provider],[Environment],[PlanPriceId],[PlanCode],[BillingPeriod],[Amount],[Currency],[Status],[StatusObservedAtUtc],[CreatedAtUtc],[UpdatedAtUtc],[Revision]) " +
        $"VALUES (NEWID(), '{registration}', 1, {environment}, '{planPriceId ?? _db.PlanPrice.Id}', 'Pro', 'Monthly', {amount}, 'TRY', {status}, '2026-10-22', '2026-10-22', '2026-10-22', 1)";

    private static string InsertPlanPriceSql(string amount, string period = "Monthly", string effectiveFrom = "2027-01-01") =>
        "INSERT INTO [BillingPlanPrices] ([Id],[PlanCode],[BillingPeriod],[Amount],[Currency],[VatIncluded],[EffectiveFromUtc],[CreatedByCentralAdminUserId],[CreatedAtUtc]) " +
        $"VALUES (NEWID(), 'Pro', '{period}', {amount}, 'TRY', 1, '{effectiveFrom}', NEWID(), '2026-10-22')";

    private static async Task AssertRefusedAsync(DbContext db, string sql, int sqlErrorNumber)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));
        Assert.Equal(sqlErrorNumber, error.Number);
    }
}

/// <summary>
/// The migration on a real SQL Server (WAS-147): it applies over every earlier central migration, its rollback refuses
/// while billing rows exist and changes nothing, and with no rows it rolls back and forward cleanly.
/// </summary>
public sealed class BillingMigrationSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private readonly SqlServerCentralDatabase _db;

    public BillingMigrationSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rollback_RefusesWhileBillingRowsExist_AndWorksOnceTheyAreGone()
    {
        await using var db = _db.Open("Setup");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        var migrations = db.Database.GetMigrations().ToList();
        var billing = migrations.Single(m => m.EndsWith("_AddBillingSubscriptions", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(billing) - 1];
        var migrator = db.GetService<IMigrator>();

        var registration = BillingTestData.Registration();
        db.PendingRegistrations.Add(registration);
        await db.SaveChangesAsync(Ct);
        var started = await _db.Store(db).StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);

        var refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, Ct));
        Assert.Equal(50147, refused.Number);
        Assert.Equal(1, await db.BillingSubscriptions.CountAsync(Ct));
        Assert.Contains(billing, await db.Database.GetAppliedMigrationsAsync(Ct));

        await db.BillingSubscriptions.ExecuteDeleteAsync(Ct);
        refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, Ct));
        Assert.Equal(50147, refused.Number);

        await db.BillingPlanPrices.ExecuteDeleteAsync(Ct);
        await migrator.MigrateAsync(previous, Ct);
        Assert.DoesNotContain(billing, await db.Database.GetAppliedMigrationsAsync(Ct));

        await migrator.MigrateAsync(billing, Ct);
        Assert.False(await db.BillingSubscriptions.AnyAsync(Ct));

        // Later central migrations (AddBillingPlanPriceCorrections) apply on top again.
        await migrator.MigrateAsync(null, Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }
}
