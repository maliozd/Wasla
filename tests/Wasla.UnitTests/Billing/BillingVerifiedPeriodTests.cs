using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// "Verified active" means an <c>Active</c> subscription with a provider-verified paid period that covers the moment
/// asked about (WAS-147). A paid period in the future, or one on the far side of an unpaid gap, never covers today;
/// an earlier period that still covers today keeps counting after a later one was recorded. Periods are half-open:
/// a period covers its start and ends at its end.
/// </summary>
public sealed class BillingVerifiedPeriodTests : IDisposable
{
    private static readonly DateTime October = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime November = October.AddMonths(1);
    private static readonly DateTime December = October.AddMonths(2);
    private static readonly DateTime January = October.AddMonths(3);

    private readonly SqliteCentralDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task APaidPeriodThatStartsInTheFuture_DoesNotCoverToday()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-nov", November);

        Assert.False(await IsVerifiedActiveAsync(at: October.AddDays(21)));
        Assert.True(await IsVerifiedActiveAsync(at: November.AddDays(3)));
    }

    [Fact]
    public async Task AnUnpaidGapBetweenPaidPeriods_IsNotCovered()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-oct", October);
        await PayAsync(id, "pay-dec", December);

        Assert.True(await IsVerifiedActiveAsync(at: October.AddDays(21)));
        Assert.False(await IsVerifiedActiveAsync(at: November.AddDays(14)));
        Assert.True(await IsVerifiedActiveAsync(at: December.AddDays(14)));
        Assert.False(await IsVerifiedActiveAsync(at: January.AddDays(1)));
    }

    [Fact]
    public async Task TheCurrentPaidPeriod_StillCounts_AfterAFuturePaymentWasRecorded()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-oct", October);
        await PayAsync(id, "pay-nov", November);

        Assert.True(await IsVerifiedActiveAsync(at: October.AddDays(21)));
        Assert.True(await IsVerifiedActiveAsync(at: November.AddDays(21)));
    }

    [Fact]
    public async Task AFuturePeriodRecordedFirst_DoesNotHideTheCurrentOneRecordedLater()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-nov", November);
        await PayAsync(id, "pay-oct", October);

        Assert.True(await IsVerifiedActiveAsync(at: October.AddDays(21)));
    }

    [Fact]
    public async Task AnEndedPeriod_IsNotCovered_AndAnOrdinaryCurrentPeriodIs()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-oct", October);

        Assert.True(await IsVerifiedActiveAsync(at: October));
        Assert.True(await IsVerifiedActiveAsync(at: October.AddDays(21)));
        Assert.False(await IsVerifiedActiveAsync(at: November));
        Assert.False(await IsVerifiedActiveAsync(at: November.AddDays(2)));
    }

    [Fact]
    public async Task ACoveredPeriod_GrantsNothing_UnlessTheSubscriptionIsActive()
    {
        var id = await ActiveSubscriptionAsync();
        await PayAsync(id, "pay-oct", October);
        await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.ReconciliationRequired, BillingTestData.Now.AddMinutes(5), Ct);

        Assert.False(await IsVerifiedActiveAsync(at: October.AddDays(21)));
    }

    private Guid _registrationId;

    /// <summary>Reads the subscription with the clock at <paramref name="at"/> and asks about that same moment.</summary>
    private async Task<bool> IsVerifiedActiveAsync(DateTime at)
    {
        _db.Time.UtcNow = at;
        var view = await _db.Store().GetForRegistrationAsync(_registrationId, Ct);
        return view!.IsVerifiedActive(at);
    }

    private async Task<Guid> ActiveSubscriptionAsync()
    {
        var (id, registrationId) = await BillingTestData.StartAsync(_db, BillingTestData.Sandbox);
        _registrationId = registrationId;
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().BindProviderReferencesAsync(id, BillingTestData.Sandbox, "sub-" + id.ToString("N"), null, Ct));
        Assert.Equal(BillingStoreOutcome.Applied, await _db.Store().ApplyProviderStatusAsync(id, BillingTestData.Sandbox, SubscriptionStatus.Active, BillingTestData.Now.AddMinutes(1), Ct));
        return id;
    }

    private async Task PayAsync(Guid id, string reference, DateTime periodStart) =>
        Assert.Equal(
            BillingStoreOutcome.Applied,
            await _db.Store().RecordVerifiedPaymentAsync(id, BillingTestData.Sandbox, BillingTestData.Payment(reference, periodStart), Ct));
}
