using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// The billing subscription state machine (WAS-147): allowed and refused moves, stale and repeated observations,
/// fail-closed access, set-once provider identifiers and the immutable price snapshot. Pure domain, no database.
/// </summary>
public sealed class SubscriptionStatusTransitionTests
{
    private static readonly DateTime Later = BillingTestData.Now.AddHours(1);

    private static readonly (SubscriptionStatus From, SubscriptionStatus To)[] Allowed =
    [
        (SubscriptionStatus.Pending, SubscriptionStatus.Active),
        (SubscriptionStatus.Pending, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.Pending, SubscriptionStatus.Expired),
        (SubscriptionStatus.Pending, SubscriptionStatus.ReconciliationRequired),
        (SubscriptionStatus.Active, SubscriptionStatus.PastDue),
        (SubscriptionStatus.Active, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.Active, SubscriptionStatus.Expired),
        (SubscriptionStatus.Active, SubscriptionStatus.ReconciliationRequired),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Active),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Expired),
        (SubscriptionStatus.PastDue, SubscriptionStatus.ReconciliationRequired),
        (SubscriptionStatus.Cancelled, SubscriptionStatus.Expired),
        (SubscriptionStatus.ReconciliationRequired, SubscriptionStatus.Active),
        (SubscriptionStatus.ReconciliationRequired, SubscriptionStatus.PastDue),
        (SubscriptionStatus.ReconciliationRequired, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.ReconciliationRequired, SubscriptionStatus.Expired)
    ];

    public static TheoryData<SubscriptionStatus, SubscriptionStatus> AllowedMoves()
    {
        var data = new TheoryData<SubscriptionStatus, SubscriptionStatus>();
        foreach (var (from, to) in Allowed)
            data.Add(from, to);
        return data;
    }

    public static TheoryData<SubscriptionStatus, SubscriptionStatus> RefusedMoves()
    {
        var targets = Enum.GetValues<SubscriptionStatus>().Append((SubscriptionStatus)99);
        var data = new TheoryData<SubscriptionStatus, SubscriptionStatus>();
        foreach (var from in SubscriptionStatusTransitions.BillingStates)
        foreach (var to in targets)
        {
            if (from != to && !Allowed.Contains((from, to)))
                data.Add(from, to);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedMoves))]
    public void AnAllowedMove_WithANewerObservation_IsApplied(SubscriptionStatus from, SubscriptionStatus to)
    {
        var subscription = BillingTestData.SubscriptionIn(from);
        if (subscription.ExternalSubscriptionReference is null)
            subscription.BindProviderReferences("sub-ref-1", null, BillingTestData.Now);
        var revision = subscription.Revision;

        Assert.Equal(BillingChangeResult.Applied, subscription.ApplyProviderStatus(to, Later, Later));

        Assert.Equal(to, subscription.Status);
        Assert.Equal(Later, subscription.StatusObservedAtUtc);
        Assert.Equal(revision + 1, subscription.Revision);
    }

    [Theory]
    [MemberData(nameof(RefusedMoves))]
    public void ARefusedMove_ChangesNothing(SubscriptionStatus from, SubscriptionStatus to)
    {
        var subscription = BillingTestData.SubscriptionIn(from);
        var revision = subscription.Revision;
        var observed = subscription.StatusObservedAtUtc;

        Assert.Equal(BillingChangeResult.InvalidTransition, subscription.ApplyProviderStatus(to, Later, Later));

        Assert.Equal(from, subscription.Status);
        Assert.Equal(observed, subscription.StatusObservedAtUtc);
        Assert.Equal(revision, subscription.Revision);
    }

    [Fact]
    public void TheTable_HoldsExactlyTheRoadmapStates_AndExpiredIsTerminal()
    {
        Assert.Equal(
            new[]
            {
                SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled,
                SubscriptionStatus.Expired, SubscriptionStatus.Pending, SubscriptionStatus.ReconciliationRequired
            }.Order(),
            SubscriptionStatusTransitions.BillingStates.Order());
        Assert.False(SubscriptionStatusTransitions.IsBillingState(SubscriptionStatus.None));
        Assert.False(SubscriptionStatusTransitions.IsBillingState(SubscriptionStatus.Trialing));
        Assert.All(Enum.GetValues<SubscriptionStatus>(), to =>
            Assert.False(SubscriptionStatusTransitions.IsAllowed(SubscriptionStatus.Expired, to)));
    }

    [Fact]
    public void ANewSubscription_IsPending_WithTheSnapshotAndNoProviderIdentifiers()
    {
        var registrationId = Guid.NewGuid();
        var subscription = BillingSubscription.StartPending(
            registrationId,
            BillingProvider.Iyzico,
            BillingEnvironment.Production,
            "ProPlus",
            BillingSubscription.YearlyPeriod,
            new BillingPrice(14990.50m, "TRY"),
            BillingTestData.Now);

        Assert.Equal(SubscriptionStatus.Pending, subscription.Status);
        Assert.Equal(registrationId, subscription.PendingRegistrationId);
        Assert.Null(subscription.TenantId);
        Assert.Equal(BillingEnvironment.Production, subscription.Environment);
        Assert.Equal("ProPlus", subscription.PlanCode);
        Assert.Equal(BillingSubscription.YearlyPeriod, subscription.BillingPeriod);
        Assert.Equal(new BillingPrice(14990.50m, "TRY"), subscription.Price);
        Assert.Null(subscription.ExternalSubscriptionReference);
        Assert.Null(subscription.CurrentPeriodEndUtc);
        Assert.Equal(1, subscription.Revision);
    }

    [Fact]
    public void AnOlderObservation_IsStale_EvenAfterTheSameStateWasSeenAgainLater()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Active, BillingTestData.Now.AddMinutes(1));
        var t3 = BillingTestData.Now.AddMinutes(30);

        // Active seen again at T3, then a PastDue observed at T2 arrives late: it must not win.
        Assert.Equal(BillingChangeResult.Applied, subscription.ApplyProviderStatus(SubscriptionStatus.Active, t3, t3));
        Assert.Equal(
            BillingChangeResult.Stale,
            subscription.ApplyProviderStatus(SubscriptionStatus.PastDue, BillingTestData.Now.AddMinutes(10), t3));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(t3, subscription.StatusObservedAtUtc);
    }

    [Fact]
    public void ARepeatedObservation_IsUnchanged_AndADifferentStateAtTheSameInstantIsStale()
    {
        var at = BillingTestData.Now.AddMinutes(1);
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Active, at);
        var revision = subscription.Revision;

        Assert.Equal(BillingChangeResult.Unchanged, subscription.ApplyProviderStatus(SubscriptionStatus.Active, at, Later));
        Assert.Equal(BillingChangeResult.Stale, subscription.ApplyProviderStatus(SubscriptionStatus.Expired, at, Later));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(revision, subscription.Revision);
    }

    [Fact]
    public void AnExpiredSubscription_IsNeverReactivated_ByALateActiveObservation()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Expired);

        Assert.Equal(BillingChangeResult.InvalidTransition, subscription.ApplyProviderStatus(SubscriptionStatus.Active, Later, Later));
        Assert.Equal(BillingChangeResult.InvalidTransition, subscription.ApplyProviderStatus(SubscriptionStatus.Pending, Later, Later));
        Assert.Equal(SubscriptionStatus.Expired, subscription.Status);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    public void AVerifiedState_NeedsAProviderReference(SubscriptionStatus target)
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Pending);
        Assert.Equal(
            BillingChangeResult.Applied,
            subscription.ApplyProviderStatus(SubscriptionStatus.ReconciliationRequired, BillingTestData.Now.AddMinutes(1), Later));

        Assert.Equal(BillingChangeResult.InvalidTransition, subscription.ApplyProviderStatus(target, Later, Later));
        Assert.Equal(SubscriptionStatus.ReconciliationRequired, subscription.Status);
    }

    [Fact]
    public void OnlyAnActiveSubscriptionWithACurrentVerifiedPeriod_IsVerifiedActive()
    {
        var periodStart = BillingTestData.Now.AddDays(-1);
        foreach (var status in SubscriptionStatusTransitions.BillingStates)
        {
            var subscription = BillingTestData.SubscriptionIn(status);
            subscription.RecordVerifiedPayment("pay-1", BillingTestData.MonthlyPrice, periodStart, periodStart.AddMonths(1), periodStart, BillingTestData.Now);

            Assert.Equal(status == SubscriptionStatus.Active, subscription.IsVerifiedActive(BillingTestData.Now));
        }

        var unpaid = BillingTestData.SubscriptionIn(SubscriptionStatus.Active);
        Assert.False(unpaid.IsVerifiedActive(BillingTestData.Now));

        var lapsed = BillingTestData.SubscriptionIn(SubscriptionStatus.Active);
        lapsed.RecordVerifiedPayment("pay-2", BillingTestData.MonthlyPrice, periodStart, periodStart.AddDays(1), periodStart, BillingTestData.Now);
        Assert.False(lapsed.IsVerifiedActive(periodStart.AddDays(1)));
    }

    [Fact]
    public void AnUnknownProviderState_FailsClosed()
    {
        foreach (var unknown in new[] { (SubscriptionStatus)99, SubscriptionStatus.None, SubscriptionStatus.Trialing })
        {
            var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Pending);
            Assert.Equal(BillingChangeResult.InvalidTransition, subscription.ApplyProviderStatus(unknown, Later, Later));
            Assert.Equal(SubscriptionStatus.Pending, subscription.Status);
            Assert.False(subscription.IsVerifiedActive(Later));
        }

        // An adapter maps what it cannot recognise to ReconciliationRequired, which grants nothing even with a paid period.
        var reconciling = BillingTestData.SubscriptionIn(SubscriptionStatus.Active);
        reconciling.RecordVerifiedPayment("pay-1", BillingTestData.MonthlyPrice, BillingTestData.Now, BillingTestData.Now.AddMonths(1), BillingTestData.Now, BillingTestData.Now);
        Assert.Equal(BillingChangeResult.Applied, reconciling.ApplyProviderStatus(SubscriptionStatus.ReconciliationRequired, Later, Later));
        Assert.False(reconciling.IsVerifiedActive(Later));
    }

    [Fact]
    public void ProviderReferences_AreSetOnce()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Pending);

        Assert.Equal(BillingChangeResult.Applied, subscription.BindProviderReferences("sub-A", null, Later));
        Assert.Equal(BillingChangeResult.Unchanged, subscription.BindProviderReferences("sub-A", null, Later));
        Assert.Equal(BillingChangeResult.Applied, subscription.BindProviderReferences("sub-A", "subscriber-A", Later));
        Assert.Equal(BillingChangeResult.Conflict, subscription.BindProviderReferences("sub-B", "subscriber-A", Later));
        Assert.Equal(BillingChangeResult.Conflict, subscription.BindProviderReferences("sub-A", "subscriber-B", Later));
        Assert.Equal(BillingChangeResult.Unchanged, subscription.BindProviderReferences("sub-A", null, Later));

        Assert.Equal("sub-A", subscription.ExternalSubscriptionReference);
        Assert.Equal("subscriber-A", subscription.ExternalSubscriberReference);
        Assert.Throws<ArgumentException>(() => subscription.BindProviderReferences(" sub-A", null, Later));
        Assert.Throws<ArgumentException>(() => subscription.BindProviderReferences(new string('x', 101), null, Later));
    }

    [Fact]
    public void TheTenant_IsLinkedOnce()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Active);
        var tenantA = Guid.NewGuid();

        Assert.Equal(BillingChangeResult.Applied, subscription.LinkTenant(tenantA, Later));
        Assert.Equal(BillingChangeResult.Unchanged, subscription.LinkTenant(tenantA, Later));
        Assert.Equal(BillingChangeResult.Conflict, subscription.LinkTenant(Guid.NewGuid(), Later));
        Assert.Equal(tenantA, subscription.TenantId);
    }

    [Fact]
    public void AVerifiedPayment_MustMatchThePriceSnapshot_AndNeverMovesThePeriodBack()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Active);
        var october = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            BillingChangeResult.Conflict,
            subscription.RecordVerifiedPayment("pay-x", new BillingPrice(1099.00m, "TRY"), october, october.AddMonths(1), october, Later).Result);
        Assert.Equal(
            BillingChangeResult.Conflict,
            subscription.RecordVerifiedPayment("pay-x", new BillingPrice(999.00m, "USD"), october, october.AddMonths(1), october, Later).Result);

        var (result, payment) = subscription.RecordVerifiedPayment("pay-nov", BillingTestData.MonthlyPrice, october.AddMonths(1), october.AddMonths(2), october, Later);
        Assert.Equal(BillingChangeResult.Applied, result);
        Assert.NotNull(payment);
        Assert.Equal(subscription.Id, payment!.SubscriptionId);
        Assert.Equal(subscription.Environment, payment.Environment);

        // October reported after November: stored, but the current period stays November.
        Assert.Equal(BillingChangeResult.Applied, subscription.RecordVerifiedPayment("pay-oct", BillingTestData.MonthlyPrice, october, october.AddMonths(1), october, Later).Result);
        Assert.Equal(october.AddMonths(1), subscription.CurrentPeriodStartUtc);
        Assert.Equal(october.AddMonths(2), subscription.CurrentPeriodEndUtc);
    }

    [Fact]
    public void APendingSubscriptionWithoutAProviderReference_CannotRecordAPayment()
    {
        var subscription = BillingTestData.SubscriptionIn(SubscriptionStatus.Pending);

        var (result, payment) = subscription.RecordVerifiedPayment("pay-1", BillingTestData.MonthlyPrice, BillingTestData.Now, BillingTestData.Now.AddMonths(1), BillingTestData.Now, Later);

        Assert.Equal(BillingChangeResult.InvalidTransition, result);
        Assert.Null(payment);
        Assert.Null(subscription.CurrentPeriodEndUtc);
    }

    [Theory]
    [InlineData(0, "TRY")]
    [InlineData(-1, "TRY")]
    [InlineData(10.005, "TRY")]
    [InlineData(10, "try")]
    [InlineData(10, "TR")]
    [InlineData(10, "TRYX")]
    [InlineData(10, "T1Y")]
    public void AnInvalidPrice_IsRefused(double amount, string currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BillingPrice((decimal)amount, currency));
    }

    [Fact]
    public void ASubscription_StartsOnlyWithAValidSnapshot()
    {
        BillingSubscription Start(string period = BillingSubscription.MonthlyPeriod, string plan = "Pro", BillingPrice? price = null) =>
            BillingSubscription.StartPending(Guid.NewGuid(), BillingProvider.Iyzico, BillingEnvironment.Sandbox, plan, period, price ?? BillingTestData.MonthlyPrice, BillingTestData.Now);

        Assert.Throws<ArgumentException>(() => Start(period: "Weekly"));
        Assert.Throws<ArgumentException>(() => Start(period: "monthly"));
        Assert.Throws<ArgumentException>(() => Start(plan: " "));
        Assert.Throws<ArgumentException>(() => Start(price: default(BillingPrice)));
        Assert.Throws<ArgumentException>(() => BillingSubscription.StartPending(Guid.Empty, BillingProvider.Iyzico, BillingEnvironment.Sandbox, "Pro", BillingSubscription.MonthlyPeriod, BillingTestData.MonthlyPrice, BillingTestData.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillingSubscription.StartPending(Guid.NewGuid(), BillingProvider.Iyzico, (BillingEnvironment)0, "Pro", BillingSubscription.MonthlyPeriod, BillingTestData.MonthlyPrice, BillingTestData.Now));
    }
}
