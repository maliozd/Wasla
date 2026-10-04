using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Checkout;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// A registration ID is not proof of ownership: the pending-tenant redirect reveals it to anyone who
/// opens the tenant address. Cancelling releases the reserved slug, so only the browser holding the
/// signup's ownership proof may cancel, and only where the existing status rules allow it.
/// </summary>
public sealed class CheckoutCancellationTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData("Development", PendingRegistrationStatus.AwaitingPayment)]
    [InlineData("Development", PendingRegistrationStatus.PaymentFailed)]
    [InlineData("Production", PendingRegistrationStatus.AwaitingPayment)]
    [InlineData("Production", PendingRegistrationStatus.PaymentFailed)]
    public async Task Cancel_WithOnlyAKnownRegistrationId_ReturnsNotFound_AndLeavesRegistrationUnchanged(
        string environmentName,
        PendingRegistrationStatus status)
    {
        var registration = await _harness.SeedAsync(status);
        var controller = _harness.CreateController(environmentName);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task Cancel_WithoutProof_DoesNotReleaseTheReservedSlugForSomeoneElse()
    {
        var registration = await _harness.SeedAsync(slug: "victim");
        var controller = _harness.CreateController(Environments.Production);

        await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        Assert.False(await _harness.IsSlugAvailableAsync("victim"));
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task Cancel_WithProofForRegistrationA_CannotCancelRegistrationB()
    {
        var registrationA = await _harness.SeedAsync(slug: "applicant-a");
        var registrationB = await _harness.SeedAsync(slug: "applicant-b");
        var browserA = _harness.CreateOwnerController(Environments.Production, registrationA.Id);

        var result = await browserA.Cancel(registrationB.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registrationB);
        await _harness.AssertUnchangedAsync(registrationA);
        Assert.False(await _harness.IsSlugAvailableAsync("applicant-b"));
    }

    [Fact]
    public async Task Cancel_WithTamperedProof_ReturnsNotFound_AndLeavesRegistrationUnchanged()
    {
        var registration = await _harness.SeedAsync(slug: "tampered");
        var proof = _harness.ProofFor(registration.Id);
        var tampered = proof[..^4] + (proof[^4] == 'A' ? "BBBB" : "AAAA");
        var controller = _harness.CreateController(Environments.Production, tampered);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
        Assert.False(await _harness.IsSlugAvailableAsync("tampered"));
    }

    [Fact]
    public async Task Cancel_WithExpiredProof_ReturnsNotFound_AndLeavesRegistrationUnchanged()
    {
        var registration = await _harness.SeedAsync(slug: "expired-proof");
        var expired = _harness.ProofFor(registration.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        var controller = _harness.CreateController(Environments.Production, expired);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        await _harness.AssertUnchangedAsync(registration);
        Assert.False(await _harness.IsSlugAvailableAsync("expired-proof"));
    }

    [Fact]
    public async Task Cancel_ByTheApplicant_WhileAwaitingPayment_CancelsAndReleasesTheSlug()
    {
        var registration = await _harness.SeedAsync(slug: "own-signup");
        Assert.False(await _harness.IsSlugAvailableAsync("own-signup"));
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CheckoutController.Cancelled), redirect.ActionName);
        Assert.Equal(PendingRegistrationStatus.Cancelled, (await _harness.ReloadAsync(registration.Id)).Status);
        Assert.True(await _harness.IsSlugAvailableAsync("own-signup"));
    }

    [Fact]
    public async Task Cancel_ByTheApplicant_AfterAFailedPayment_Cancels()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentFailed, slug: "failed-payment");
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CheckoutController.Cancelled), redirect.ActionName);
        Assert.Equal(PendingRegistrationStatus.Cancelled, (await _harness.ReloadAsync(registration.Id)).Status);
    }

    // The original status rules still apply to the proven applicant.
    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded, nameof(CheckoutController.Success))]
    [InlineData(PendingRegistrationStatus.Provisioned, nameof(CheckoutController.Success))]
    [InlineData(PendingRegistrationStatus.Cancelled, nameof(CheckoutController.Cancelled))]
    [InlineData(PendingRegistrationStatus.Expired, nameof(CheckoutController.Review))]
    public async Task Cancel_ByTheApplicant_InAStateThatCannotBeCancelled_LeavesItUnchanged(
        PendingRegistrationStatus status,
        string expectedRedirect)
    {
        var registration = await _harness.SeedAsync(status, slug: "locked-state");
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var result = await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(expectedRedirect, redirect.ActionName);
        await _harness.AssertUnchangedAsync(registration);
    }

    [Fact]
    public async Task Cancel_ByTheApplicant_AfterPayment_KeepsTheSlugReserved()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentSucceeded, slug: "paid-signup");
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        await controller.Cancel(registration.Id, TestContext.Current.CancellationToken);

        Assert.False(await _harness.IsSlugAvailableAsync("paid-signup"));
    }

    [Fact]
    public async Task Review_ForTheApplicant_OffersCancelOnlyWhereTheStatusAllowsIt()
    {
        var awaiting = await _harness.SeedAsync(slug: "awaiting");
        var paid = await _harness.SeedAsync(PendingRegistrationStatus.PaymentSucceeded, slug: "paid");

        var awaitingModel = await ReviewModelAsync(
            _harness.CreateOwnerController(Environments.Production, awaiting.Id), awaiting.Id);
        var paidModel = await ReviewModelAsync(
            _harness.CreateOwnerController(Environments.Production, paid.Id), paid.Id);

        Assert.True(awaitingModel.CanCancel);
        Assert.False(paidModel.CanCancel);
    }

    [Fact]
    public async Task Cancelled_ForAnAlreadyCancelledRegistration_IsAPublicPageWithoutBusinessDetails()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.Cancelled);
        var controller = _harness.CreateController(Environments.Production);

        var result = await controller.Cancelled(registration.Id, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CheckoutResultViewModel>(view.Model);
        Assert.Equal(registration.Id, model.RegistrationId);
        Assert.Null(model.BusinessName);
        Assert.Null(model.PrimaryDomain);
    }

    private static async Task<CheckoutReviewViewModel> ReviewModelAsync(CheckoutController controller, Guid id)
    {
        var view = Assert.IsType<ViewResult>(await controller.Review(id, TestContext.Current.CancellationToken));
        return Assert.IsType<CheckoutReviewViewModel>(view.Model);
    }
}
