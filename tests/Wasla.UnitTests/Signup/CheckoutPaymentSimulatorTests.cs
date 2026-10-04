using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Checkout;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The checkout payment simulator marks a registration as paid, which makes it eligible for
/// provisioning. It must work only in Development, and only for the browser that submitted the signup.
/// </summary>
public sealed class CheckoutPaymentSimulatorTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SimulateSuccess_InDevelopment_ForTheApplicant_MarksRegistrationPaid()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(Environments.Development, registration.Id);

        var result = await controller.SimulateSuccess(registration.Id, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CheckoutController.Success), redirect.ActionName);
        var stored = await _harness.ReloadAsync(registration.Id);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, stored.Status);
        Assert.NotNull(stored.PaymentSucceededAtUtc);
        Assert.StartsWith("SIM-", stored.SimulatedPaymentReference);
    }

    [Fact]
    public async Task SimulateFailed_InDevelopment_ForTheApplicant_MarksRegistrationFailed()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(Environments.Development, registration.Id);

        var result = await controller.SimulateFailed(registration.Id, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CheckoutController.Failed), redirect.ActionName);
        var stored = await _harness.ReloadAsync(registration.Id);
        Assert.Equal(PendingRegistrationStatus.PaymentFailed, stored.Status);
        Assert.NotNull(stored.PaymentFailedAtUtc);
    }

    // The applicant's own valid proof is present, so these isolate the environment gate.
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task SimulateSuccess_OutsideDevelopment_ReturnsNotFound_AndLeavesRegistrationUnchanged(string environmentName)
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(environmentName, registration.Id);

        var result = await controller.SimulateSuccess(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task SimulateFailed_OutsideDevelopment_ReturnsNotFound_AndLeavesRegistrationUnchanged(string environmentName)
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(environmentName, registration.Id);

        var result = await controller.SimulateFailed(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task SimulateSuccess_OutsideDevelopment_DoesNotRecoverAPreviouslyFailedPayment()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentFailed);
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var result = await controller.SimulateSuccess(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    // Development alone is not enough: the registration ID is discoverable.
    [Fact]
    public async Task SimulateSuccess_InDevelopment_WithoutProof_ReturnsNotFound_AndLeavesRegistrationUnchanged()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateController(Environments.Development);

        var result = await controller.SimulateSuccess(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task SimulateFailed_InDevelopment_WithoutProof_ReturnsNotFound_AndLeavesRegistrationUnchanged()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateController(Environments.Development);

        var result = await controller.SimulateFailed(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task SimulateSuccess_InDevelopment_WithAnotherRegistrationsProof_ReturnsNotFound_AndLeavesRegistrationUnchanged()
    {
        var mine = await _harness.SeedAsync(slug: "mine");
        var theirs = await _harness.SeedAsync(slug: "theirs");
        var controller = _harness.CreateOwnerController(Environments.Development, mine.Id);

        var result = await controller.SimulateSuccess(theirs.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(theirs);
        await _harness.AssertUnchangedAsync(mine);
    }

    [Fact]
    public async Task Review_InDevelopment_OffersSimulatorToTheApplicant()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(Environments.Development, registration.Id);

        var model = await ReviewModelAsync(controller, registration.Id);

        Assert.True(model.CanSimulatePayment);
    }

    [Fact]
    public async Task Review_OutsideDevelopment_HidesSimulatorFromTheApplicant()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var model = await ReviewModelAsync(controller, registration.Id);

        Assert.False(model.CanSimulatePayment);
    }

    private static async Task<CheckoutReviewViewModel> ReviewModelAsync(CheckoutController controller, Guid id)
    {
        var view = Assert.IsType<ViewResult>(await controller.Review(id, TestContext.Current.CancellationToken));
        return Assert.IsType<CheckoutReviewViewModel>(view.Model);
    }
}
