using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Sms;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Sms;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;
using static Wasla.UnitTests.Web.PricingPeriodTabsRenderedTests;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The stepped signup page (WAS-194) in a real headless Chromium browser over the real registration draft API (WAS-192):
/// <see cref="TenantSessionWebHost"/> serves the marketing host <c>wasla.local</c> with the real controllers, views,
/// scripts and styles, the fake SMS sender (codes are read from <see cref="FakeSmsOutbox"/> in the test process, never
/// shown on the page) and fixed plan prices. Covers the steps saved through their PUTs and restored after Back, Forward,
/// Edit and a reload; the phone step gating the review (a wrong code, a changed number, a failed delivery, and a submit
/// sent before verification); the advisory address check ignoring a stale answer; the final address conflict at submit;
/// keyboard and error focus; and Arabic right to left at phone width. Every wait is on page state.
/// </summary>
[Trait("Category", "Browser")]
public sealed class SignupFlowBrowserTests : IAsyncLifetime
{
    // The marketing host is a *.localhost name: the draft cookie is Secure outside Development, and Chromium keeps Secure
    // cookies over plain HTTP only for loopback names, which it also resolves itself.
    private const string Host = "wasla.localhost";
    private const string ApplicationDomain = "app.wasla.localhost";
    private const int CityId = 934;
    private const int DistrictId = 9451;
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // Small page helpers, installed before the page's own scripts on every navigation.
    private const string PageHelpers = """
        window.__su = {
          set(id, value) {
            const e = document.getElementById(id);
            e.value = value;
            e.dispatchEvent(new Event('input', { bubbles: true }));
            e.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
          },
          click(selector) { document.querySelector(selector).click(); return true; },
          current() { const r = document.querySelector('[data-su-root]'); return r ? r.getAttribute('data-su-current') : ''; },
          active() { const a = document.activeElement; return a ? (a.id || a.getAttribute('data-su-error-summary') !== null && 'summary' || a.tagName) : ''; }
        };
        """;

    private TenantSessionWebHost _host = null!;

    private static int Port { get; set; }

    public async ValueTask InitializeAsync()
    {
        _host = await TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?>
            {
                ["Wasla:TenantOnboarding:MarketingBaseDomain"] = Host,
                ["Wasla:TenantOnboarding:ApplicationBaseDomain"] = ApplicationDomain
            },
            configureServices: services =>
            services.AddSingleton<IBillingPlanPriceReader>(new FixedPlanPrices
            {
                Quotes =
                [
                    new(Guid.NewGuid(), "Starter", "Monthly", new BillingPrice(450m, "TRY"), true),
                    new(Guid.NewGuid(), "Pro", "Monthly", new BillingPrice(820m, "TRY"), true),
                    new(Guid.NewGuid(), "Pro", "Yearly", new BillingPrice(8200m, "TRY"), true)
                ]
            }));

        // One Turkish city and district for the address selects (the page offers Türkiye only).
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        db.Cities.Add(new City { Id = CityId, CountryCode = "TR", Name = "İstanbul", PhoneAreaCode = "216", IsActive = true });
        db.Districts.Add(new District { Id = DistrictId, CityId = CityId, Name = "Kadıköy", IsActive = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private FakeSmsOutbox Outbox => _host.Services.GetRequiredService<FakeSmsOutbox>();

    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryStepIsSaved_AndRestored_AfterBackForwardEditAndReload_ThenTheSubmitOpensThePendingPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();

        await OpenAsync(browser, "/signup?plan=Pro", ct);
        Assert.Equal("plan", await CurrentAsync(browser, ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('signupPlan-Pro').checked", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("document.querySelectorAll('h1').length", ct));

        await Js(browser, "__su.click('#signupBillingPeriod-Yearly')", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, applicant, ct);
        await ContinueAsync(browser, "owner", ct);
        await FillOwnerAsync(browser, applicant, ct);

        // "Send verification code" saves the owner and sends the first code.
        await ContinueAsync(browser, "phone", ct);
        await WaitForAsync(browser, "document.getElementById('signupStep-phone').getAttribute('data-phone-state') === 'codeSent'", ct);
        Assert.Equal("signupPhoneCode", await ActiveIdAsync(browser, ct));
        var code = Outbox.LatestCodeTo(applicant.E164) ?? throw new InvalidOperationException("No code was sent.");
        await Js(browser, $"__su.set('signupPhoneCode', '{code}')", ct);

        // A full code verifies on its own and opens the review.
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        var review = await ReviewAsync(browser, ct);
        Assert.Equal(applicant.BusinessName, review.GetProperty("businessName").GetString());
        Assert.Equal(applicant.Slug + "." + ApplicationDomain, review.GetProperty("domain").GetString());
        Assert.Equal(applicant.Display, review.GetProperty("ownerPhone").GetString());
        Assert.True(review.GetProperty("verified").GetBoolean());
        Assert.Equal(Text("Signup.Review.PasswordSet", "tr-TR"), review.GetProperty("password").GetString());
        Assert.Equal("review", await browser.EvaluateAsync<string>("document.querySelector('#signupReceipt-title').closest('[data-su-receipt]').querySelector('[aria-current=step]').getAttribute('data-su-receipt-step')", ct));

        // Back and Forward move between the steps; the verified phone stays verified.
        await Js(browser, "history.back()", ct);
        await WaitForAsync(browser, "__su.current() === 'phone'", ct);
        Assert.Equal("verified", await browser.EvaluateAsync<string>("document.getElementById('signupStep-phone').getAttribute('data-phone-state')", ct));
        await Js(browser, "history.forward()", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);

        // A reload restores the draft: the review opens again with the restaurant fields filled in.
        await OpenAsync(browser, "/signup", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(applicant.BusinessName, await browser.EvaluateAsync<string>("document.getElementById('signupBusinessName').value", ct));
        await WaitForAsync(browser, $"document.getElementById('signupDistrictId').value === '{DistrictId}'", ct);

        // Edit from the review, change a value, save again: the change reaches the review.
        await Js(browser, "document.querySelector('#signupStep-review [data-su-go=restaurant]').click()", ct);
        await WaitForAsync(browser, "__su.current() === 'restaurant'", ct);
        Assert.Equal(applicant.Slug, await browser.EvaluateAsync<string>("document.getElementById('signupSlug').value", ct));
        await Js(browser, $"__su.set('signupBusinessName', '{applicant.BusinessName} 2')", ct);
        await ContinueAsync(browser, "owner", ct);
        // The saved password is kept: the owner step continues without typing it again, and the phone stays verified.
        Assert.False(await browser.EvaluateAsync<bool>("document.querySelector('[data-su-password-kept]').hidden", ct));
        await ContinueAsync(browser, "phone", ct);
        Assert.Equal("verified", await browser.EvaluateAsync<string>("document.getElementById('signupStep-phone').getAttribute('data-phone-state')", ct));
        await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(applicant.BusinessName + " 2", (await ReviewAsync(browser, ct)).GetProperty("businessName").GetString());

        await Js(browser, "__su.click('#signupSubmitButton')", ct);
        await WaitForAsync(browser, "location.pathname.startsWith('/signup/pending/')", ct);
    }

    [Fact]
    public async Task TheReview_StaysClosedUntilThePhoneIsVerified_AndAWrongCodeKeepsFocusInTheField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);

        // An address that names the review lands on the phone step while the number is not verified, and the address
        // is corrected to the step shown.
        await Js(browser, "location.assign('/signup?step=review')", ct);
        await WaitForAsync(browser, "location.search === '?step=phone' && !!window.__su && __su.current() === 'phone'", ct);

        // A wrong code: the server's message under the field, focus kept in the field.
        var wrong = Outbox.LatestCodeTo(applicant.E164) == "000000" ? "111111" : "000000";
        await Js(browser, "document.getElementById('signupPhoneCode').focus()", ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{wrong}')", ct);
        await WaitForAsync(browser, "document.getElementById('signupPhoneCode-error').textContent.trim().length > 0", ct);
        Assert.Equal(Text("Signup.Phone.CodeIncorrect", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupPhoneCode-error').textContent.trim()", ct));
        Assert.Equal("true", await browser.EvaluateAsync<string>("document.getElementById('signupPhoneCode').getAttribute('aria-invalid')", ct));
        Assert.Equal("signupPhoneCode", await ActiveIdAsync(browser, ct));

        // The hidden submit button only continues the open step (Enter's implicit submission), so it verifies, not submits.
        await Js(browser, "document.getElementById('signupSubmitButton').click()", ct);
        await WaitForAsync(browser, "document.getElementById('signupPhoneCode-error').textContent.trim().length > 0", ct);
        Assert.Equal("phone", await CurrentAsync(browser, ct));

        // A submit sent straight to the API from the page, with its own token and draft cookie, is refused before
        // verification; no registration exists afterwards.
        var refused = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              const endpoints = JSON.parse(document.getElementById('signupEndpoints').textContent);
              const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
              const response = await fetch(endpoints.submit, { method: 'POST', headers: { 'RequestVerificationToken': token, 'Content-Type': 'application/json' }, body: '{}' });
              return { status: response.status, body: await response.json() };
            })()
            """, ct);
        Assert.Equal(409, refused.GetProperty("status").GetInt32());
        Assert.Equal("Signup.Phone.NotVerified", refused.GetProperty("body").GetProperty("code").GetString());
        Assert.Equal(Text("Signup.Phone.NotVerified", "tr-TR"), refused.GetProperty("body").GetProperty("message").GetString());
        Assert.Equal("phone", await CurrentAsync(browser, ct));
    }

    [Fact]
    public async Task ChangingTheNumber_SendsANewCode_AndOnlyTheNewNumbersCodeVerifies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);
        var oldCode = Outbox.LatestCodeTo(applicant.E164)!;

        var other = Applicant.New();
        await Js(browser, "__su.click('[data-su-phone-change]')", ct);
        await WaitForAsync(browser, "!document.querySelector('[data-su-phone-changer]').hidden", ct);
        Assert.Equal("signupPhoneChange", await ActiveIdAsync(browser, ct));
        await Js(browser, $"__su.set('signupPhoneChange', '{other.Digits}')", ct);
        await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
        await WaitForAsync(browser, $"document.querySelector('[data-su-phone-masked]').textContent.trim().endsWith('{other.Digits[^2..]}') && document.getElementById('signupStep-phone').getAttribute('data-phone-state') === 'codeSent'", ct);
        var newCode = Outbox.LatestCodeTo(other.E164) ?? throw new InvalidOperationException("No code to the new number.");

        if (oldCode != newCode)
        {
            await Js(browser, $"__su.set('signupPhoneCode', '{oldCode}')", ct);
            await WaitForAsync(browser, "document.getElementById('signupPhoneCode-error').textContent.trim().length > 0", ct);
            Assert.Equal("phone", await CurrentAsync(browser, ct));
        }

        await Js(browser, $"__su.set('signupPhoneCode', '{newCode}')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(other.Display, (await ReviewAsync(browser, ct)).GetProperty("ownerPhone").GetString());
        // The owner step shows the new number too.
        Assert.Equal(other.Grouped, await browser.EvaluateAsync<string>("document.getElementById('signupOwnerPhone').value", ct));
    }

    [Fact]
    public async Task AFailedDelivery_ShowsTheServersMessageWithTheSupportAddress_AndNoCodeField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();
        Outbox.FailDeliveriesWith = SmsDeliveryFailure.Rejected;
        try
        {
            await ReachPhoneStepAsync(browser, applicant, ct, expectCode: false);
            await WaitForAsync(browser, "!document.querySelector('[data-su-phone-alert]').hidden", ct);
            Assert.Equal(Text("Signup.Phone.DeliveryFailed", "tr-TR"), await browser.EvaluateAsync<string>("document.querySelector('[data-su-phone-alert-text]').textContent.trim()", ct));
            Assert.Equal(Text("Signup.Phone.DeliveryFailedTitle", "tr-TR"), await browser.EvaluateAsync<string>("document.querySelector('[data-su-phone-alert-title]').textContent.trim()", ct));
            Assert.Contains("mailto:", await browser.EvaluateAsync<string>("document.querySelector('[data-su-phone-alert-support] a').getAttribute('href')", ct));
            Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('[data-su-code-field]').hidden", ct));
            Assert.Equal("phone", await CurrentAsync(browser, ct));
        }
        finally
        {
            Outbox.FailDeliveriesWith = null;
        }
    }

    [Fact]
    public async Task TheAddressCheck_ShowsOnlyTheAnswerForTheAddressInTheField()
    {
        var ct = TestContext.Current.CancellationToken;
        // The answer for the first address is held back, so it arrives after the answer for the second one.
        var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, extraScript: """
            (() => {
              const original = window.fetch.bind(window);
              window.fetch = (url, init) => String(url).includes('address-availability') && String(url).includes('slug=admin')
                ? new Promise(resolve => setTimeout(() => resolve(original(url, init)), 1500))
                : original(url, init);
            })();
            """);
        await using var _ = browser;
        var applicant = Applicant.New();

        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await Js(browser, "__su.set('signupSlug', 'admin')", ct);
        await WaitForAsync(browser, "document.getElementById('signupSlugStatus').getAttribute('data-state') === 'checking'", ct);
        await Js(browser, $"__su.set('signupSlug', '{applicant.Slug}')", ct);
        await WaitForAsync(browser, "document.getElementById('signupSlugStatus').getAttribute('data-state') === 'available'", ct);
        await Task.Delay(1800, ct); // the held-back answer for "admin" arrives now; it must not change anything

        Assert.Equal("available", await browser.EvaluateAsync<string>("document.getElementById('signupSlugStatus').getAttribute('data-state')", ct));
        Assert.Contains(applicant.Slug + "." + ApplicationDomain, await browser.EvaluateAsync<string>("document.querySelector('[data-su-slug-message]').textContent", ct));

        // A reserved address answers "taken" once it is the address in the field.
        await Js(browser, "__su.set('signupSlug', 'admin')", ct);
        await WaitForAsync(browser, "document.getElementById('signupSlugStatus').getAttribute('data-state') === 'taken'", ct);
    }

    [Fact]
    public async Task AnAddressTakenBeforeTheSubmit_ReturnsToTheRestaurantStep_WithEverythingElseKept()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(applicant.E164)}')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);

        // Another registration takes the same address through the API meanwhile.
        await RegisterThroughApiAsync(applicant.Slug, ct);

        await Js(browser, "__su.click('#signupSubmitButton')", ct);
        await WaitForAsync(browser, "__su.current() === 'restaurant'", ct);
        await WaitForAsync(browser, "!document.querySelector('[data-su-address-taken]').hidden", ct);
        Assert.Equal("signupSlug", await ActiveIdAsync(browser, ct));
        Assert.Equal(Text("Signup.SlugUnavailable", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupSlug-error').textContent.trim()", ct));
        Assert.Equal(applicant.BusinessName, await browser.EvaluateAsync<string>("document.getElementById('signupBusinessName').value", ct));

        // A new address, saved; the phone stays verified, and the submit goes through.
        await Js(browser, $"__su.set('signupSlug', '{applicant.Slug}-2')", ct);
        await ContinueAsync(browser, "owner", ct);
        await ContinueAsync(browser, "phone", ct);
        await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        await Js(browser, "__su.click('#signupSubmitButton')", ct);
        await WaitForAsync(browser, "location.pathname.startsWith('/signup/pending/')", ct);
    }

    [Fact]
    public async Task Errors_FocusTheSummaryOrTheOneInvalidField_AndTheSummaryLinksLeadToTheFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        var applicant = Applicant.New();
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);

        // Enter in an empty field: several errors, so the summary takes focus.
        await Js(browser, "document.getElementById('signupBusinessName').focus()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.hasAttribute('data-su-error-summary')", ct);
        Assert.True(await browser.EvaluateAsync<int>("document.querySelectorAll('#signupStep-restaurant [data-su-error-summary] a').length", ct) >= 2);

        // Tab to the first link and follow it: focus moves to that field.
        await browser.PressKeyAsync("Tab", shift: false, ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.hasAttribute('data-su-error-link')", ct);
        Assert.True(await browser.EvaluateAsync<bool>("document.activeElement.matches(':focus-visible')", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.hasAttribute('data-su-field')", ct);

        // Everything but the street: the one invalid field gets focus, with its message as its description.
        await FillRestaurantAsync(browser, applicant, ct);
        await Js(browser, "__su.set('signupStreetAddress', '')", ct);
        await Js(browser, "document.getElementById('signupBusinessName').focus()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupStreetAddress'", ct);
        Assert.Contains("signupStreetAddress-error", await browser.EvaluateAsync<string>("document.getElementById('signupStreetAddress').getAttribute('aria-describedby')", ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
    }

    [Theory]
    [InlineData("ar-SA", 390, 844)]
    [InlineData("tr-TR", 360, 740)]
    public async Task OnAPhone_TheStripCarriesTheAmountAndTheAction_TheSheetReturnsFocus_AndNothingScrollsSideways(string culture, int width, int height)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile: true, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);

        var state = await LayoutAsync(browser, ct);
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", state.GetProperty("dir").GetString());
        Assert.True(state.GetProperty("overflow").GetDouble() <= 0, $"The plan step scrolls sideways at {width} px.");
        Assert.True(state.GetProperty("stripVisible").GetBoolean());
        Assert.True(state.GetProperty("stripHeight").GetDouble() > 40);
        Assert.Equal(state.GetProperty("stripHeight").GetDouble(), state.GetProperty("bodyPadding").GetDouble(), 1.0);
        Assert.Equal(Text("Signup.Strip.Continue", culture), state.GetProperty("stripAction").GetString());

        // The amount opens the summary sheet; Escape closes it and focus returns to the strip.
        await Js(browser, "document.querySelector('[data-su-sheet-open]').focus()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "document.querySelector('.su-sheet').open", ct);
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitForAsync(browser, "!document.querySelector('.su-sheet').open && document.activeElement && document.activeElement.hasAttribute('data-su-sheet-open')", ct);

        // The strip's action continues the step.
        await Js(browser, "__su.click('[data-su-strip-action]')", ct);
        await WaitForAsync(browser, "__su.current() === 'restaurant'", ct);
        state = await LayoutAsync(browser, ct);
        Assert.True(state.GetProperty("overflow").GetDouble() <= 0, $"The restaurant step scrolls sideways at {width} px.");
        Assert.Equal("ltr", state.GetProperty("slugDirection").GetString());
        Assert.Equal("ltr", state.GetProperty("phoneDirection").GetString());
        if (culture == "ar-SA")
            Assert.NotEqual("none", state.GetProperty("backIconTransform").GetString());
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Applicant(string BusinessName, string Slug, string Digits, string Email)
    {
        public string E164 => "+90" + Digits;

        public string Grouped => $"{Digits[..3]} {Digits[3..6]} {Digits[6..8]} {Digits[8..]}";

        public string Display => "+90 " + Grouped;

        public static Applicant New()
        {
            var token = Guid.NewGuid().ToString("N")[..10];
            var digits = "5" + Random.Shared.Next(0, 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
            return new Applicant($"Lezzet {token[..4]}", $"lezzet-{token}", digits, $"owner.{token}@example.test");
        }
    }

    private async Task FillRestaurantAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct)
    {
        await Js(browser, $$"""
            (() => {
              __su.set('signupBusinessName', '{{applicant.BusinessName}}');
              const toggle = document.querySelector('[data-category-toggle]');
              if (!toggle.checked) toggle.click();
              const subtype = document.querySelector('input[data-category="' + toggle.getAttribute('data-category-toggle') + '"]');
              if (!subtype.checked) subtype.click();
              __su.set('signupSlug', '{{applicant.Slug}}');
              document.getElementById('signupBusinessPhoneType-Landline').click();
              __su.set('signupBusinessPhone', '2125551234');
              __su.set('signupCityId', '{{CityId}}');
              return true;
            })()
            """, ct);
        await WaitForAsync(browser, $"!!document.querySelector('#signupDistrictId option[value=\"{DistrictId}\"]') && !document.getElementById('signupDistrictId').disabled", ct);
        await Js(browser, $"__su.set('signupDistrictId', '{DistrictId}')", ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Moda Cad.')", ct);
    }

    private static async Task FillOwnerAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct) =>
        await Js(browser, $$"""
            (() => {
              __su.set('signupOwnerFullName', 'Ayşe Demir');
              __su.set('signupOwnerEmail', '{{applicant.Email}}');
              __su.set('signupOwnerPhone', '{{applicant.Digits}}');
              __su.set('signupPassword', 'Fake-pass-194');
              __su.set('signupConfirmPassword', 'Fake-pass-194');
              return true;
            })()
            """, ct);

    private async Task ReachPhoneStepAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct, bool expectCode = true)
    {
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, applicant, ct);
        await ContinueAsync(browser, "owner", ct);
        await FillOwnerAsync(browser, applicant, ct);
        await ContinueAsync(browser, "phone", ct);
        if (expectCode)
            await WaitForAsync(browser, "document.getElementById('signupStep-phone').getAttribute('data-phone-state') === 'codeSent'", ct);
    }

    // The open step's main button, then a wait for the next step.
    private static async Task ContinueAsync(HeadlessChromium browser, string expectedStep, CancellationToken ct)
    {
        await Js(browser, "document.querySelector('[data-su-step]:not([hidden]) [data-su-next]').click()", ct);
        await WaitForAsync(browser, $"__su.current() === '{expectedStep}'", ct);
    }

    // Another applicant registers through the API with the given address (as a second browser would).
    private async Task RegisterThroughApiAsync(string slug, CancellationToken ct)
    {
        var other = Applicant.New();
        var client = new TestBrowser(_host.BaseAddress);
        var token = await client.GetAntiforgeryTokenAsync("/signup", Host);
        async Task<TestResponse> Send(HttpMethod method, string path, object? body)
        {
            var response = await client.SendJsonAsync(method, path, body, token, Host);
            Assert.True(response.Status == HttpStatusCode.OK, $"{path} answered {(int)response.Status}: {response.Body}");
            return response;
        }

        await Send(HttpMethod.Put, "/signup/draft/plan", new Dictionary<string, object?> { ["planCode"] = "Starter", ["billingPeriod"] = "Monthly" });
        await Send(HttpMethod.Put, "/signup/draft/restaurant", new Dictionary<string, object?>
        {
            ["businessName"] = other.BusinessName,
            ["businessTypeCodes"] = new[] { "burger" },
            ["businessPhoneType"] = "Landline",
            ["businessPhone"] = "212 555 12 34",
            ["slug"] = slug,
            ["country"] = "Türkiye",
            ["cityId"] = CityId,
            ["districtId"] = DistrictId,
            ["streetAddress"] = "Bahariye Cad."
        });
        await Send(HttpMethod.Put, "/signup/draft/owner", new Dictionary<string, object?>
        {
            ["fullName"] = "Mehmet Kaya",
            ["email"] = other.Email,
            ["mobilePhone"] = other.Digits,
            ["password"] = "Fake-pass-194",
            ["confirmPassword"] = "Fake-pass-194"
        });
        await Send(HttpMethod.Post, "/signup/draft/phone/send", null);
        await Send(HttpMethod.Post, "/signup/draft/phone/verify", new Dictionary<string, object?> { ["code"] = Outbox.LatestCodeTo(other.E164) });
        await Send(HttpMethod.Post, "/signup/draft/submit", null);
    }

    private static Task<JsonElement> ReviewAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("""
            (() => {
              const t = key => document.querySelector('[data-su-review="' + key + '"]').textContent.trim();
              return {
                businessName: t('businessName'),
                domain: t('domain'),
                ownerPhone: t('ownerPhone'),
                password: t('password'),
                verified: !document.querySelector('[data-su-review-verified]').hidden
              };
            })()
            """, ct);

    private static Task<JsonElement> LayoutAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("""
            (() => {
              const strip = document.querySelector('[data-su-strip]');
              const action = strip.querySelector('[data-su-strip-action]');
              const back = document.querySelector('[data-su-step]:not([hidden]) .su-back .bi');
              const slug = document.querySelector('#signupSlug').closest('.su-group');
              const phone = document.querySelector('#signupBusinessPhone').closest('.su-group');
              return {
                dir: document.documentElement.getAttribute('dir'),
                overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
                stripVisible: strip.getClientRects().length > 0,
                stripHeight: strip.getBoundingClientRect().height,
                bodyPadding: parseFloat(getComputedStyle(document.body).paddingBottom),
                stripAction: action.hidden ? '' : action.textContent.trim(),
                slugDirection: getComputedStyle(slug).direction,
                phoneDirection: getComputedStyle(phone).direction,
                backIconTransform: back ? getComputedStyle(back).transform : 'none'
              };
            })()
            """, ct);

    private static Task<string> CurrentAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>("__su.current()", ct);

    private static Task<string> ActiveIdAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>("document.activeElement ? document.activeElement.id : ''", ct);

    private static async Task Js(HeadlessChromium browser, string expression, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>("(() => { " + expression.TrimEnd(';') + "; return true; })()", ct);

    private static async Task OpenAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri($"http://{Host}:{Port}{path}"), ct);
        await WaitForAsync(browser, "!!window.__su && !!document.querySelector('[data-su-root]') && document.querySelector('[data-su-step]:not([hidden])') !== null", ct);
    }

    private static async Task WaitForAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        if (await browser.WaitForAsync(expression, Wait, ct))
            return;
        var state = await browser.EvaluateAsync<string>(
            "JSON.stringify({ step: window.__su && __su.current(), path: location.pathname + location.search, alert: (document.querySelector('[data-su-page-alert-text]') || {}).textContent, phone: (document.querySelector('[data-su-phone-alert-text]') || {}).textContent, active: document.activeElement && (document.activeElement.id || document.activeElement.tagName) })", ct);
        Assert.Fail($"Timed out waiting for {expression}; page: {state}");
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, bool mobile, CancellationToken ct) =>
        await StartBrowserAsync(culture, width, height, mobile, ct, extraScript: null);

    private async Task<HeadlessChromium> StartBrowserAsync(string culture, int width, int height, bool mobile, CancellationToken ct, string? extraScript)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the signup flow checks.");

        Port = _host.BaseAddress.Port;
        var browser = await HeadlessChromium.StartAsync(executable, ct);
        return await browser.SetUpAsync(async started =>
        {
            var cookie = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            await started.SetCookieAsync(new Uri($"http://{Host}:{Port}/"), CookieRequestCultureProvider.DefaultCookieName, cookie, ct);
            await started.SetViewportAsync(width, height, mobile, ct);
            await started.AddScriptBeforePageScriptsAsync(PageHelpers, ct);
            if (extraScript is not null)
                await started.AddScriptBeforePageScriptsAsync(extraScript, ct);
        });
    }

    private static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException(key);
}
