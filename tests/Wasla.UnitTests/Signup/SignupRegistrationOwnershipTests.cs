using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Checkout;
using Wasla.Web.Models.Signup;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The registration ID is discoverable through the pending-tenant redirect, so it must never be enough
/// to read the applicant's personal details. Only the browser that submitted the signup, holding the
/// ownership proof issued by that submission, sees the private pages. The proof is issued by the registration draft
/// submit (WAS-192), covered over HTTP in <see cref="SignupDraftApiTests"/>.
/// </summary>
public sealed class SignupRegistrationOwnershipTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PendingPage_NeverIssuesAProof()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateSignupController(Environments.Production);

        await controller.Pending(registration.Id, TestContext.Current.CancellationToken);

        Assert.Empty(IssuedProofCookies(controller.HttpContext));
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.AwaitingPayment)]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    public async Task PendingPage_WithOnlyTheId_ShowsStatusWithoutPersonalOrApplicationDetails(PendingRegistrationStatus status)
    {
        var registration = await _harness.SeedAsync(status);

        var model = await PendingModelAsync(_harness.CreateSignupController(Environments.Production), registration.Id);

        AssertNoPrivateDetails(model);
        Assert.Equal(status, model.Status);
        Assert.Equal(registration.PrimaryDomain, model.PrimaryDomain);
    }

    [Fact]
    public async Task PendingPage_WithProofForAnotherRegistration_ShowsOnlyTheStatus()
    {
        var mine = await _harness.SeedAsync(slug: "mine");
        var theirs = await _harness.SeedAsync(slug: "theirs");

        var model = await PendingModelAsync(
            _harness.CreateSignupController(Environments.Production, _harness.ProofFor(mine.Id)),
            theirs.Id);

        AssertNoPrivateDetails(model);
    }

    [Fact]
    public async Task PendingPage_WithTamperedOrExpiredProof_ShowsOnlyTheStatus()
    {
        var registration = await _harness.SeedAsync();
        var proof = _harness.ProofFor(registration.Id);
        var tampered = proof[..^4] + (proof[^4] == 'A' ? "BBBB" : "AAAA");
        var expired = _harness.ProofFor(registration.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        foreach (var badProof in new[] { tampered, expired, "not-a-proof", string.Empty })
        {
            var model = await PendingModelAsync(
                _harness.CreateSignupController(Environments.Production, badProof),
                registration.Id);
            AssertNoPrivateDetails(model);
        }
    }

    [Fact]
    public async Task Review_ForTheApplicant_ShowsTheirApplication()
    {
        var registration = await _harness.SeedAsync();
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var view = Assert.IsType<ViewResult>(await controller.Review(registration.Id, TestContext.Current.CancellationToken));

        var model = Assert.IsType<CheckoutReviewViewModel>(view.Model);
        Assert.Equal("Private Owner", model.OwnerFullName);
        Assert.Equal("owner@example.test", model.OwnerEmail);
    }

    [Fact]
    public async Task Review_WithMissingWrongTamperedOrExpiredProof_RedirectsToThePublicStatusPage()
    {
        var registration = await _harness.SeedAsync(slug: "target");
        var other = await _harness.SeedAsync(slug: "other");
        var proof = _harness.ProofFor(registration.Id);
        var browsers = new[]
        {
            _harness.CreateController(Environments.Production),
            _harness.CreateOwnerController(Environments.Production, other.Id),
            _harness.CreateController(Environments.Production, proof[..^4] + (proof[^4] == 'A' ? "BBBB" : "AAAA")),
            _harness.CreateController(Environments.Production, _harness.ProofFor(registration.Id, DateTimeOffset.UtcNow.AddMinutes(-1)))
        };

        foreach (var browser in browsers)
        {
            var result = await browser.Review(registration.Id, TestContext.Current.CancellationToken);
            AssertRedirectsToPublicStatus(result, registration.Id);
        }
    }

    [Fact]
    public async Task Success_WithOnlyTheId_RedirectsToThePublicStatusPage()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentSucceeded);
        var controller = _harness.CreateController(Environments.Production);

        var result = await controller.Success(registration.Id, TestContext.Current.CancellationToken);

        AssertRedirectsToPublicStatus(result, registration.Id);
    }

    [Fact]
    public async Task Success_ForTheApplicant_ShowsTheirDetails()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentSucceeded);
        var controller = _harness.CreateOwnerController(Environments.Production, registration.Id);

        var view = Assert.IsType<ViewResult>(await controller.Success(registration.Id, TestContext.Current.CancellationToken));

        var model = Assert.IsType<SignupPendingViewModel>(view.Model);
        Assert.True(model.ShowPrivateDetails);
        Assert.Equal("Private Owner", model.OwnerFullName);
    }

    [Fact]
    public async Task Failed_WithOnlyTheId_IsAPublicPageWithoutBusinessDetails()
    {
        var registration = await _harness.SeedAsync(PendingRegistrationStatus.PaymentFailed);
        var controller = _harness.CreateController(Environments.Production);

        var view = Assert.IsType<ViewResult>(await controller.Failed(registration.Id, TestContext.Current.CancellationToken));

        var model = Assert.IsType<CheckoutResultViewModel>(view.Model);
        Assert.Null(model.BusinessName);
        Assert.Null(model.PrimaryDomain);
    }

    private static void AssertNoPrivateDetails(SignupPendingViewModel model)
    {
        Assert.False(model.ShowPrivateDetails);
        Assert.False(model.ShowCheckoutAction);
        Assert.True(string.IsNullOrEmpty(model.BusinessName));
        Assert.True(string.IsNullOrEmpty(model.PlanCode));
        Assert.True(string.IsNullOrEmpty(model.PlanDisplayName));
        Assert.True(string.IsNullOrEmpty(model.BillingPeriod));
        Assert.True(string.IsNullOrEmpty(model.BusinessPhone));
        Assert.True(string.IsNullOrEmpty(model.BusinessEmail));
        Assert.True(string.IsNullOrEmpty(model.OwnerFullName));
        Assert.True(string.IsNullOrEmpty(model.OwnerEmail));
        Assert.True(string.IsNullOrEmpty(model.OwnerPhone));
        Assert.Equal(default, model.CreatedAtUtc);
        Assert.Null(model.PaymentSucceededAtUtc);
        Assert.Null(model.ProvisionedAtUtc);
    }

    private static void AssertRedirectsToPublicStatus(IActionResult result, Guid registrationId)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Signup", redirect.ControllerName);
        Assert.Equal(nameof(SignupController.Pending), redirect.ActionName);
        Assert.Equal(registrationId, redirect.RouteValues!["id"]);
    }

    private static async Task<SignupPendingViewModel> PendingModelAsync(SignupController controller, Guid id)
    {
        var view = Assert.IsType<ViewResult>(await controller.Pending(id, TestContext.Current.CancellationToken));
        return Assert.IsType<SignupPendingViewModel>(view.Model);
    }

    private static IReadOnlyList<SetCookieHeaderValue> IssuedProofCookies(HttpContext context) =>
        SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.OfType<string>().ToList())
            .Where(cookie => cookie.Name.Value == SignupRegistrationOwnership.CookieName)
            .ToList();
}
