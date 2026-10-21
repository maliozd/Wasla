using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// <see cref="Wasla.Infrastructure.Services.BillingSubscriptionStore"/> over a CentralDb built from the model
/// (WAS-147): repeated and conflicting commands, provider identifiers per environment, tenant isolation, immutable
/// snapshots and the optimistic-concurrency retry. SQLite proves the logic and the indexes, not SQL Server locking.
/// </summary>
public sealed class BillingSubscriptionStoreTests : IDisposable
{
    private readonly SqliteCentralDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Start_SnapshotsTheRegistrationsPlanAndPeriod_WithThePrice()
    {
        var registration = await _db.SeedRegistrationAsync();

        var started = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct);

        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        var view = await _db.Store().GetForRegistrationAsync(registration.Id, Ct);
        Assert.NotNull(view);
        Assert.Equal(started.SubscriptionId, view!.Id);
        Assert.Equal(SubscriptionStatus.Pending, view.Status);
        Assert.Equal(registration.PlanCode, view.PlanCode);
        Assert.Equal(registration.BillingPeriod, view.BillingPeriod);
        Assert.Equal(BillingTestData.MonthlyPrice, view.Price);
        Assert.Equal(BillingTestData.Sandbox, view.Scope);
        Assert.Null(view.TenantId);
        Assert.False(view.IsVerifiedActive(BillingTestData.Now));
    }

    [Fact]
    public async Task Start_Repeated_ReturnsTheSameSubscription_AndAChangedPriceOrEnvironmentConflicts()
    {
        var registration = await _db.SeedRegistrationAsync();
        var first = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct);

        var again = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct);
        var repriced = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, new BillingPrice(499m, "TRY"), Ct);
        var production = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Production, BillingTestData.MonthlyPrice, Ct);

        Assert.Equal(new BillingStartResult(BillingStoreOutcome.Unchanged, first.SubscriptionId), again);
        Assert.Equal(new BillingStartResult(BillingStoreOutcome.Conflict, first.SubscriptionId), repriced);
        Assert.Equal(new BillingStartResult(BillingStoreOutcome.Conflict, first.SubscriptionId), production);
        await using var check = _db.Open();
        var stored = await check.BillingSubscriptions.SingleAsync(Ct);
        Assert.Equal(BillingTestData.MonthlyPrice, stored.Price);
        Assert.Equal(1, stored.Revision);
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    [InlineData(PendingRegistrationStatus.Expired)]
    [InlineData(PendingRegistrationStatus.Draft)]
    public async Task Start_ForARegistrationThatDoesNotAwaitPayment_IsNotEligible(PendingRegistrationStatus status)
    {
        var registration = await _db.SeedRegistrationAsync(status);

        var started = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct);

        Assert.Equal(new BillingStartResult(BillingStoreOutcome.NotEligible, null), started);
        await using var check = _db.Open();
        Assert.False(await check.BillingSubscriptions.AnyAsync(Ct));
    }

    [Fact]
    public async Task Start_ForAnExpiredOrUnknownRegistration_WritesNothing()
    {
        var registration = await _db.SeedRegistrationAsync();
        _db.Time.UtcNow = registration.ExpiresAtUtc!.Value;

        Assert.Equal(BillingStoreOutcome.NotEligible, (await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).Outcome);
        Assert.Equal(BillingStoreOutcome.NotFound, (await _db.Store().StartForRegistrationAsync(Guid.NewGuid(), BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).Outcome);
        await using var check = _db.Open();
        Assert.False(await check.BillingSubscriptions.AnyAsync(Ct));
    }

    [Fact]
    public async Task AProviderSubscriptionReference_BelongsToOneSubscriptionPerEnvironment()
    {
        var a = await StartAsync(BillingTestData.Sandbox);
        var b = await StartAsync(BillingTestData.Sandbox);

        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().BindProviderReferencesAsync(a, BillingTestData.Sandbox, "sub-shared", null, Ct));
        Assert.Equal(BillingStoreOutcome.Unchanged, await _db.Store().BindProviderReferencesAsync(a, BillingTestData.Sandbox, "sub-shared", null, Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().BindProviderReferencesAsync(b, BillingTestData.Sandbox, "sub-shared", null, Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().BindProviderReferencesAsync(a, BillingTestData.Sandbox, "sub-other", null, Ct));

        await using var check = _db.Open();
        Assert.Null((await check.BillingSubscriptions.SingleAsync(s => s.Id == b, Ct)).ExternalSubscriptionReference);
    }

    [Fact]
    public async Task TheUniqueIndex_RefusesADuplicateProviderReference_ThatBypassesTheStore()
    {
        var a = await StartAsync(BillingTestData.Sandbox);
        var b = await StartAsync(BillingTestData.Sandbox);
        await _db.Store().BindProviderReferencesAsync(a, BillingTestData.Sandbox, "sub-shared", null, Ct);

        await using var db = _db.Open();
        var other = await db.BillingSubscriptions.SingleAsync(s => s.Id == b, Ct);
        other.BindProviderReferences("sub-shared", null, BillingTestData.Now);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task SandboxAndProduction_KeepTheirOwnIdentifiers_AndNeverWriteEachOther()
    {
        var sandbox = await StartAsync(BillingTestData.Sandbox);
        var production = await StartAsync(BillingTestData.Production);

        // The same provider reference string in both environments names two different subscriptions.
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().BindProviderReferencesAsync(sandbox, BillingTestData.Sandbox, "sub-1", null, Ct));
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().BindProviderReferencesAsync(production, BillingTestData.Production, "sub-1", null, Ct));
        Assert.Equal(sandbox, await _db.Store().FindByProviderReferenceAsync(BillingTestData.Sandbox, "sub-1", Ct));
        Assert.Equal(production, await _db.Store().FindByProviderReferenceAsync(BillingTestData.Production, "sub-1", Ct));

        // A sandbox-verified write never touches the production subscription.
        var later = BillingTestData.Now.AddMinutes(5);
        Assert.Equal(BillingStoreOutcome.ScopeMismatch, await _db.Store().ApplyProviderStatusAsync(production, BillingTestData.Sandbox, SubscriptionStatus.Active, later, Ct));
        Assert.Equal(BillingStoreOutcome.ScopeMismatch, await _db.Store().RecordVerifiedPaymentAsync(production, BillingTestData.Sandbox, BillingTestData.Payment("pay-1", later), Ct));
        Assert.Equal(BillingStoreOutcome.ScopeMismatch, await _db.Store().BindProviderReferencesAsync(production, BillingTestData.Sandbox, "sub-1", "subscriber-1", Ct));

        // Payment references are unique per environment too.
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().RecordVerifiedPaymentAsync(sandbox, BillingTestData.Sandbox, BillingTestData.Payment("pay-1", later), Ct));
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().RecordVerifiedPaymentAsync(production, BillingTestData.Production, BillingTestData.Payment("pay-1", later), Ct));

        await using var check = _db.Open();
        var stored = await check.BillingSubscriptions.SingleAsync(s => s.Id == production, Ct);
        Assert.Equal(SubscriptionStatus.Pending, stored.Status);
        Assert.Null(stored.ExternalSubscriberReference);
        Assert.Equal(2, await check.BillingPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AVerifiedPayment_RecordedAgain_IsUnchanged_AndAContradictingOneConflicts()
    {
        var a = await StartBoundAsync("sub-a");
        var b = await StartBoundAsync("sub-b");
        var october = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var payment = BillingTestData.Payment("pay-oct", october);

        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().RecordVerifiedPaymentAsync(a, BillingTestData.Sandbox, payment, Ct));
        Assert.Equal(BillingStoreOutcome.Unchanged, await _db.Store().RecordVerifiedPaymentAsync(a, BillingTestData.Sandbox, payment, Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().RecordVerifiedPaymentAsync(a, BillingTestData.Sandbox, payment with { PeriodEndUtc = october.AddMonths(2) }, Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().RecordVerifiedPaymentAsync(b, BillingTestData.Sandbox, payment, Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().RecordVerifiedPaymentAsync(a, BillingTestData.Sandbox, BillingTestData.Payment("pay-oct-2", october), Ct));
        Assert.Equal(BillingStoreOutcome.Conflict, await _db.Store().RecordVerifiedPaymentAsync(a, BillingTestData.Sandbox, BillingTestData.Payment("pay-nov", october.AddMonths(1), new BillingPrice(1999m, "TRY")), Ct));

        await using var check = _db.Open();
        var stored = await check.BillingPayments.SingleAsync(Ct);
        Assert.Equal(a, stored.SubscriptionId);
        Assert.Equal(october.AddMonths(1), (await check.BillingSubscriptions.SingleAsync(s => s.Id == a, Ct)).CurrentPeriodEndUtc);
        Assert.Null((await check.BillingSubscriptions.SingleAsync(s => s.Id == b, Ct)).CurrentPeriodEndUtc);
    }

    [Fact]
    public async Task ARepeatedStatusCallback_DoesNotRaiseTheRevision()
    {
        var id = await StartBoundAsync("sub-a");
        var at = BillingTestData.Now.AddMinutes(1);

        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Active, at, Ct));
        var revision = await RevisionAsync(id);
        Assert.Equal(BillingStoreOutcome.Unchanged, await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Active, at, Ct));
        Assert.Equal(BillingStoreOutcome.Stale, await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.PastDue, at.AddSeconds(-1), Ct));
        Assert.Equal(BillingStoreOutcome.InvalidTransition, await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Pending, at.AddMinutes(1), Ct));

        Assert.Equal(revision, await RevisionAsync(id));
        Assert.Equal(BillingStoreOutcome.NotFound, await _db.Store().ApplyProviderStatusAsync(Guid.NewGuid(), BillingTestData.Sandbox, SubscriptionStatus.Active, at, Ct));
    }

    [Fact]
    public async Task AConcurrentNewerWrite_WinsOverAnOlderOne_ThatReadBeforeIt()
    {
        var id = await StartBoundAsync("sub-a");
        var t1 = BillingTestData.Now.AddMinutes(1);
        await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Active, t1, Ct);

        // The PastDue writer (observed at T2) reads the subscription; before it saves, an Expired observed at T3 commits.
        var slow = _db.Store(AfterFirstBillingRead(() => _db.Store()
            .ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Expired, t1.AddMinutes(2), CancellationToken.None)
            .GetAwaiter().GetResult()));

        var outcome = await slow.ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.PastDue, t1.AddMinutes(1), Ct);

        Assert.Equal(BillingStoreOutcome.Stale, outcome);
        await using var check = _db.Open();
        var stored = await check.BillingSubscriptions.SingleAsync(Ct);
        Assert.Equal(SubscriptionStatus.Expired, stored.Status);
        Assert.Equal(t1.AddMinutes(2), stored.StatusObservedAtUtc);
    }

    [Fact]
    public async Task AConcurrentIdenticalPayment_IsRecordedOnce()
    {
        var id = await StartBoundAsync("sub-a");
        var payment = BillingTestData.Payment("pay-oct", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var slow = _db.Store(AfterFirstBillingRead(() => _db.Store()
            .RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, payment, CancellationToken.None)
            .GetAwaiter().GetResult()));

        Assert.Equal(BillingStoreOutcome.Unchanged, await slow.RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, payment, Ct));

        await using var check = _db.Open();
        Assert.Equal(1, await check.BillingPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task TheRevision_IsAnOptimisticConcurrencyToken()
    {
        var id = await StartBoundAsync("sub-a");
        await using var first = _db.Open();
        await using var second = _db.Open();
        var a = await first.BillingSubscriptions.SingleAsync(s => s.Id == id, Ct);
        var b = await second.BillingSubscriptions.SingleAsync(s => s.Id == id, Ct);

        a.ApplyProviderStatus(SubscriptionStatus.Active, BillingTestData.Now.AddMinutes(1), BillingTestData.Now);
        await first.SaveChangesAsync(Ct);
        b.ApplyProviderStatus(SubscriptionStatus.Cancelled, BillingTestData.Now.AddMinutes(2), BillingTestData.Now);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ThePriceSnapshot_CannotBeChangedThroughEf()
    {
        var id = await StartBoundAsync("sub-a");
        await _db.Store().RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, BillingTestData.Payment("pay-1", BillingTestData.Now), Ct);
        await using var db = _db.Open();

        var subscription = await db.BillingSubscriptions.SingleAsync(Ct);
        foreach (var (property, value) in new (string, object)[]
                 {
                     (nameof(BillingSubscription.Amount), 1m),
                     (nameof(BillingSubscription.Currency), "USD"),
                     (nameof(BillingSubscription.PlanCode), "Starter"),
                     (nameof(BillingSubscription.BillingPeriod), BillingSubscription.YearlyPeriod),
                     (nameof(BillingSubscription.PendingRegistrationId), Guid.NewGuid())
                 })
        {
            var entry = db.Entry(subscription).Property(property);
            var original = entry.CurrentValue;
            entry.CurrentValue = value;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            entry.CurrentValue = original;
            entry.IsModified = false;
        }

        var payment = await db.BillingPayments.SingleAsync(Ct);
        db.Entry(payment).Property(nameof(BillingPayment.Amount)).CurrentValue = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        await using var check = _db.Open();
        Assert.Equal(BillingTestData.MonthlyPrice, (await check.BillingSubscriptions.SingleAsync(Ct)).Price);
        Assert.Equal(BillingTestData.MonthlyPrice, (await check.BillingPayments.SingleAsync(Ct)).Price);
    }

    [Fact]
    public async Task TenantA_NeverReadsOrLinksTenantBsSubscription()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var registrationA = await _db.SeedRegistrationAsync(PendingRegistrationStatus.AwaitingPayment);
        var registrationB = await _db.SeedRegistrationAsync(PendingRegistrationStatus.AwaitingPayment);
        var a = (await _db.Store().StartForRegistrationAsync(registrationA.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).SubscriptionId!.Value;
        var b = (await _db.Store().StartForRegistrationAsync(registrationB.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).SubscriptionId!.Value;
        await ProvisionAsync(registrationA.Id, tenantA);
        await ProvisionAsync(registrationB.Id, tenantB);

        // Before linking, a tenant sees nothing; B's subscription can never be linked to A.
        Assert.Null(await _db.Store().GetForTenantAsync(tenantA, Ct));
        Assert.Equal(BillingStoreOutcome.NotEligible, await _db.Store().LinkTenantAsync(b, tenantA, Ct));
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().LinkTenantAsync(a, tenantA, Ct));
        Assert.Equal(BillingStoreOutcome.Unchanged, await _db.Store().LinkTenantAsync(a, tenantA, Ct));
        Assert.Equal(BillingStoreOutcome.NotEligible, await _db.Store().LinkTenantAsync(a, tenantB, Ct));
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().LinkTenantAsync(b, tenantB, Ct));

        var seenByA = await _db.Store().GetForTenantAsync(tenantA, Ct);
        var seenByB = await _db.Store().GetForTenantAsync(tenantB, Ct);
        Assert.Equal(a, seenByA!.Id);
        Assert.Equal(b, seenByB!.Id);
        Assert.Null(await _db.Store().GetForTenantAsync(Guid.Empty, Ct));
        Assert.Null(await _db.Store().GetForTenantAsync(Guid.NewGuid(), Ct));

        // A provider write for A's subscription leaves B's untouched.
        await _db.Store().BindProviderReferencesAsync(a, BillingTestData.Sandbox, "sub-a", null, Ct);
        await _db.Store().ApplyProviderStatusAsync(a, BillingTestData.Sandbox, SubscriptionStatus.Active, BillingTestData.Now.AddMinutes(1), Ct);
        Assert.Equal(SubscriptionStatus.Pending, (await _db.Store().GetForTenantAsync(tenantB, Ct))!.Status);
    }

    [Fact]
    public async Task LinkTenant_RequiresTheProvisionedRegistrationToNameThatTenant()
    {
        var registration = await _db.SeedRegistrationAsync();
        var id = (await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).SubscriptionId!.Value;
        var tenant = Guid.NewGuid();

        // Not provisioned yet.
        await using (var db = _db.Open())
        {
            db.Tenants.Add(BillingTestData.Tenant(tenant));
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(BillingStoreOutcome.NotEligible, await _db.Store().LinkTenantAsync(id, tenant, Ct));
        Assert.Equal(BillingStoreOutcome.NotEligible, await _db.Store().LinkTenantAsync(id, Guid.Empty, Ct));
        Assert.Equal(BillingStoreOutcome.NotFound, await _db.Store().LinkTenantAsync(Guid.NewGuid(), tenant, Ct));
        Assert.Null((await _db.Store().GetForRegistrationAsync(registration.Id, Ct))!.TenantId);
    }

    [Fact]
    public async Task DeletingATenant_KeepsItsBillingHistory_OwnedByTheRegistration()
    {
        var tenant = Guid.NewGuid();
        var registration = await _db.SeedRegistrationAsync();
        var id = (await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, BillingTestData.MonthlyPrice, Ct)).SubscriptionId!.Value;
        await ProvisionAsync(registration.Id, tenant);
        await _db.Store().LinkTenantAsync(id, tenant, Ct);
        await _db.Store().BindProviderReferencesAsync(id, BillingTestData.Sandbox, "sub-a", null, Ct);
        await _db.Store().RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, BillingTestData.Payment("pay-1", BillingTestData.Now), Ct);

        await using (var db = _db.Open())
        {
            db.Tenants.Remove(await db.Tenants.SingleAsync(t => t.Id == tenant, Ct));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _db.Open();
        var stored = await check.BillingSubscriptions.SingleAsync(Ct);
        Assert.Null(stored.TenantId);
        Assert.Equal(registration.Id, stored.PendingRegistrationId);
        Assert.Equal(1, await check.BillingPayments.CountAsync(Ct));

        // The database refuses to delete the registration that owns billing history.
        await using var delete = _db.Open();
        delete.PendingRegistrations.Remove(await delete.PendingRegistrations.SingleAsync(r => r.Id == registration.Id, Ct));
        await Assert.ThrowsAsync<DbUpdateException>(() => delete.SaveChangesAsync(Ct));
    }

    private async Task<Guid> StartAsync(BillingProviderScope scope)
    {
        var registration = await _db.SeedRegistrationAsync();
        var started = await _db.Store().StartForRegistrationAsync(registration.Id, scope, BillingTestData.MonthlyPrice, Ct);
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        return started.SubscriptionId!.Value;
    }

    private async Task<Guid> StartBoundAsync(string reference)
    {
        var id = await StartAsync(BillingTestData.Sandbox);
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().BindProviderReferencesAsync(id, BillingTestData.Sandbox, reference, null, Ct));
        return id;
    }

    private async Task ProvisionAsync(Guid registrationId, Guid tenantId)
    {
        await using var db = _db.Open();
        db.Tenants.Add(BillingTestData.Tenant(tenantId));
        var registration = await db.PendingRegistrations.SingleAsync(r => r.Id == registrationId, Ct);
        registration.Status = PendingRegistrationStatus.Provisioned;
        registration.TenantId = tenantId;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> RevisionAsync(Guid id)
    {
        await using var db = _db.Open();
        return await db.BillingSubscriptions.Where(s => s.Id == id).Select(s => s.Revision).SingleAsync(Ct);
    }

    /// <summary>
    /// Runs the concurrent change once, right after the store read the subscription and before it saves: the window
    /// a second request, callback or reconciliation pass can hit.
    /// </summary>
    private static ChangeAfterFirstBillingRead AfterFirstBillingRead(Action concurrentChange) => new(concurrentChange);

    private sealed class ChangeAfterFirstBillingRead(Action concurrentChange) : DbCommandInterceptor
    {
        private bool _done;

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (!_done && command.CommandText.Contains("FROM \"BillingSubscriptions\"", StringComparison.Ordinal))
            {
                _done = true;
                concurrentChange();
            }

            return result;
        }
    }
}
