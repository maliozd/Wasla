using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Encodings.Web;
using Wasla.Domain.Enums;
using Wasla.Web.Models.Signup;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The registration status and checkout pages of Figma "03 · Güncel kayıt durumu ve ödeme", rendered by the real hosts:
/// what each stored status may offer (the panel action only for a provisioned registration with a sign-in address, the
/// summary link only while payment is awaited), what a browser without ownership proof sees (the status and the
/// address, nothing about the business, plan or applicant), and the Development-only simulator.
/// </summary>
public sealed class RegistrationStatusPagesRenderedTests : IClassFixture<SignupRenderedResponseTests.Hosts>
{
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);

    private readonly SignupRenderedResponseTests.Hosts _hosts;

    public RegistrationStatusPagesRenderedTests(SignupRenderedResponseTests.Hosts hosts) => _hosts = hosts;

    public static TheoryData<PendingRegistrationStatus> AllStatuses => new()
    {
        PendingRegistrationStatus.AwaitingPayment,
        PendingRegistrationStatus.PaymentFailed,
        PendingRegistrationStatus.PaymentSucceeded,
        PendingRegistrationStatus.Provisioned,
        PendingRegistrationStatus.Cancelled,
        PendingRegistrationStatus.Expired
    };

    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task StatusPage_ForTheApplicant_OffersOnlyWhatTheStoredStatusAllows(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var page = await owner.GetAsync($"/signup/pending/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var body = page.Body;
        var (badgeKey, tone) = ExpectedBadge(status);
        Assert.Contains($"data-su-status-badge=\"{tone}\">{Text(badgeKey)}</span>", body, StringComparison.Ordinal);

        // A ready panel is shown only for Provisioned: payment received is not a ready panel, and nothing else is either.
        Assert.Equal(status == PendingRegistrationStatus.Provisioned, body.Contains("data-su-panel-login", StringComparison.Ordinal));
        Assert.Equal(status == PendingRegistrationStatus.Provisioned, body.Contains("/auth/login\"", StringComparison.Ordinal));
        Assert.Equal(status == PendingRegistrationStatus.Provisioned, body.Contains($">{Text("Signup.PanelReadyTitle")}</h1>", StringComparison.Ordinal));
        Assert.Equal(
            status == PendingRegistrationStatus.PaymentSucceeded,
            body.Contains($">{Text("Signup.PaymentReceivedPreparingTitle")}</h1>", StringComparison.Ordinal));
        Assert.Equal(status == PendingRegistrationStatus.AwaitingPayment, body.Contains($"/checkout/review/{applicant.Id}", StringComparison.Ordinal));
        AssertStateActions(body, status, applicant, $"/signup/pending/{applicant.Id}");

        // The applicant's details all stay, under the contact disclosure (a full-width row) and in the card.
        Assert.Contains($"class=\"su-status-contact__title\">{Text("RegistrationStatus.ContactDetails")}</span>", body, StringComparison.Ordinal);
        Assert.Contains(Text("RegistrationStatus.CardRestaurant"), body, StringComparison.Ordinal);
        Assert.Contains(Html(applicant.Entity.BusinessEmail!), body, StringComparison.Ordinal);
        Assert.Contains(Html(applicant.OwnerPhoneDisplay), body, StringComparison.Ordinal);

        // No amount or renewal: this model has no price.
        Assert.DoesNotContain("su-receipt__amount", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" TL", WebUtility.HtmlDecode(body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    public async Task SuccessPage_ShowsThePanelActionOnlyOnceTheRegistrationIsProvisioned(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var page = await owner.GetAsync($"/checkout/success/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var provisioned = status == PendingRegistrationStatus.Provisioned;
        Assert.Equal(provisioned, page.Body.Contains("data-su-panel-login", StringComparison.Ordinal));
        Assert.Equal(provisioned, page.Body.Contains(Text("RegistrationStatus.LoginAddress"), StringComparison.Ordinal));
        Assert.Equal(!provisioned, page.Body.Contains(Text("RegistrationStatus.StepPanelWaiting"), StringComparison.Ordinal));
        Assert.Equal(!provisioned, page.Body.Contains(Text("RegistrationStatus.CheckLaterTitle"), StringComparison.Ordinal));
        Assert.Equal(!provisioned, page.Body.Contains(Text("RegistrationStatus.CheckLaterBody"), StringComparison.Ordinal));
        AssertStateActions(page.Body, status, applicant, $"/checkout/success/{applicant.Id}");
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task StatusPage_WithoutOwnershipProof_ShowsTheStatusAndAddress_NeverTheBusinessPlanOrContact(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var page = await stranger.GetAsync($"/signup/pending/{applicant.Id}");
        var ownerPage = await owner.GetAsync($"/signup/pending/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains($">{Text("RegistrationStatus.PublicTitle")}</h1>", page.Body, StringComparison.Ordinal);
        Assert.Contains(Text("RegistrationStatus.PublicCardTitle"), page.Body, StringComparison.Ordinal);
        Assert.Contains("data-su-status-badge=", page.Body, StringComparison.Ordinal);
        Assert.Contains(applicant.Entity.PrimaryDomain, page.Body, StringComparison.Ordinal);
        // Matched as the elements that carry them, since the same words also occur in the public step texts.
        foreach (var fragment in new[]
                 {
                     $">{Text("RegistrationStatus.CardRestaurant")}</h3>", $"<dt>{Text("RegistrationStatus.BusinessLabel")}</dt>",
                     $"<dt>{Text("Checkout.PlanLabel")}</dt>", $"class=\"su-status-contact__title\">{Text("RegistrationStatus.ContactDetails")}</span>",
                     $"<dt>{Text("Signup.PendingLabelOwnerEmail")}</dt>", $"<dt>{Text("Signup.PendingLabelCreatedAt")}</dt>",
                     $">{Text("RegistrationStatus.NextStepTitle")}</strong>", $">{Text("RegistrationStatus.CheckLaterTitle")}</strong>"
                 })
        {
            Assert.False(page.Body.Contains(fragment, StringComparison.Ordinal), $"public page shows {fragment}");
            Assert.True(
                ownerPage.Body.Contains(fragment, StringComparison.Ordinal) || fragment.EndsWith("</strong>", StringComparison.Ordinal),
                $"owner page lacks {fragment}");
        }

        Assert.DoesNotContain("su-status-facts", page.Body, StringComparison.Ordinal);
        // The same state actions as the applicant: the address a link only when provisioned, refresh while the panel is
        // prepared, a new registration after a cancellation.
        AssertStateActions(page.Body, status, applicant, $"/signup/pending/{applicant.Id}");
        Assert.DoesNotContain("su-status-contact", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Text("Pricing.Plan.Starter.Name"), page.Body, StringComparison.Ordinal);
        foreach (var value in applicant.PrivateValues)
            Assert.DoesNotContain(Html(value), page.Body, StringComparison.OrdinalIgnoreCase);

        // The same address with proof is the applicant's page.
        Assert.DoesNotContain($">{Text("RegistrationStatus.PublicTitle")}</h1>", ownerPage.Body, StringComparison.Ordinal);
        Assert.Contains(Text("Pricing.Plan.Starter.Name"), ownerPage.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewPage_ShowsTheSimulatorOnlyInDevelopment_AndAlwaysKeepsCancel()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var production = _hosts.OwnerBrowser(_hosts.Production, applicant);
        using var development = _hosts.OwnerBrowser(_hosts.Development, applicant);

        var productionPage = await production.GetAsync($"/checkout/review/{applicant.Id}");
        var developmentPage = await development.GetAsync($"/checkout/review/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, productionPage.Status);
        Assert.DoesNotContain("su-status-sim", productionPage.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Text("RegistrationStatus.SimulatorTitle"), productionPage.Body, StringComparison.Ordinal);
        Assert.Contains(Text("RegistrationStatus.AwaitingPaymentNoticeTitle"), productionPage.Body, StringComparison.Ordinal);
        Assert.Contains($"action=\"/checkout/cancel/{applicant.Id}\"", productionPage.Body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, developmentPage.Status);
        Assert.Contains(Text("RegistrationStatus.SimulatorTitle"), developmentPage.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Text("RegistrationStatus.AwaitingPaymentNoticeTitle"), developmentPage.Body, StringComparison.Ordinal);
        Assert.Contains($"action=\"/checkout/cancel/{applicant.Id}\"", developmentPage.Body, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(productionPage.Body, "name=\"__RequestVerificationToken\""));
        Assert.Equal(3, CountOf(developmentPage.Body, "name=\"__RequestVerificationToken\""));
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    [InlineData(PendingRegistrationStatus.Expired)]
    public async Task ReviewPage_ForAClosedRegistration_ExplainsItsStatus_WithoutCancelOrSimulator(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var development = _hosts.OwnerBrowser(_hosts.Development, applicant);

        var page = await development.GetAsync($"/checkout/review/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("class=\"su-notice su-notice--info\" role=\"status\"", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/checkout/cancel/", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/checkout/simulate-", page.Body, StringComparison.Ordinal);
        var (badgeKey, tone) = ExpectedBadge(status, simulator: true);
        Assert.Contains($"data-su-status-badge=\"{tone}\">{Text(badgeKey)}</span>", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArabicStatusPage_IsRightToLeft_WithTheSharedLanguageMenu()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var page = await owner.GetAsync($"/signup/pending/{applicant.Id}?culture=ar-SA&ui-culture=ar-SA");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("<html lang=\"ar-SA\" dir=\"rtl\"", page.Body, StringComparison.Ordinal);
        Assert.Contains("css/rtl.css", page.Body, StringComparison.Ordinal);
        Assert.Contains("id=\"su-status-languages\"", page.Body, StringComparison.Ordinal);
        Assert.Contains($">{Text("Signup.PendingTitle", "ar-SA")}</h1>", page.Body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task CancelledPage_IsOneTerminalCard_NewRegistrationFirst_HomeSecond_AndNothingPrivate(string culture)
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.Cancelled);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);
        var query = $"?culture={culture}&ui-culture={culture}";

        foreach (var browser in new[] { stranger, owner })
        {
            var page = await browser.GetAsync($"/checkout/cancelled/{applicant.Id}{query}");

            Assert.Equal(HttpStatusCode.OK, page.Status);
            var body = page.Body;
            // The terminal layout (Figma 92:2302): one card holding the marker, the heading, the text and the actions.
            Assert.Contains("class=\"su-shell su-status-page su-status-page--terminal\"", body, StringComparison.Ordinal);
            var card = body.IndexOf("data-su-terminal=\"cancelled\"", StringComparison.Ordinal);
            Assert.True(card >= 0, "no terminal card");
            Assert.Contains($"data-su-status-badge=\"neutral\">{Text("Signup.PaymentStatusCancelled", culture)}</span>", body, StringComparison.Ordinal);
            Assert.Contains($"id=\"cancelledTitle\">{Text("Checkout.CancelledTitle", culture)}</h1>", body, StringComparison.Ordinal);
            Assert.Contains(Text("Checkout.CancelledMessage", culture), body, StringComparison.Ordinal);

            // The registration card that repeated the message is gone, and so is the two-column layout.
            Assert.DoesNotContain("su-status__aside", body, StringComparison.Ordinal);
            Assert.DoesNotContain("su-status-card", body, StringComparison.Ordinal);
            Assert.DoesNotContain("su-status__layout", body, StringComparison.Ordinal);

            // A new registration is the primary action and comes first; home stays the quiet one.
            var primary = body.IndexOf($"<a class=\"su-btn su-btn--primary\" href=\"/signup\">{Text("Checkout.RegisterAgain", culture)}</a>", StringComparison.Ordinal);
            var home = body.IndexOf($"<a class=\"su-btn su-btn--quiet\" href=\"/\">{Text("Signup.PendingBackHome", culture)}</a>", StringComparison.Ordinal);
            Assert.True(primary > card && home > primary, "the new registration comes first, home second, both in the card");
            Assert.Equal(1, CountOf(body, "su-btn--primary"));

            foreach (var value in applicant.PrivateValues)
                Assert.DoesNotContain(Html(value), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(applicant.Entity.PrimaryDomain, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Text("Pricing.Plan.Starter.Name", culture), body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What a status page offers for a stored status, owner or not: the tenant address is a link only once the panel is
    /// provisioned (monospaced text before), "Durumu yenile" is a plain link to the same page only while the panel is
    /// prepared, and only a cancelled registration offers a new one, before the way home.
    /// </summary>
    private static void AssertStateActions(string body, PendingRegistrationStatus status, SentinelApplicant applicant, string pagePath)
    {
        var provisioned = status == PendingRegistrationStatus.Provisioned;
        Assert.Equal(provisioned, body.Contains("class=\"su-status-card__domain-link", StringComparison.Ordinal));
        Assert.Equal(provisioned, body.Contains($"://{applicant.Entity.PrimaryDomain}", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(!provisioned, body.Contains($"data-su-tenant-address=\"text\"><bdi class=\"su-ltr\">{applicant.Entity.PrimaryDomain}</bdi>", StringComparison.Ordinal));

        Assert.Equal(
            status == PendingRegistrationStatus.PaymentSucceeded,
            body.Contains($"href=\"{pagePath}\" data-su-refresh-status", StringComparison.Ordinal));
        Assert.Equal(status == PendingRegistrationStatus.PaymentSucceeded, body.Contains(Text("RegistrationStatus.RefreshStatus"), StringComparison.Ordinal));

        var registerAgain = body.IndexOf("data-su-register-again", StringComparison.Ordinal);
        Assert.Equal(status == PendingRegistrationStatus.Cancelled, registerAgain >= 0);
        if (registerAgain >= 0)
        {
            Assert.Contains("/signup\" data-su-register-again", body, StringComparison.Ordinal);
            Assert.True(body.IndexOf(Text("Signup.PendingBackHome"), StringComparison.Ordinal) > registerAgain, "home comes after the new registration");
        }
    }

    private static (string Key, string Tone) ExpectedBadge(PendingRegistrationStatus status, bool simulator = false) => status switch
    {
        PendingRegistrationStatus.PaymentSucceeded => ("Signup.PaymentStatusPaid", "success"),
        PendingRegistrationStatus.Provisioned => ("RegistrationStatus.PanelReady", "success"),
        PendingRegistrationStatus.PaymentFailed => (simulator ? "Signup.PaymentStatusFailed" : "Checkout.PaymentNotCompletedTitle", "danger"),
        PendingRegistrationStatus.Cancelled => ("Signup.PaymentStatusCancelled", "neutral"),
        PendingRegistrationStatus.Expired => ("Signup.PaymentStatusExpired", "neutral"),
        _ => ("Signup.PaymentStatusPending", "neutral")
    };

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private static string Text(string key, string culture = "tr-TR") =>
        Html(Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException($"Missing resource {key}"));
}

/// <summary>The status pages' presentation rules, without a host.</summary>
public sealed class RegistrationStatusPresentationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/auth/login", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://kebap.app.wasla.tr/auth/login", false)]
    [InlineData("https://kebap.app.wasla.tr/auth/login", true)]
    [InlineData("http://kebap.app.localhost:5310/auth/login", true)]
    public void PanelAction_NeedsAnAbsoluteHttpSignInAddress(string? url, bool usable) =>
        Assert.Equal(usable, RegistrationStatusPresentation.IsUsablePanelLoginUrl(url));

    [Fact]
    public void Steps_FollowTheStoredStatus_AndNeverMarkAPanelReadyBeforeProvisioning()
    {
        var awaiting = RegistrationStatusPresentation.Steps(PendingRegistrationStatus.AwaitingPayment, simulator: false);
        var paid = RegistrationStatusPresentation.Steps(PendingRegistrationStatus.PaymentSucceeded, simulator: false);
        var failed = RegistrationStatusPresentation.Steps(PendingRegistrationStatus.PaymentFailed, simulator: false);

        Assert.Equal([RegistrationStepState.Done, RegistrationStepState.Current, RegistrationStepState.Upcoming], awaiting.Select(s => s.State));
        Assert.Equal([RegistrationStepState.Done, RegistrationStepState.Current, RegistrationStepState.Upcoming], paid.Select(s => s.State));
        Assert.Equal("RegistrationStatus.PanelReady", paid[2].NameKey);
        Assert.Equal(RegistrationStepState.Failed, failed[1].State);
        Assert.Equal("Checkout.PaymentNotCompletedTitle", failed[1].NameKey);
        Assert.Equal("Signup.PaymentStatusFailed", RegistrationStatusPresentation.Steps(PendingRegistrationStatus.PaymentFailed, simulator: true)[1].NameKey);
        Assert.Empty(RegistrationStatusPresentation.Steps(PendingRegistrationStatus.Provisioned, simulator: false));
        Assert.Empty(RegistrationStatusPresentation.Steps(PendingRegistrationStatus.Cancelled, simulator: false));
        Assert.Empty(RegistrationStatusPresentation.Steps(PendingRegistrationStatus.Expired, simulator: false));
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.AwaitingPayment)]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    [InlineData(PendingRegistrationStatus.PaymentFailed)]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    [InlineData(PendingRegistrationStatus.Expired)]
    public void ABrowserWithoutProof_GetsTheNeutralHeading_WhateverTheStatus(PendingRegistrationStatus status)
    {
        Assert.Equal("RegistrationStatus.PublicTitle", RegistrationStatusPresentation.TitleKey(status, simulator: true, isOwner: false));
        Assert.Equal("RegistrationStatus.PublicLead", RegistrationStatusPresentation.LeadKey(status, simulator: false, isOwner: false));
    }
}
