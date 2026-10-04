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
/// ownership proof issued by that submission, sees the private pages.
/// </summary>
public sealed class SignupRegistrationOwnershipTests : IDisposable
{
    private readonly CheckoutTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SuccessfulSignup_IssuesAHostOnlySecureHttpOnlyStrictProofCookie_OutsideTheUrl()
    {
        var controller = _harness.CreateSignupController(Environments.Production, https: true);

        var result = await controller.Index(SignupForm("fresh-signup"), TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SignupController.Pending), redirect.ActionName);
        var registrationId = Assert.IsType<Guid>(redirect.RouteValues!["id"]);
        Assert.Single(redirect.RouteValues!);

        var cookie = Assert.Single(IssuedProofCookies(controller.HttpContext));
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/", cookie.Path.Value);
        Assert.True(string.IsNullOrEmpty(cookie.Domain.Value), "The proof must not be shared with tenant subdomains.");
        Assert.NotNull(cookie.Expires);
        Assert.InRange(cookie.Expires!.Value, DateTimeOffset.UtcNow.AddDays(6), DateTimeOffset.UtcNow.AddDays(8));
        Assert.DoesNotContain(cookie.Value.Value!, registrationId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulSignup_InDevelopmentOverHttp_StillIssuesAnHttpOnlyStrictProof()
    {
        var controller = _harness.CreateSignupController(Environments.Development);

        await controller.Index(SignupForm("local-signup"), TestContext.Current.CancellationToken);

        var cookie = Assert.Single(IssuedProofCookies(controller.HttpContext));
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
    }

    [Fact]
    public async Task ProofIssuedBySignup_ShowsTheApplicantTheirPrivateDetails_AndAnotherBrowserOnlyTheStatus()
    {
        var signup = _harness.CreateSignupController(Environments.Production, https: true);
        var redirect = Assert.IsType<RedirectToActionResult>(
            await signup.Index(SignupForm("two-browsers"), TestContext.Current.CancellationToken));
        var registrationId = (Guid)redirect.RouteValues!["id"]!;
        var proof = Assert.Single(IssuedProofCookies(signup.HttpContext)).Value.Value;

        var applicant = await PendingModelAsync(_harness.CreateSignupController(Environments.Production, proof), registrationId);
        var stranger = await PendingModelAsync(_harness.CreateSignupController(Environments.Production), registrationId);

        Assert.True(applicant.ShowPrivateDetails);
        Assert.Equal("Owner Name", applicant.OwnerFullName);
        Assert.Equal("owner@example.com", applicant.OwnerEmail);
        Assert.True(applicant.ShowCheckoutAction);
        AssertNoPrivateDetails(stranger);
        Assert.Equal(PendingRegistrationStatus.AwaitingPayment, stranger.Status);
    }

    [Fact]
    public async Task RejectedSignup_DoesNotIssueAProof()
    {
        await _harness.SeedAsync(slug: "taken");
        var controller = _harness.CreateSignupController(Environments.Production, https: true);

        var result = await controller.Index(SignupForm("taken"), TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        Assert.Empty(IssuedProofCookies(controller.HttpContext));
    }

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

    private static SignupViewModel SignupForm(string slug) => new()
    {
        PlanCode = "Starter",
        BillingPeriod = "Monthly",
        BusinessName = "Burger House",
        SelectedBusinessTypeCodes = ["burger"],
        BusinessPhoneType = "Mobile",
        BusinessPhone = "5551112233",
        Slug = slug,
        Country = "Germany",
        City = "Berlin",
        District = "Mitte",
        StreetAddress = "Main Street 1",
        OwnerFullName = "Owner Name",
        OwnerEmail = "owner@example.com",
        Password = "password1",
        ConfirmPassword = "password1"
    };
}
