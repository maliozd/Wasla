using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Another request (a second tab, a double submit) can change a registration between this request's
/// read and its write. The later write must apply only to the status it read, so it never overwrites
/// a payment, a failure, or a cancellation that landed in between.
/// </summary>
public sealed class PendingRegistrationTransitionRaceTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Cancel_WhenAPaymentLandsAfterItsRead_KeepsThePaidRegistrationAndItsSlug()
    {
        var seeded = await _harness.SeedAsync();
        var service = _harness.CreateService(AfterFirstRead(() => _harness.CreateService()
            .SimulatePaymentSuccessAsync(seeded.Id, CancellationToken.None).GetAwaiter().GetResult()));

        var result = await service.CancelRegistrationAsync(seeded.Id, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutSimulationOutcome.AlreadyPaymentSucceeded, result.Outcome);
        var stored = await _harness.ReloadAsync(seeded.Id);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, stored.Status);
        Assert.False(await _harness.IsSlugAvailableAsync(seeded.Slug));
    }

    [Fact]
    public async Task SimulateFailed_WhenAPaymentLandsAfterItsRead_DoesNotMarkThePaidRegistrationFailed()
    {
        var seeded = await _harness.SeedAsync();
        var service = _harness.CreateService(AfterFirstRead(() => _harness.CreateService()
            .SimulatePaymentSuccessAsync(seeded.Id, CancellationToken.None).GetAwaiter().GetResult()));

        var result = await service.SimulatePaymentFailedAsync(seeded.Id, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutSimulationOutcome.AlreadyPaymentSucceeded, result.Outcome);
        var stored = await _harness.ReloadAsync(seeded.Id);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, stored.Status);
        Assert.NotNull(stored.PaymentSucceededAtUtc);
        Assert.Null(stored.PaymentFailedAtUtc);
    }

    [Fact]
    public async Task SimulateSuccess_SubmittedTwice_KeepsTheFirstPaymentReference()
    {
        var seeded = await _harness.SeedAsync();
        string? firstReference = null;
        var service = _harness.CreateService(AfterFirstRead(() => firstReference = _harness.CreateService()
            .SimulatePaymentSuccessAsync(seeded.Id, CancellationToken.None).GetAwaiter().GetResult()
            .SimulatedPaymentReference));

        var result = await service.SimulatePaymentSuccessAsync(seeded.Id, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutSimulationOutcome.AlreadyPaymentSucceeded, result.Outcome);
        var stored = await _harness.ReloadAsync(seeded.Id);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, stored.Status);
        Assert.NotNull(firstReference);
        Assert.Equal(firstReference, stored.SimulatedPaymentReference);
    }

    [Fact]
    public async Task SimulateSuccess_WhenTheApplicantCancelsAfterItsRead_LeavesTheRegistrationCancelled()
    {
        var seeded = await _harness.SeedAsync();
        var service = _harness.CreateService(AfterFirstRead(() => _harness.CreateService()
            .CancelRegistrationAsync(seeded.Id, CancellationToken.None).GetAwaiter().GetResult()));

        var result = await service.SimulatePaymentSuccessAsync(seeded.Id, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutSimulationOutcome.AlreadyCancelled, result.Outcome);
        var stored = await _harness.ReloadAsync(seeded.Id);
        Assert.Equal(PendingRegistrationStatus.Cancelled, stored.Status);
        Assert.Null(stored.PaymentSucceededAtUtc);
        Assert.Null(stored.SimulatedPaymentReference);
    }

    private static ChangeAfterFirstRead AfterFirstRead(Action concurrentChange) => new(concurrentChange);

    /// <summary>
    /// Runs the concurrent change once, right after the service has read the registration and before
    /// it writes, which is the window a second request can hit.
    /// </summary>
    private sealed class ChangeAfterFirstRead(Action concurrentChange) : DbCommandInterceptor
    {
        private bool _done;

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (!_done)
            {
                _done = true;
                concurrentChange();
            }

            return result;
        }
    }
}
