using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Signup;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// The Development checkout simulator (WAS-26/WAS-39) stays as it was and never touches billing records (WAS-147):
/// a simulated payment writes only the registration, and a registration paid by simulation cannot start a provider
/// subscription afterwards.
/// </summary>
public sealed class BillingSimulationIsolationTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ASimulatedPayment_WritesNoBillingRecord_AndCannotStartASubscription()
    {
        var registration = await _harness.SeedAsync();

        var simulated = await _harness.CreateService().SimulatePaymentSuccessAsync(registration.Id, Ct);
        var started = await new BillingSubscriptionStore(_harness.Db, TimeProvider.System, NullLogger<BillingSubscriptionStore>.Instance)
            .StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);

        Assert.Equal(Wasla.Application.Abstractions.Onboarding.Checkout.CheckoutSimulationOutcome.Applied, simulated.Outcome);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, (await _harness.ReloadAsync(registration.Id)).Status);
        Assert.Equal(BillingStoreOutcome.NotEligible, started.Outcome);
        Assert.False(await _harness.Db.BillingSubscriptions.AnyAsync(Ct));
        Assert.False(await _harness.Db.BillingPayments.AnyAsync(Ct));
    }
}
