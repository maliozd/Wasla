using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Sms;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
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
/// the searchable address lists (keyboard choice, catalog ids, a failed district list and its retry, the required
/// neighbourhood, the list's place on screen); the landline's area-code hint; the checked plan and the receipt after the
/// browser reloads the page from its history; the confirmation after a fresh verification and its cancellation; the
/// password Show control; keyboard and error focus; and Arabic right to left at phone width. Every wait is on page state.
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
    private const int NeighborhoodId = 94511;
    private const string NeighborhoodName = "Caferağa";
    private const int StreetId = 945111;
    private const string StreetName = "Moda Caddesi"; // name "Moda", type "Caddesi", as the page lists it and the server stores it
    private const int OtherDistrictId = 9452;
    private const string OtherNeighborhoodName = "Salacak";
    private const int OtherNeighborhoodId = 94521;
    private const int OtherNeighborhoodStreets = 12;
    private const int TypedNeighborhoodId = 94512; // a second neighbourhood of the saved district, with no streets
    private const string TypedNeighborhoodName = "Rasimpaşa";
    private const string TypedOnlyNeighborhoodName = "Moda"; // typed by the visitor; the catalog has no such neighbourhood
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

    private readonly LookupHold _lookups = new();
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
            {
                services.AddSingleton<IBillingPlanPriceReader>(new FixedPlanPrices
                {
                    Quotes =
                    [
                        new(Guid.NewGuid(), "Starter", "Monthly", new BillingPrice(450m, "TRY"), true),
                        new(Guid.NewGuid(), "Pro", "Monthly", new BillingPrice(820m, "TRY"), true),
                        new(Guid.NewGuid(), "Pro", "Yearly", new BillingPrice(8200m, "TRY"), true)
                    ]
                });
                services.AddScoped<ISignupReferenceDataService>(provider =>
                    new HeldReferenceData(ActivatorUtilities.CreateInstance<SignupReferenceDataService>(provider), _lookups));
            });

        // One Turkish city and district for the address selects (the page offers Türkiye only).
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        db.Cities.Add(new City { Id = CityId, CountryCode = "TR", Name = "İstanbul", PhoneAreaCode = "216", IsActive = true });
        db.Districts.Add(new District { Id = DistrictId, CityId = CityId, Name = "Kadıköy", IsActive = true });
        db.Neighborhoods.Add(new Neighborhood { Id = NeighborhoodId, DistrictId = DistrictId, Name = NeighborhoodName, IsActive = true });
        db.Neighborhoods.Add(new Neighborhood { Id = TypedNeighborhoodId, DistrictId = DistrictId, Name = TypedNeighborhoodName, IsActive = true });
        db.Streets.Add(new Street { Id = StreetId, NeighborhoodId = NeighborhoodId, Name = "Moda", StreetType = "Caddesi", IsActive = true });
        db.Districts.Add(new District { Id = OtherDistrictId, CityId = CityId, Name = "Üsküdar", IsActive = true });
        db.Neighborhoods.Add(new Neighborhood { Id = OtherNeighborhoodId, DistrictId = OtherDistrictId, Name = OtherNeighborhoodName, IsActive = true });
        // More streets than the address list shows at once, so it has to scroll.
        for (var i = 1; i <= OtherNeighborhoodStreets; i++)
            db.Streets.Add(new Street { Id = 945_210 + i, NeighborhoodId = OtherNeighborhoodId, Name = $"Salacak {i}", StreetType = "Sokak", IsActive = true });
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

    private const string NextButton = "document.querySelector('#signupStep-restaurant [data-su-next]')";
    private const string AddressAlert = "document.querySelector('[data-su-page-alert]')";

    // Counts the restaurant saves the page sends.
    private const string CountRestaurantSaves = """
        (() => {
          const original = window.fetch.bind(window);
          window.__suAddress = { saves: 0, expire: null };
          window.fetch = (url, init) => {
            if (new URL(String(url), location.href).pathname === '/signup/draft/restaurant') window.__suAddress.saves++;
            return original(url, init);
          };
        })();
        """;

    // The page's limit on restoring the address, fired by the test instead of by the clock. A changed limit is not
    // caught here, so expire stays null and the test fails.
    private const string ControlAddressLimit = """
        (() => {
          const setTimer = window.setTimeout.bind(window);
          window.setTimeout = (callback, ms, ...args) => {
            if (ms !== 15000) return setTimer(callback, ms, ...args);
            window.__suAddress.expire = callback;
            return 0;
          };
        })();
        """;

    // Records each lookup answer the page read, one task after it was read: by then the page's own handlers of it have run.
    private const string TrackAnswers = """
        (() => {
          window.__suAnswers = [];
          const json = Response.prototype.json;
          Response.prototype.json = function () {
            const url = new URL(this.url);
            const read = json.call(this);
            read.then(() => setTimeout(() => window.__suAnswers.push(url.pathname + url.search), 0), () => {});
            return read;
          };
        })();
        """;

    private Task<HeadlessChromium> ReachRestaurantWithHeldLookupAsync(string lookup, string extraScript, CancellationToken ct, bool withNeighborhood = false) =>
        ReachRestaurantAfterReloadAsync(lookups => lookups.Hold(lookup), extraScript, ct, withNeighborhood);

    // Saves every step up to the phone, sets up the lookups, and reopens the restaurant step from the saved draft.
    private async Task<HeadlessChromium> ReachRestaurantAfterReloadAsync(
        Action<LookupHold> beforeReload, string extraScript, CancellationToken ct, bool withNeighborhood = false, bool withStreet = false)
    {
        var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, extraScript);
        return await browser.SetUpAsync(async started =>
        {
            await ReachPhoneStepAsync(started, Applicant.New(), ct, withNeighborhood: withNeighborhood || withStreet, withStreet: withStreet);
            beforeReload(_lookups);
            await OpenAsync(started, "/signup?step=restaurant", ct);
            await WaitForAsync(started, "__su.current() === 'restaurant'", ct);
        });
    }

    private static Task<string> ValueAsync(HeadlessChromium browser, string id, CancellationToken ct) =>
        browser.EvaluateAsync<string>($"document.getElementById('{id}').value", ct);

    // Waits until the page has handled the answer to this lookup: TrackAnswers records it one task after its body was
    // read, so the page's own handlers of that answer have run.
    private static Task AnswerHandledAsync(HeadlessChromium browser, string pathAndQuery, CancellationToken ct) =>
        WaitForAsync(browser, $"window.__suAnswers.includes('{pathAndQuery}')", ct);

    // Waits until the page has handled the answer to this lookup or the server saw one more lookup cancelled than
    // cancelledBefore, so a test can assert what either outcome left in the form.
    private async Task AnswerHandledOrCancelledAsync(HeadlessChromium browser, string pathAndQuery, CancellationToken ct, int cancelledBefore = 0)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (_lookups.Cancelled == cancelledBefore && DateTime.UtcNow < deadline
               && !await browser.EvaluateAsync<bool>($"window.__suAnswers.includes('{pathAndQuery}')", ct))
            await Task.Delay(50, ct);
    }

    private static Task<JsonElement> SavedRestaurantAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("fetch('/signup/draft', { cache: 'no-store' }).then(r => r.json()).then(d => d.restaurant)", ct);

    private static Task<int> RestaurantSavesAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<int>("window.__suAddress.saves", ct);

    private async Task AssertAddressAlertAsync(HeadlessChromium browser, CancellationToken ct, TimeSpan? timeout = null)
    {
        await WaitForAsync(browser, $"!{AddressAlert}.hidden && !{NextButton}.hasAttribute('aria-busy')", ct, timeout);
        Assert.Equal(Text("Signup.Flow.NetworkError", "tr-TR"), await browser.EvaluateAsync<string>("document.querySelector('[data-su-page-alert-text]').textContent.trim()", ct));
        Assert.True(await browser.EvaluateAsync<bool>($"document.activeElement === {AddressAlert}", ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal("", await browser.EvaluateAsync<string>("document.getElementById('signupStreetAddress-error').textContent.trim()", ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));
    }

    private static async Task UntilAsync(Func<int> count, int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (count() < expected && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        Assert.Equal(expected, count());
    }

    // Fires the limit once the server has the lookup it cuts off: a request cancelled before it arrives is never seen there.
    private async Task ExpireAddressLimitAsync(HeadlessChromium browser, int lookupsStarted, CancellationToken ct)
    {
        await UntilAsync(() => _lookups.Started, lookupsStarted, ct);
        await WaitForAsync(browser, "!!window.__suAddress.expire", ct);
        await Js(browser, "window.__suAddress.expire()", ct);
    }

    [Fact]
    public async Task AContinueWhileTheSavedAddressIsRestored_WaitsForIt_InsteadOfReportingTheStreetAsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantWithHeldLookupAsync(NeighborhoodsOf(DistrictId), CountRestaurantSaves, ct);
        await using var _ = browser;
        await WaitForAsync(browser, $"document.getElementById('signupDistrictId').value === '{DistrictId}'", ct);
        Assert.Equal("", await browser.EvaluateAsync<string>("document.getElementById('signupStreetAddress').value", ct));

        // Two clicks while the street is on its way: nothing is reported as missing, and the click is kept.
        await Js(browser, $"{NextButton}.click(); {NextButton}.click()", ct);
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.NotEqual("signupStreetAddress", await ActiveIdAsync(browser, ct));
        Assert.Equal("", await browser.EvaluateAsync<string>("document.getElementById('signupStreetAddress-error').textContent.trim()", ct));
        Assert.Null(await browser.EvaluateAsync<string?>("document.getElementById('signupStreetAddress').getAttribute('aria-invalid')", ct));
        Assert.Equal("true", await browser.EvaluateAsync<string>($"{NextButton}.getAttribute('aria-busy')", ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));

        // A page brought back from the back/forward cache forgets the waiting move, so a new click is one move, not two.
        await Js(browser, "window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))", ct);
        Assert.False(await browser.EvaluateAsync<bool>($"{NextButton}.hasAttribute('aria-busy')", ct));
        await Js(browser, $"{NextButton}.click()", ct);

        _lookups.Release();
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
        Assert.False(await browser.EvaluateAsync<bool>($"{NextButton}.hasAttribute('aria-busy')", ct));
        Assert.Equal("Moda Cad.", (await SavedRestaurantAsync(browser, ct)).GetProperty("streetAddress").GetString());
    }

    [Fact]
    public async Task GoingBackWhileAContinueWaitsForTheSavedAddress_FreesTheStepItWentBackTo_AndTheAnswerMovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, extraScript: CountRestaurantSaves);
        await using var _ = browser;
        await ReachPhoneStepAsync(browser, Applicant.New(), ct);

        _lookups.Hold(NeighborhoodsOf(DistrictId));
        await OpenAsync(browser, "/signup?step=plan", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, $"{NextButton}.getAttribute('aria-busy') === 'true'", ct);

        await Js(browser, "history.back()", ct);
        await WaitForAsync(browser, $"__su.current() === 'plan' && !{NextButton}.hasAttribute('aria-busy')", ct);

        // The plan step works while the address is still on its way.
        await ContinueAsync(browser, "restaurant", ct);

        _lookups.Release();
        await WaitForAsync(browser, "document.getElementById('signupStreetAddress').value === 'Moda Cad.'", ct);
        // One task later, a move that went on would already have changed the step and sent its save.
        await browser.EvaluateAsync<bool>("new Promise(resolve => setTimeout(() => resolve(true), 0))", ct);

        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));
    }

    // On the page's own clock: a lookup whose answer stays open ends the wait, and Enter tries again.
    [Theory]
    [InlineData("neighborhoods")]
    [InlineData("districts")]
    public async Task ASavedAddressThatNeverArrives_EndsTheWaitWithAMessage_AndALaterTryContinues(string lookup)
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantWithHeldLookupAsync(lookup == "districts" ? DistrictsOf(CityId) : NeighborhoodsOf(DistrictId), CountRestaurantSaves, ct);
        await using var _ = browser;

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, $"{NextButton}.getAttribute('aria-busy') === 'true'", ct);

        await AssertAddressAlertAsync(browser, ct, TimeSpan.FromSeconds(25));
        await UntilAsync(() => _lookups.Cancelled, 1, ct);

        // The answer the page gave up on changes nothing.
        _lookups.Release();
        await browser.EvaluateAsync<bool>("new Promise(resolve => setTimeout(() => resolve(true), 300))", ct);
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));

        await Js(browser, "document.getElementById('signupBusinessName').focus()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
        Assert.True(await browser.EvaluateAsync<bool>($"{AddressAlert}.hidden", ct));
        Assert.Equal("Moda Cad.", (await SavedRestaurantAsync(browser, ct)).GetProperty("streetAddress").GetString());
    }

    [Fact]
    public async Task AStreetTypedAfterTheLimit_SurvivesTheLateAnswerAndARetry_WhichStillBringsTheSavedNeighbourhoodBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantWithHeldLookupAsync(NeighborhoodsOf(DistrictId), CountRestaurantSaves + ControlAddressLimit, ct, withNeighborhood: true);
        await using var _ = browser;

        await Js(browser, $"{NextButton}.click()", ct);
        await ExpireAddressLimitAsync(browser, lookupsStarted: 1, ct);
        await AssertAddressAlertAsync(browser, ct);

        // A retry that also runs out ends the same way.
        await Js(browser, "window.__suAddress.expire = null", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, $"{AddressAlert}.hidden", ct);
        await ExpireAddressLimitAsync(browser, lookupsStarted: 2, ct);
        await AssertAddressAlertAsync(browser, ct);
        await UntilAsync(() => _lookups.Cancelled, 2, ct);

        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);
        _lookups.Release();
        await browser.EvaluateAsync<bool>("new Promise(resolve => setTimeout(() => resolve(true), 300))", ct);
        Assert.Equal("Yeni Sokak 1", await browser.EvaluateAsync<string>("document.getElementById('signupStreetAddress').value", ct));

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal("Yeni Sokak 1", saved.GetProperty("streetAddress").GetString());
        Assert.Equal(NeighborhoodName, saved.GetProperty("neighborhood").GetString());
        Assert.Equal(NeighborhoodId, saved.GetProperty("neighborhoodId").GetInt32());
        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoosingTheDistrictAgain_DropsTheSavedAddress_AndTheNewEntryIsSaved(bool beforeTheLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantWithHeldLookupAsync(NeighborhoodsOf(DistrictId), CountRestaurantSaves + ControlAddressLimit, ct, withNeighborhood: true);
        await using var _ = browser;

        await Js(browser, $"{NextButton}.click()", ct);
        if (beforeTheLimit)
        {
            // From the first attempt on (WAS-212): the waiting Continue ends quietly and the restore is cancelled.
            await UntilAsync(() => _lookups.Started, 1, ct);
            await Js(browser, $"__su.set('signupDistrictId', '{DistrictId}')", ct);
            await WaitForAsync(browser, $"!{NextButton}.hasAttribute('aria-busy')", ct);
        }
        else
        {
            await ExpireAddressLimitAsync(browser, lookupsStarted: 1, ct);
            await AssertAddressAlertAsync(browser, ct);
            await Js(browser, $"__su.set('signupDistrictId', '{DistrictId}')", ct);
        }
        Assert.True(await browser.EvaluateAsync<bool>($"{AddressAlert}.hidden", ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        await UntilAsync(() => _lookups.Cancelled, 1, ct);
        _lookups.Release();
        await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{NeighborhoodName}\"]')", ct);
        // The saved neighbourhood went with the saved district: the field is empty, and the new entry names its own.
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhood", ct));
        await Js(browser, "__su.set('signupNeighborhood', 'Yeni Mahalle')", ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal("Yeni Sokak 1", saved.GetProperty("streetAddress").GetString());
        Assert.Equal("Yeni Mahalle", saved.GetProperty("neighborhood").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("neighborhoodId").ValueKind);
    }

    // WAS-211: the lists are only suggestions. A lookup that answers with an error keeps the saved neighbourhood and street,
    // with the ids the server stored for them, and the next save keeps them.
    [Theory]
    [InlineData("neighborhoods")]
    [InlineData("streets")]
    public async Task ASavedAddressWhoseLookupFails_StaysInTheForm_AndTheNextSaveKeepsIt(string failing)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = failing == "streets" ? StreetsOf(NeighborhoodId) : NeighborhoodsOf(DistrictId);
        var browser = await ReachRestaurantAfterReloadAsync(lookups => lookups.Fail(key), CountRestaurantSaves, ct, withStreet: true);
        await using var _ = browser;

        await WaitForAsync(browser, $"document.getElementById('signupNeighborhood').value === '{NeighborhoodName}' && document.getElementById('signupStreetAddress').value === '{StreetName}'", ct);
        Assert.Equal(1, _lookups.Failed);
        Assert.Equal(NeighborhoodId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal(StreetId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupStreetId", ct));

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(NeighborhoodName, saved.GetProperty("neighborhood").GetString());
        Assert.Equal(NeighborhoodId, saved.GetProperty("neighborhoodId").GetInt32());
        Assert.Equal(StreetName, saved.GetProperty("streetAddress").GetString());
        Assert.Equal(StreetId, saved.GetProperty("streetId").GetInt32());
        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
    }

    // WAS-212: from the first attempt on, a street the visitor typed, or typed and cleared, is theirs; the neighbourhood
    // they did not touch still comes back.
    [Theory]
    [InlineData("Yeni Sokak 1")]
    [InlineData("")]
    public async Task AStreetChangedWhileTheSavedAddressIsRestored_IsKept_AndTheNeighbourhoodStillComesBack(string street)
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantAfterReloadAsync(lookups => lookups.Hold(NeighborhoodsOf(DistrictId)), CountRestaurantSaves + TrackAnswers, ct, withStreet: true);
        await using var _ = browser;

        await UntilAsync(() => _lookups.Started, 1, ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);
        if (street.Length == 0)
            await Js(browser, "__su.set('signupStreetAddress', '')", ct);
        _lookups.Release();
        await AnswerHandledAsync(browser, $"/signup/streets?neighborhoodId={NeighborhoodId}", ct);

        Assert.Equal(street, await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        Assert.Equal(NeighborhoodName, await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal(NeighborhoodId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupNeighborhoodId", ct));

        await Js(browser, $"{NextButton}.click()", ct);
        if (street.Length == 0)
        {
            // A street the visitor cleared stays cleared, so the page asks for it instead of saving the old one.
            await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupStreetAddress'", ct);
            Assert.Equal("restaurant", await CurrentAsync(browser, ct));
            Assert.Equal(0, await RestaurantSavesAsync(browser, ct));
            return;
        }

        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(street, saved.GetProperty("streetAddress").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("streetId").ValueKind);
        Assert.Equal(NeighborhoodId, saved.GetProperty("neighborhoodId").GetInt32());
    }

    // WAS-212: another district chosen while the saved one is being restored drops the saved address at once. The saved
    // district's lookup is cancelled, and the page shows only the new district's neighbourhoods.
    [Fact]
    public async Task AnotherDistrictChosenWhileTheSavedAddressIsRestored_GetsOnlyItsOwnNeighbourhoods()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantAfterReloadAsync(
            lookups => lookups.Hold(NeighborhoodsOf(DistrictId), NeighborhoodsOf(OtherDistrictId)), CountRestaurantSaves + TrackAnswers, ct, withStreet: true);
        await using var _ = browser;

        await UntilAsync(() => _lookups.Started, 1, ct);
        await Js(browser, $"__su.set('signupDistrictId', '{OtherDistrictId}')", ct);
        await UntilAsync(() => _lookups.Started, 2, ct);
        _lookups.Release(NeighborhoodsOf(OtherDistrictId));
        await AnswerHandledAsync(browser, $"/signup/neighborhoods?districtId={OtherDistrictId}", ct);
        _lookups.Release(NeighborhoodsOf(DistrictId));
        // The saved district's lookup is over once the page cancelled it or handled its late answer.
        await AnswerHandledOrCancelledAsync(browser, $"/signup/neighborhoods?districtId={DistrictId}", ct);

        Assert.Equal(OtherDistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));
        Assert.True(await browser.EvaluateAsync<bool>($"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{OtherNeighborhoodName}\"]') && !document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{NeighborhoodName}\"]')", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        Assert.Equal(1, _lookups.Cancelled);

        // A Türkiye address names its neighbourhood: a typed one, so nothing of the saved one comes with it.
        await Js(browser, "__su.set('signupNeighborhood', 'Yeni Mahalle')", ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(OtherDistrictId, saved.GetProperty("districtId").GetInt32());
        Assert.Equal("Yeni Mahalle", saved.GetProperty("neighborhood").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("neighborhoodId").ValueKind);
        Assert.Equal("Yeni Sokak 1", saved.GetProperty("streetAddress").GetString());
    }

    // Without a saved address too: the answer for the district chosen first, held until after the answer for the one
    // chosen next, does not replace the neighbourhoods of the later choice.
    [Fact]
    public async Task AnAnswerForTheDistrictChosenBefore_DoesNotReplaceTheNeighbourhoodsOfTheOneChosenAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, extraScript: CountRestaurantSaves + TrackAnswers);
        await using var _ = browser;
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);

        _lookups.Hold(NeighborhoodsOf(DistrictId), NeighborhoodsOf(OtherDistrictId));
        await Js(browser, $"__su.set('signupCityId', '{CityId}')", ct);
        await WaitForAsync(browser, $"!!document.querySelector('#signupDistrictId option[value=\"{OtherDistrictId}\"]') && !document.getElementById('signupDistrictId').disabled", ct);
        await Js(browser, $"__su.set('signupDistrictId', '{DistrictId}')", ct);
        await UntilAsync(() => _lookups.Started, 1, ct);
        await Js(browser, $"__su.set('signupDistrictId', '{OtherDistrictId}')", ct);
        await UntilAsync(() => _lookups.Started, 2, ct);

        _lookups.Release(NeighborhoodsOf(OtherDistrictId));
        await AnswerHandledAsync(browser, $"/signup/neighborhoods?districtId={OtherDistrictId}", ct);
        _lookups.Release(NeighborhoodsOf(DistrictId));
        await AnswerHandledAsync(browser, $"/signup/neighborhoods?districtId={DistrictId}", ct);

        Assert.True(await browser.EvaluateAsync<bool>($"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{OtherNeighborhoodName}\"]') && !document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{NeighborhoodName}\"]')", ct));
        Assert.Equal(OtherDistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));
    }

    // WAS-212: a neighbourhood typed while the saved district (still disabled) or its neighbourhoods are on their way is
    // the visitor's. The city and district they did not touch still come back, the typed neighbourhood gets the id of
    // the district's neighbourhood with that name, and the saved street, which belongs to the old neighbourhood, is
    // not put under it. After the limit, the retry does the same.
    [Theory]
    [InlineData("districts", false)]
    [InlineData("neighborhoods", false)]
    [InlineData("districts", true)]
    public async Task ANeighbourhoodTypedWhileTheSavedAddressIsRestored_IsKeptWithItsOwnId_AndTheSavedDistrictStillComesBack(string held, bool afterTheLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = held == "districts" ? DistrictsOf(CityId) : NeighborhoodsOf(DistrictId);
        var browser = await ReachRestaurantAfterReloadAsync(
            lookups => lookups.Hold(key), CountRestaurantSaves + ControlAddressLimit + TrackAnswers, ct, withStreet: true);
        await using var _ = browser;

        await UntilAsync(() => _lookups.Started, 1, ct);
        Assert.Equal(held == "districts", await browser.EvaluateAsync<bool>("document.getElementById('signupDistrictId').disabled", ct));
        await Js(browser, $"__su.set('signupNeighborhood', '{TypedNeighborhoodName}')", ct);

        var cancelledBefore = 0;
        if (afterTheLimit)
        {
            await Js(browser, $"{NextButton}.click()", ct);
            await ExpireAddressLimitAsync(browser, lookupsStarted: 1, ct);
            await AssertAddressAlertAsync(browser, ct);
            await UntilAsync(() => _lookups.Cancelled, 1, ct);
            cancelledBefore = 1;
            _lookups.Release();
            await Js(browser, $"{NextButton}.click()", ct);
        }
        else
        {
            _lookups.Release();
        }
        await AnswerHandledOrCancelledAsync(browser, $"/signup/streets?neighborhoodId={TypedNeighborhoodId}", ct, cancelledBefore);

        Assert.Equal(CityId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupCityId", ct));
        Assert.Equal(DistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));
        Assert.False(await browser.EvaluateAsync<bool>("document.getElementById('signupDistrictId').disabled", ct));
        Assert.Equal(TypedNeighborhoodName, await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal(TypedNeighborhoodId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        Assert.Equal(cancelledBefore, _lookups.Cancelled);

        // Continue asks for the street of the typed neighbourhood; the retry after the limit already did.
        if (!afterTheLimit)
            await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupStreetAddress'", ct);
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));

        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(DistrictId, saved.GetProperty("districtId").GetInt32());
        Assert.Equal(TypedNeighborhoodName, saved.GetProperty("neighborhood").GetString());
        Assert.Equal(TypedNeighborhoodId, saved.GetProperty("neighborhoodId").GetInt32());
        Assert.Equal("Yeni Sokak 1", saved.GetProperty("streetAddress").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("streetId").ValueKind);
        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
    }

    // WAS-212: a neighbourhood typed while the saved neighbourhood's streets are on their way makes their answer stale,
    // so none of those streets is offered, or given its id, under the new neighbourhood.
    [Fact]
    public async Task ANeighbourhoodTypedWhileTheSavedStreetsAreOnTheirWay_GetsNoneOfThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = await ReachRestaurantAfterReloadAsync(
            lookups => lookups.Hold(StreetsOf(NeighborhoodId)), CountRestaurantSaves + TrackAnswers, ct, withStreet: true);
        await using var _ = browser;

        await UntilAsync(() => _lookups.Started, 1, ct);
        Assert.Equal(NeighborhoodName, await ValueAsync(browser, "signupNeighborhood", ct));
        await Js(browser, "__su.set('signupNeighborhood', 'Yeni Mahalle')", ct);
        _lookups.Release();
        await AnswerHandledOrCancelledAsync(browser, $"/signup/streets?neighborhoodId={NeighborhoodId}", ct);

        Assert.Equal("Yeni Mahalle", await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal(0, await browser.EvaluateAsync<int>("document.querySelectorAll('#signupStreetList [role=option][data-value]').length", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));

        await Js(browser, $"__su.set('signupStreetAddress', '{StreetName}')", ct);
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(DistrictId, saved.GetProperty("districtId").GetInt32());
        Assert.Equal("Yeni Mahalle", saved.GetProperty("neighborhood").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("neighborhoodId").ValueKind);
        Assert.Equal(StreetName, saved.GetProperty("streetAddress").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("streetId").ValueKind);
        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
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
        // The draft read made after the number change is held back, so it answers after the new code was sent: its older
        // phone state must not replace the code-sent state.
        var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, extraScript: """
            (() => {
              const original = window.fetch.bind(window);
              window.fetch = (url, init) => new URL(String(url), location.href).pathname === '/signup/draft' && (!init || !init.method || init.method === 'GET')
                ? new Promise(resolve => setTimeout(() => resolve(original(url, init)), 1500))
                : original(url, init);
            })();
            """);
        await using var _ = browser;
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

    // The address fields are searchable lists. Typing filters them the Turkish way; the arrows only move through the list
    // and Enter chooses, while Escape and Tab choose nothing. A city and a district must come from the list. A
    // neighbourhood or street picked from it keeps its catalog id while its name is in the field; typed text keeps none.
    [Fact]
    public async Task TheAddressFields_AreSearchedAndChosenByKeyboard_PickedNamesKeepTheirIds_TypedTextKeepsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillBusinessAsync(browser, Applicant.New(), ct);

        // The four fields search their own lists. Their autocomplete token is one the browser does not read as an
        // address field, so its saved-address popup stays closed over them (Chrome ignores "off" there).
        foreach (var id in new[] { "signupCityInput", "signupDistrictInput", "signupNeighborhood", "signupStreetAddress" })
            Assert.Equal("wasla-list-search", await AttributeAsync(browser, id, "autocomplete", ct));

        // City: focus alone opens nothing; a lower-case query without Turkish letters finds İstanbul, Enter chooses it
        // and the step stays where it is, with focus in the field.
        await Js(browser, "document.getElementById('signupCityInput').focus()", ct);
        Assert.Equal("false", await AttributeAsync(browser, "signupCityInput", "aria-expanded", ct));
        await browser.InsertTextAsync("istan", ct);
        Assert.Equal("true", await AttributeAsync(browser, "signupCityInput", "aria-expanded", ct));
        Assert.Equal("İstanbul", await ActiveOptionAsync(browser, "signupCityInput", ct));
        Assert.Equal("", await ValueAsync(browser, "signupCityId", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal(CityId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupCityId", ct));
        Assert.Equal("İstanbul", await ValueAsync(browser, "signupCityInput", ct));
        Assert.Equal("false", await AttributeAsync(browser, "signupCityInput", "aria-expanded", ct));
        Assert.Equal("signupCityInput", await ActiveIdAsync(browser, ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));

        // District: Tab reaches it once its list is in. Escape leaves a query without choosing; Enter chooses.
        await WaitForAsync(browser, "!document.getElementById('signupDistrictInput').disabled && document.querySelectorAll('#signupDistrictList [role=option]').length === 2", ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("signupDistrictInput", await ActiveIdAsync(browser, ct));
        await browser.InsertTextAsync("usk", ct);
        Assert.Equal("Üsküdar", await ActiveOptionAsync(browser, "signupDistrictInput", ct));
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.Equal("", await ValueAsync(browser, "signupDistrictInput", ct));
        Assert.Equal("", await ValueAsync(browser, "signupDistrictId", ct));
        await browser.InsertTextAsync("kad", ct);
        Assert.Equal("Kadıköy", await ActiveOptionAsync(browser, "signupDistrictInput", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal(DistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));

        // Neighbourhood: a partial name offers the matching ones and the typed text; nothing is active until an arrow
        // moves, and moving changes nothing in the field. Enter on a name picks it with its id.
        await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{NeighborhoodName}\"]')", ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("signupNeighborhood", await ActiveIdAsync(browser, ct));
        await browser.InsertTextAsync("cafer", ct);
        Assert.False(await browser.EvaluateAsync<bool>("document.getElementById('signupNeighborhoodList-typed').hidden", ct));
        Assert.Equal("", await ActiveOptionAsync(browser, "signupNeighborhood", ct));
        await browser.PressKeyAsync("ArrowDown", shift: false, ct);
        Assert.Equal(NeighborhoodName, await ActiveOptionAsync(browser, "signupNeighborhood", ct));
        Assert.Equal("cafer", await ValueAsync(browser, "signupNeighborhood", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal(NeighborhoodName, await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal(NeighborhoodId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupNeighborhoodId", ct));

        // The same name typed in capitals and without Turkish letters is that catalog entry: no "keep as typed" row, and
        // the id follows it without a pick.
        await Js(browser, "document.getElementById('signupNeighborhood').select()", ct);
        await browser.InsertTextAsync("CAFERAGA", ct);
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('signupNeighborhoodList-typed').hidden", ct));
        Assert.Equal(NeighborhoodId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupNeighborhoodId", ct));
        await browser.PressKeyAsync("Escape", shift: false, ct);

        // Street: picked from the list it has its id; one more character typed after the name drops it.
        await WaitForAsync(browser, $"!!document.querySelector('#signupStreetList [role=option][data-value=\"{StreetName}\"]')", ct);
        await Js(browser, "document.getElementById('signupStreetAddress').focus()", ct);
        await browser.InsertTextAsync("moda", ct);
        await browser.PressKeyAsync("ArrowDown", shift: false, ct);
        Assert.Equal(StreetName, await ActiveOptionAsync(browser, "signupStreetAddress", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal(StreetName, await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal(StreetId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupStreetId", ct));
        await browser.PressKeyAsync("End", shift: false, ct);
        await browser.InsertTextAsync(" 5", ct);
        Assert.Equal(StreetName + " 5", await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.Equal(StreetName + " 5", await ValueAsync(browser, "signupStreetAddress", ct));

        // A neighbourhood the list does not have, kept through its own row: no id, and the street of the picked one goes
        // with it (WAS-212).
        await Js(browser, "const n = document.getElementById('signupNeighborhood'); n.focus(); n.select()", ct);
        await browser.InsertTextAsync("Yukarı Mahalle", ct);
        Assert.Equal("", await ActiveOptionAsync(browser, "signupNeighborhood", ct));
        await browser.PressKeyAsync("ArrowDown", shift: false, ct);
        Assert.Equal("signupNeighborhoodList-typed", await ActiveOptionAsync(browser, "signupNeighborhood", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal("Yukarı Mahalle", await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));
        await Js(browser, "document.getElementById('signupStreetAddress').focus()", ct);
        await browser.InsertTextAsync("Yeni Sokak 3", ct);
        await browser.PressKeyAsync("Escape", shift: false, ct);

        await ContinueAsync(browser, "owner", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(CityId, saved.GetProperty("cityId").GetInt32());
        Assert.Equal(DistrictId, saved.GetProperty("districtId").GetInt32());
        Assert.Equal("Yukarı Mahalle", saved.GetProperty("neighborhood").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("neighborhoodId").ValueKind);
        Assert.Equal("Yeni Sokak 3", saved.GetProperty("streetAddress").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("streetId").ValueKind);
    }

    // A district list that does not load says so at the field, with a retry. The field stays usable and empty, so the
    // step asks for a district instead of saving without one, and the rest of the saved address stays. The retry brings
    // the saved district back and focus stays in the field.
    [Fact]
    public async Task ADistrictListThatFailsToLoad_SaysSo_KeepsTheRest_AndItsRetryBringsTheSavedDistrictBack()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Alert = "document.getElementById('signupDistrictAlert')";
        var browser = await ReachRestaurantAfterReloadAsync(lookups => lookups.Fail(DistrictsOf(CityId)), CountRestaurantSaves, ct, withStreet: true);
        await using var _ = browser;

        await WaitForAsync(browser, $"!{Alert}.hidden", ct);
        await WaitForAsync(browser, $"document.getElementById('signupNeighborhood').value === '{NeighborhoodName}' && document.getElementById('signupStreetAddress').value === '{StreetName}'", ct);
        Assert.Equal(1, _lookups.Failed);
        Assert.Equal(Text("Signup.Address.DistrictsFailed", "tr-TR"), await browser.EvaluateAsync<string>($"{Alert}.querySelector('span').textContent.trim()", ct));
        Assert.Contains("signupDistrictAlert", await AttributeAsync(browser, "signupDistrictInput", "aria-describedby", ct));
        Assert.False(await browser.EvaluateAsync<bool>("document.getElementById('signupDistrictInput').disabled", ct));
        Assert.Equal(CityId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupCityId", ct));
        Assert.Equal("", await ValueAsync(browser, "signupDistrictId", ct));

        // Continue asks for the district at its field and saves nothing.
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupDistrictInput'", ct);
        Assert.Equal(Text("Validation.DistrictRequired", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupDistrictId-error').textContent.trim()", ct));
        Assert.Equal("true", await AttributeAsync(browser, "signupDistrictInput", "aria-invalid", ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));

        // A real click on the retry once the list loads again.
        _lookups.Recover(DistrictsOf(CityId));
        await ClickAsync(browser, $"{Alert}.querySelector('[data-su-combo-retry]')", ct);
        await WaitForAsync(browser, $"document.getElementById('signupDistrictId').value === '{DistrictId}' && {Alert}.hidden", ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupDistrictInput'", ct);
        Assert.Equal("Kadıköy", await ValueAsync(browser, "signupDistrictInput", ct));
        Assert.Null(await AttributeAsync(browser, "signupDistrictInput", "aria-invalid", ct));
        Assert.DoesNotContain("signupDistrictAlert", await AttributeAsync(browser, "signupDistrictInput", "aria-describedby", ct));
        Assert.Equal("", await browser.EvaluateAsync<string>("document.getElementById('signupDistrictId-error').textContent.trim()", ct));
        Assert.Equal(NeighborhoodName, await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal(StreetName, await ValueAsync(browser, "signupStreetAddress", ct));

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(DistrictId, saved.GetProperty("districtId").GetInt32());
        Assert.Equal(NeighborhoodId, saved.GetProperty("neighborhoodId").GetInt32());
        Assert.Equal(StreetId, saved.GetProperty("streetId").GetInt32());
        Assert.Equal(1, await RestaurantSavesAsync(browser, ct));
    }

    // A Türkiye address names its neighbourhood: without one the step stops at that field. Building, floor, door and
    // postal code stay optional.
    [Fact]
    public async Task WithoutANeighbourhood_TheRestaurantStepAsksForIt_AtTheField_AndTheBuildingDetailsStayOptional()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, Applicant.New(), ct);

        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('label[for=signupNeighborhood] .su-optional')", ct));
        Assert.True(await browser.EvaluateAsync<bool>("['signupBuildingNumber', 'signupFloor', 'signupDoorNumber', 'signupPostalCode', 'signupAddressNote', 'signupLocationUrl'].every(id => !!document.querySelector('label[for=' + id + '] .su-optional'))", ct));

        // Emptying the neighbourhood empties the street with it (WAS-212), so the street is typed again.
        await Js(browser, "__su.set('signupNeighborhood', '')", ct);
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));
        await Js(browser, "__su.set('signupStreetAddress', 'Moda Cad.')", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.id === 'signupNeighborhood'", ct);
        Assert.Equal(Text("Validation.NeighborhoodRequired", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupNeighborhood-error').textContent.trim()", ct));
        Assert.Contains("signupNeighborhood-error", await AttributeAsync(browser, "signupNeighborhood", "aria-describedby", ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));

        await Js(browser, $"__su.set('signupNeighborhood', '{TypedOnlyNeighborhoodName}')", ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Moda Cad.')", ct);
        await ContinueAsync(browser, "owner", ct);
        Assert.Equal(TypedOnlyNeighborhoodName, (await SavedRestaurantAsync(browser, ct)).GetProperty("neighborhood").GetString());
    }

    // The address list opens at its field with the field's width, shows six or seven rows and scrolls the rest, and stays
    // on screen: above the field when there is no room below, clear of the phone's bottom strip, right to left in Arabic.
    [Theory]
    [InlineData("ar-SA", 390, 844, true)]
    [InlineData("tr-TR", 1440, 900, false)]
    public async Task TheAddressList_OpensAtItsField_ShowsAboutSevenRows_AndStaysOnScreen(string culture, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillBusinessAsync(browser, Applicant.New(), ct);
        await Js(browser, $"__su.set('signupCityId', '{CityId}')", ct);
        await WaitForAsync(browser, $"!!document.querySelector('#signupDistrictId option[value=\"{OtherDistrictId}\"]') && !document.getElementById('signupDistrictId').disabled", ct);
        await Js(browser, $"__su.set('signupDistrictId', '{OtherDistrictId}')", ct);
        await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{OtherNeighborhoodName}\"]')", ct);
        await Js(browser, $"__su.set('signupNeighborhood', '{OtherNeighborhoodName}')", ct);
        await WaitForAsync(browser, $"document.querySelectorAll('#signupStreetList [role=option]:not([data-su-combo-typed])').length === {OtherNeighborhoodStreets}", ct);
        // Room under the form, so the field can be put anywhere on screen.
        await Js(browser, "document.querySelector('[data-su-root]').insertAdjacentHTML('beforeend', '<div style=\"block-size: 100vh\"></div>')", ct);

        // Near the top of the screen: the list opens under the field. The page scrolls smoothly; these moves are instant.
        await Js(browser, "window.scrollBy({ top: document.getElementById('signupStreetAddress').getBoundingClientRect().top - 120, behavior: 'instant' })", ct);
        await Js(browser, "document.getElementById('signupStreetAddress').focus()", ct);
        await browser.PressKeyAsync("ArrowDown", shift: false, ct);
        var below = await AddressListAsync(browser, ct);
        AssertAtTheField(below);
        Assert.True(below.GetProperty("top").GetDouble() >= below.GetProperty("inputBottom").GetDouble(), $"The list does not open under the field: {below}");
        Assert.True(below.GetProperty("bottom").GetDouble() <= below.GetProperty("viewBottom").GetDouble() + 0.5, $"The list runs off the screen: {below}");
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", below.GetProperty("direction").GetString());
        await browser.PressKeyAsync("Escape", shift: false, ct);

        // Just above the bottom of the screen (on a phone, of the strip): the list opens above the field.
        await Js(browser, $"window.scrollBy({{ top: document.getElementById('signupStreetAddress').getBoundingClientRect().bottom - ({ViewBottom} - 60), behavior: 'instant' }})", ct);
        await browser.PressKeyAsync("ArrowDown", shift: false, ct);
        var above = await AddressListAsync(browser, ct);
        AssertAtTheField(above);
        Assert.True(above.GetProperty("bottom").GetDouble() <= above.GetProperty("inputTop").GetDouble(), $"The list does not open above the field: {above}");
        Assert.True(above.GetProperty("top").GetDouble() >= 0, $"The list runs off the top of the screen: {above}");

        static void AssertAtTheField(JsonElement list)
        {
            Assert.True(list.GetProperty("open").GetBoolean(), $"The list is closed: {list}");
            Assert.Equal(list.GetProperty("inputLeft").GetDouble(), list.GetProperty("left").GetDouble(), 1.0);
            Assert.Equal(list.GetProperty("inputRight").GetDouble(), list.GetProperty("right").GetDouble(), 1.0);
            var rows = list.GetProperty("listHeight").GetDouble() / list.GetProperty("row").GetDouble();
            Assert.True(rows is >= 6 and <= 7.5, $"The list shows {rows:0.0} rows: {list}");
            Assert.True(list.GetProperty("scrolls").GetBoolean(), $"The list does not scroll: {list}");
        }
    }

    // The restaurant's landline after the fixed +90: grouped as it is typed and pasted, with a trunk zero or country code
    // removed instead of stored twice, the caret kept in place, and the same number after switching the type. The hint
    // shows an example with the city's area code, left to right in Arabic too. The saved value is the national number,
    // and the owner's mobile is a separate field.
    [Theory]
    [InlineData("tr-TR", 1440, 900, false)]
    [InlineData("ar-SA", 390, 844, true)]
    public async Task TheBusinessLandline_IsGroupedAsTypedAndPasted_KeepsOnePrefix_AndSurvivesATypeSwitch(string culture, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, Applicant.New(), ct);
        Assert.Equal("212 555 12 34", await ValueAsync(browser, "signupBusinessPhone", ct));

        // Typed with the trunk zero: the zero goes once the number is complete.
        await Js(browser, "const p = document.getElementById('signupBusinessPhone'); p.focus(); p.select()", ct);
        await browser.InsertTextAsync("0212 345 67 89", ct);
        Assert.Equal("212 345 67 89", await ValueAsync(browser, "signupBusinessPhone", ct));

        // A digit typed in the middle: regrouped, the caret right after it.
        await Js(browser, "const p = document.getElementById('signupBusinessPhone'); p.focus(); p.value = '212 345 67 8'; p.setSelectionRange(5, 5)", ct);
        await browser.InsertTextAsync("9", ct);
        Assert.Equal("212 394 56 78", await ValueAsync(browser, "signupBusinessPhone", ct));
        Assert.Equal(6, await browser.EvaluateAsync<int>("document.getElementById('signupBusinessPhone').selectionStart", ct));

        // Pasted with the country code over the whole field.
        await Js(browser, """
            const p = document.getElementById('signupBusinessPhone');
            p.focus(); p.select();
            const data = new DataTransfer(); data.setData('text', '+90 (216) 345-67-89');
            p.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }))
            """, ct);
        Assert.Equal("216 345 67 89", await ValueAsync(browser, "signupBusinessPhone", ct));

        // Switching the type and back changes nothing.
        await Js(browser, "document.getElementById('signupBusinessPhoneType-Mobile').click()", ct);
        Assert.Equal("216 345 67 89", await ValueAsync(browser, "signupBusinessPhone", ct));
        Assert.Equal(Text("Signup.MobilePhoneHint", culture), await browser.EvaluateAsync<string>("document.getElementById('signupPhoneHint').textContent", ct));
        await Js(browser, "document.getElementById('signupBusinessPhoneType-Landline').click()", ct);
        Assert.Equal("216 345 67 89", await ValueAsync(browser, "signupBusinessPhone", ct));

        // The hint names the chosen city's area code: İstanbul has two (the catalog stores one), isolated left to right.
        // The number typed is left as it is.
        var hint = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const hint = document.getElementById('signupPhoneHint');
              const codes = hint.querySelector('bdi[dir=ltr]');
              const text = codes.firstChild;
              const rect = (from, to) => { const r = document.createRange(); r.setStart(text, from); r.setEnd(text, to); return r.getBoundingClientRect(); };
              return { text: hint.textContent, codes: codes.textContent, firstLeft: rect(0, 3).left, secondLeft: rect(6, 9).left };
            })()
            """, ct);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Text("Signup.LandlineAreaCodeHint", culture), "İstanbul", "212 / 216"), hint.GetProperty("text").GetString());
        Assert.Equal("212 / 216", hint.GetProperty("codes").GetString());
        Assert.True(hint.GetProperty("firstLeft").GetDouble() < hint.GetProperty("secondLeft").GetDouble(), $"212 is not shown before 216: {hint}");
        Assert.Equal("216 345 67 89", await ValueAsync(browser, "signupBusinessPhone", ct));

        await ContinueAsync(browser, "owner", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal("Landline", saved.GetProperty("businessPhoneType").GetString());
        Assert.Equal("2163456789", saved.GetProperty("businessPhone").GetString());
        Assert.Equal("", await ValueAsync(browser, "signupOwnerPhone", ct));
    }

    // The checked plan and period, the price its card shows, and what the receipt says.
    private static Task<JsonElement> PlanChoiceAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("""
            (() => {
              const plan = document.querySelector('input[name=PlanCode]:checked');
              const period = document.querySelector('input[name=BillingPeriod]:checked');
              const card = plan.closest('.su-plan');
              const price = [...card.querySelectorAll('.su-plan__price [data-su-period]')].find(e => e.getClientRects().length > 0);
              const receipt = document.getElementById('signupReceipt-title').closest('[data-su-receipt]');
              return {
                plan: plan.value,
                period: period.value,
                planName: card.querySelector('.su-plan__name').textContent.trim(),
                periodShown: document.querySelector('[data-su-root]').getAttribute('data-su-period-shown'),
                cardPeriod: price ? price.getAttribute('data-su-period') : '',
                cardAmount: price ? price.querySelector('.su-plan__amount').textContent.trim() : '',
                receiptPlan: receipt.querySelector('[data-su-text="planLine"]').textContent.trim(),
                receiptAmount: receipt.querySelector('[data-su-text="amount"]').textContent.trim()
              };
            })()
            """, ct);

    // One plan and one period on the whole page: the checked radios, the prices the cards show and the receipt.
    private static void AssertOnePlanChoice(JsonElement choice)
    {
        var period = choice.GetProperty("period").GetString();
        var periodWord = Text(period == "Yearly" ? "Signup.Receipt.PeriodYearly" : "Signup.Receipt.PeriodMonthly", "tr-TR");
        Assert.Equal(period, choice.GetProperty("periodShown").GetString());
        Assert.Equal(period, choice.GetProperty("cardPeriod").GetString());
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Text("Signup.Receipt.PlanLine", "tr-TR"), choice.GetProperty("planName").GetString(), periodWord),
            choice.GetProperty("receiptPlan").GetString());
        Assert.Equal(choice.GetProperty("cardAmount").GetString(), choice.GetProperty("receiptAmount").GetString());
    }

    // Chrome reloads a page from its history when the back/forward cache cannot keep it (an unload listener rules it out
    // here), then checks the radios the visitor last chose: after the page's script drew the receipt, with no change
    // event, leaving one plan checked beside another plan's price. The plan and period radios belong to the draft, so
    // the browser leaves them alone and the page shows one choice.
    [Fact]
    public async Task APlanChosenButNotSaved_IsNotPutBackByTheBrowserAfterAHistoryReload_SoTheReceiptMatchesTheCheckedPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        await OpenAsync(browser, "/signup?plan=Starter", ct);
        var start = await PlanChoiceAsync(browser, ct);
        Assert.Equal("Starter", start.GetProperty("plan").GetString());
        Assert.Equal("Monthly", start.GetProperty("period").GetString());
        AssertOnePlanChoice(start);

        await ClickAsync(browser, "document.querySelector('label[for=signupBillingPeriod-Yearly]')", ct);
        await ClickAsync(browser, "document.querySelector('label[for=signupPlan-Pro]')", ct);
        var chosen = await PlanChoiceAsync(browser, ct);
        Assert.Equal("Pro", chosen.GetProperty("plan").GetString());
        Assert.Equal("Yearly", chosen.GetProperty("period").GetString());
        AssertOnePlanChoice(chosen);

        // Away and back through the history, as a new document: a page kept by the cache would still have its marker.
        await Js(browser, "window.__suSamePage = true; window.addEventListener('unload', () => {})", ct);
        await browser.NavigateAsync(new Uri($"http://{Host}:{Port}/"), ct);
        await WaitForAsync(browser, "location.pathname === '/'", ct);
        await Js(browser, "history.back()", ct);
        await WaitForAsync(browser, "location.pathname === '/signup' && document.readyState === 'complete' && !!window.__su && __su.current() === 'plan'", ct);
        Assert.False(await browser.EvaluateAsync<bool>("window.__suSamePage === true", ct), "The page came back from the back/forward cache: nothing was restored.");
        Assert.Equal("back_forward", await browser.EvaluateAsync<string>("performance.getEntriesByType('navigation')[0].type", ct));

        // The unsaved choice is not put back; what is checked is what the cards and the receipt say.
        var back = await PlanChoiceAsync(browser, ct);
        AssertOnePlanChoice(back);
        Assert.Equal("Starter", back.GetProperty("plan").GetString());
        Assert.Equal("Monthly", back.GetProperty("period").GetString());
    }

    // The page's pause after a fresh verification, held by the test instead of the clock: the timer is recorded, not
    // started. A changed pause is not caught here, so pending stays null and the test fails.
    private const string ControlVerifiedPause = """
        (() => {
          const setTimer = window.setTimeout.bind(window);
          window.__suMove = { pending: null, scheduled: 0 };
          window.setTimeout = (callback, ms, ...args) => {
            if (ms !== 1200) return setTimer(callback, ms, ...args);
            window.__suMove.scheduled++;
            window.__suMove.pending = callback;
            return 0;
          };
        })();
        """;

    private const string MovingText = "document.querySelector('[data-su-phone-moving]').textContent.trim()";

    [Fact]
    public async Task AFreshVerification_ConfirmsOnThePhoneStep_ThenOpensTheReviewAtItsHeading_AndARestoredOneDoesNotWait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, ControlVerifiedPause);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(applicant.E164)}')", ct);

        // The server's answer starts the confirmation: still the phone step, the verified state, the visible line, and
        // its sentence once in the step's status region (the line itself is hidden from assistive technology).
        await WaitForAsync(browser, "!!window.__suMove.pending", ct);
        Assert.Equal("phone", await CurrentAsync(browser, ct));
        Assert.Equal("verified", await browser.EvaluateAsync<string>("document.getElementById('signupStep-phone').getAttribute('data-phone-state')", ct));
        var confirmation = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const line = document.querySelector('[data-su-phone-moving]');
              const live = document.querySelector('[data-su-phone-moving-live]');
              return { line: line.textContent.trim(), shown: !line.hidden && line.getClientRects().length > 0, lineHidden: line.getAttribute('aria-hidden'), live: live.textContent, role: live.getAttribute('role') };
            })()
            """, ct);
        Assert.Equal(Text("Signup.Phone.VerifiedMoving", "tr-TR"), confirmation.GetProperty("line").GetString());
        Assert.True(confirmation.GetProperty("shown").GetBoolean());
        Assert.Equal("true", confirmation.GetProperty("lineHidden").GetString());
        Assert.Equal(Text("Signup.Phone.VerifiedMoving", "tr-TR"), confirmation.GetProperty("live").GetString());
        Assert.Equal("status", confirmation.GetProperty("role").GetString());
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__suMove.scheduled", ct));

        // When the pause ends the review opens with focus on its heading, and the confirmation is gone.
        await Js(browser, "window.__suMove.pending()", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        await WaitForAsync(browser, "document.activeElement === document.getElementById('signupStep-review-title')", ct);
        Assert.Equal("", await browser.EvaluateAsync<string>(MovingText, ct));
        Assert.Equal("", await browser.EvaluateAsync<string>("document.querySelector('[data-su-phone-moving-live]').textContent", ct));

        // A draft that comes back verified does not wait again: the phone step shows the verified number without the
        // confirmation, and its "continue" opens the review at once.
        await OpenAsync(browser, "/signup?step=phone", ct);
        await WaitForAsync(browser, "__su.current() === 'phone' && document.getElementById('signupStep-phone').getAttribute('data-phone-state') === 'verified'", ct);
        Assert.Equal("", await browser.EvaluateAsync<string>(MovingText, ct));
        await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__suMove.scheduled", ct));
    }

    [Fact]
    public async Task LeavingThePhoneStepDuringTheConfirmation_CancelsTheMove_AndItsTimerMovesNothingLater()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, ControlVerifiedPause);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(applicant.E164)}')", ct);
        await WaitForAsync(browser, "!!window.__suMove.pending", ct);

        // Back to the owner step during the confirmation: the move is dropped with its sentence.
        await Js(browser, "__su.click('#signupStep-phone [data-su-go=owner]')", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        Assert.Equal("", await browser.EvaluateAsync<string>(MovingText, ct));

        // The timer that was already running ends: nothing moves.
        await Js(browser, "window.__suMove.pending()", ct);
        await browser.EvaluateAsync<bool>("new Promise(resolve => setTimeout(() => resolve(true), 300))", ct);
        Assert.Equal("owner", await CurrentAsync(browser, ct));

        // Coming back to the verified number does not start another wait; "continue" opens the review once.
        await ContinueAsync(browser, "phone", ct);
        Assert.Equal("verified", await browser.EvaluateAsync<string>("document.getElementById('signupStep-phone').getAttribute('data-phone-state')", ct));
        await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__suMove.scheduled", ct));
    }

    [Theory]
    [InlineData("tr-TR", 1440, 900, false)]
    [InlineData("ar-SA", 390, 844, true)]
    public async Task ThePasswordsShowControl_KeepsTheValueCaretAndFocus_SaysWhatItDoesNext_AndSitsAtTheFieldsEnd(string culture, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile, ct);
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, Applicant.New(), ct);
        await ContinueAsync(browser, "owner", ct);
        await Js(browser, "__su.set('signupPassword', 'Fake-pass-194')", ct);
        const string Toggle = "document.querySelector('[data-target=signupPassword]')";

        // At the field's end (the left in Arabic), inside its box, with the field's text kept clear of it.
        var place = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const input = document.getElementById('signupPassword');
              const toggle = {{Toggle}};
              const i = input.getBoundingClientRect();
              const t = toggle.getBoundingClientRect();
              const rtl = getComputedStyle(input).direction === 'rtl';
              return {
                rtl,
                inside: t.top >= i.top && t.bottom <= i.bottom && t.left >= i.left && t.right <= i.right,
                fromEnd: rtl ? t.left - i.left : i.right - t.right,
                room: parseFloat(getComputedStyle(input).paddingInlineEnd) - t.width,
                height: t.height
              };
            })()
            """, ct);
        Assert.Equal(culture == "ar-SA", place.GetProperty("rtl").GetBoolean());
        Assert.True(place.GetProperty("inside").GetBoolean(), $"The control is not inside the field: {place}");
        Assert.InRange(place.GetProperty("fromEnd").GetDouble(), 0, 12);
        Assert.True(place.GetProperty("room").GetDouble() >= 6, $"The field's text can run under the control: {place}");
        Assert.True(place.GetProperty("height").GetDouble() >= 24, $"The control is too small to press: {place}");

        // A pointer press with the caret in the field: shown, value and caret kept, focus still in the field.
        await Js(browser, "const p = document.getElementById('signupPassword'); p.focus(); p.setSelectionRange(4, 4)", ct);
        await ClickAsync(browser, Toggle, ct);
        var shown = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const p = document.getElementById('signupPassword');
              const t = {{Toggle}};
              return { type: p.type, value: p.value, caret: p.selectionStart, focused: document.activeElement === p, text: t.textContent.trim(), name: t.getAttribute('aria-label') };
            })()
            """, ct);
        Assert.Equal("text", shown.GetProperty("type").GetString());
        Assert.Equal("Fake-pass-194", shown.GetProperty("value").GetString());
        Assert.Equal(4, shown.GetProperty("caret").GetInt32());
        Assert.True(shown.GetProperty("focused").GetBoolean());
        Assert.Equal(Text("Signup.Password.Hide", culture), shown.GetProperty("text").GetString());
        Assert.Equal(Text("Common.HidePassword", culture), shown.GetProperty("name").GetString());

        // From the keyboard: Enter on the control hides it again and the focus stays on the control.
        await Js(browser, $"{Toggle}.focus()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.Equal("password", await browser.EvaluateAsync<string>("document.getElementById('signupPassword').type", ct));
        Assert.Equal("Fake-pass-194", await ValueAsync(browser, "signupPassword", ct));
        Assert.True(await browser.EvaluateAsync<bool>($"document.activeElement === {Toggle}", ct));
        Assert.Equal(Text("Signup.Password.Show", culture), await browser.EvaluateAsync<string>($"{Toggle}.textContent.trim()", ct));
        Assert.Equal(Text("Common.ShowPassword", culture), await browser.EvaluateAsync<string>($"{Toggle}.getAttribute('aria-label')", ct));
        Assert.Equal("owner", await CurrentAsync(browser, ct));
    }

    // Holds the page's address checks while __suAddressHold.on is set; each held check waits for its release().
    private const string HoldAddressChecks = """
        (() => {
          const original = window.fetch.bind(window);
          window.__suAddressHold = { on: false, held: [] };
          window.fetch = (url, init) => {
            const u = new URL(String(url), location.href);
            if (!window.__suAddressHold.on || !u.pathname.endsWith('/address-availability')) return original(url, init);
            return new Promise(resolve => window.__suAddressHold.held.push({ slug: u.searchParams.get('slug'), release: () => resolve(original(url, init)) }));
          };
        })();
        """;

    private const string SlugState = "document.getElementById('signupSlugStatus').getAttribute('data-state')";

    // Every edit of the business name makes the panel address again from it, also after a manual edit (owner's
    // decision); the old availability answer goes at once and a late one never comes back; an empty name empties the
    // address; a draft put back on load keeps its own address until the name is edited again.
    [Fact]
    public async Task EveryBusinessNameEdit_MakesTheAddressAgain_EvenAfterAManualEdit_ButARestoredDraftKeepsItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, HoldAddressChecks);
        var applicant = Applicant.New();
        var token = applicant.BusinessName["Lezzet ".Length..];
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);

        await Js(browser, $"__su.set('signupBusinessName', '{applicant.BusinessName}')", ct);
        Assert.Equal($"lezzet-{token}", await ValueAsync(browser, "signupSlug", ct));
        await WaitForAsync(browser, $"{SlugState} === 'available'", ct);

        // A manual edit, checked and available; then its next spelling's check is held back.
        await Js(browser, $"__su.set('signupSlug', '{applicant.Slug}')", ct);
        await WaitForAsync(browser, $"{SlugState} === 'available'", ct);
        await Js(browser, "window.__suAddressHold.on = true", ct);
        await Js(browser, $"__su.set('signupSlug', '{applicant.Slug}-2')", ct);
        await WaitForAsync(browser, $"window.__suAddressHold.held.some(h => h.slug === '{applicant.Slug}-2')", ct);

        // The business name changes: the manual address is replaced at once and its "available" goes with it.
        await Js(browser, $"__su.set('signupBusinessName', 'Lezzet {token} Kadıköy')", ct);
        Assert.Equal($"lezzet-{token}-kadikoy", await ValueAsync(browser, "signupSlug", ct));
        Assert.NotEqual("available", await browser.EvaluateAsync<string>(SlugState, ct));
        Assert.Contains($"lezzet-{token}-kadikoy.{ApplicationDomain}", await browser.EvaluateAsync<string>("document.getElementById('signupDomainPreview').textContent", ct));

        // The held answer for the old address arrives late: nothing changes. Then the new address's own answer shows.
        await Js(browser, $"window.__suAddressHold.held.find(h => h.slug === '{applicant.Slug}-2').release()", ct);
        await WaitForAsync(browser, $"window.__suAddressHold.held.some(h => h.slug === 'lezzet-{token}-kadikoy')", ct);
        Assert.Equal($"lezzet-{token}-kadikoy", await ValueAsync(browser, "signupSlug", ct));
        Assert.NotEqual("available", await browser.EvaluateAsync<string>(SlugState, ct));
        await Js(browser, $"window.__suAddressHold.held.find(h => h.slug === 'lezzet-{token}-kadikoy').release()", ct);
        await WaitForAsync(browser, $"{SlugState} === 'available'", ct);
        Assert.Contains($"lezzet-{token}-kadikoy.{ApplicationDomain}", await browser.EvaluateAsync<string>("document.querySelector('[data-su-slug-message]').textContent", ct));
        await Js(browser, "window.__suAddressHold.on = false", ct);

        // An empty name empties the address and its answer.
        await Js(browser, "__su.set('signupBusinessName', '')", ct);
        Assert.Equal("", await ValueAsync(browser, "signupSlug", ct));
        Assert.Equal("preview", await browser.EvaluateAsync<string>(SlugState, ct));

        // Saved with a manual address, the draft comes back with it; the next name edit makes it again.
        await FillRestaurantAsync(browser, applicant, ct);
        Assert.Equal(applicant.Slug, await ValueAsync(browser, "signupSlug", ct));
        await ContinueAsync(browser, "owner", ct);
        await OpenAsync(browser, "/signup?step=restaurant", ct);
        await WaitForAsync(browser, "__su.current() === 'restaurant'", ct);
        Assert.Equal(applicant.BusinessName, await ValueAsync(browser, "signupBusinessName", ct));
        Assert.Equal(applicant.Slug, await ValueAsync(browser, "signupSlug", ct));
        await Js(browser, $"__su.set('signupBusinessName', '{applicant.BusinessName} Moda')", ct);
        Assert.Equal($"lezzet-{token}-moda", await ValueAsync(browser, "signupSlug", ct));
    }

    // Holds the answer of a code check while __suVerify.hold is set: the server has already handled it.
    private const string HoldVerifyAnswers = """
        (() => {
          const original = window.fetch.bind(window);
          window.__suVerify = { hold: false, held: null };
          window.fetch = (url, init) => {
            const path = new URL(String(url), location.href).pathname;
            if (!window.__suVerify.hold || !path.endsWith('/phone/verify')) return original(url, init);
            return original(url, init).then(response => new Promise(resolve => { window.__suVerify.held = () => resolve(response); }));
          };
        })();
        """;

    private const string OwnerNext = "document.querySelector('#signupStep-owner [data-su-next]')";
    private const string PhoneState = "document.getElementById('signupStep-phone').getAttribute('data-phone-state')";

    private static Task<JsonElement> SavedDraftAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("fetch('/signup/draft', { cache: 'no-store' }).then(r => r.json())", ct);

    // Back through the account step with the verified number: "continue", the details saved, the phone step showing
    // the verified number with "continue to review" and "change", no new code, no wait and no jump. Another number needs
    // its own code, sent only by the visitor's choice; the review stays closed until it is verified, and a late
    // "verified" for the earlier number verifies nothing. Numbers are compared whole, not by their last digits.
    [Fact]
    public async Task AVerifiedNumber_StaysVerifiedThroughTheAccountStep_AndOnlyANewNumberNeedsItsOwnCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, ControlVerifiedPause + HoldVerifyAnswers);
        var applicant = Applicant.New();
        await ReachPhoneStepAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(applicant.E164)}')", ct);
        await WaitForAsync(browser, "!!window.__suMove.pending", ct);
        await Js(browser, "window.__suMove.pending()", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        var sent = Outbox.MessagesTo(applicant.E164).Count;

        // Back to the account step: the verified number continues, also when typed with a trunk zero.
        await Js(browser, "__su.click('#signupStep-review [data-su-go=owner]')", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        Assert.Equal(Text("Signup.Strip.Continue", "tr-TR"), await browser.EvaluateAsync<string>($"{OwnerNext}.textContent.trim()", ct));
        Assert.False(await browser.EvaluateAsync<bool>($"{OwnerNext}.hasAttribute('data-su-autosend')", ct));
        await Js(browser, "__su.set('signupOwnerFullName', 'Ayşe Kaya')", ct);
        await Js(browser, $"__su.set('signupOwnerEmail', 'yeni.{applicant.Email}')", ct);
        await Js(browser, $"__su.set('signupOwnerPhone', '0{applicant.Digits}')", ct);
        Assert.Equal(applicant.Grouped, await ValueAsync(browser, "signupOwnerPhone", ct));
        Assert.Equal(Text("Signup.Strip.Continue", "tr-TR"), await browser.EvaluateAsync<string>($"{OwnerNext}.textContent.trim()", ct));

        await ContinueAsync(browser, "phone", ct);
        var verified = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const step = document.getElementById('signupStep-phone');
              const change = [...step.querySelectorAll('[data-su-phone-change]')].find(b => !b.hidden && b.getClientRects().length > 0);
              return {
                state: step.getAttribute('data-phone-state'),
                title: step.querySelector('[data-su-phone-title]').textContent.trim(),
                number: step.querySelector('[data-su-phone-full]').textContent.trim(),
                primary: step.querySelector('[data-su-phone-primary]').textContent.trim(),
                change: change ? change.textContent.trim() : '',
                codeShown: !step.querySelector('[data-su-code-field]').hidden
              };
            })()
            """, ct);
        Assert.Equal("verified", verified.GetProperty("state").GetString());
        Assert.Equal(Text("Signup.Phone.TitleVerified", "tr-TR"), verified.GetProperty("title").GetString());
        Assert.Equal(applicant.Display, verified.GetProperty("number").GetString());
        Assert.Equal(Text("Signup.Flow.ToReview", "tr-TR"), verified.GetProperty("primary").GetString());
        Assert.Equal(Text("Signup.Phone.ChangeNumber", "tr-TR"), verified.GetProperty("change").GetString());
        Assert.False(verified.GetProperty("codeShown").GetBoolean());
        Assert.Equal(sent, Outbox.MessagesTo(applicant.E164).Count);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__suMove.scheduled", ct));
        Assert.Equal("phone", await CurrentAsync(browser, ct));
        var draft = await SavedDraftAsync(browser, ct);
        Assert.Equal("verified", draft.GetProperty("phone").GetProperty("state").GetString());
        Assert.Equal("Ayşe Kaya", draft.GetProperty("owner").GetProperty("fullName").GetString());
        Assert.Equal(applicant.Digits, draft.GetProperty("owner").GetProperty("mobilePhone").GetString());

        // Another number: the account step offers to send a code, and typing it sends nothing.
        var second = Applicant.New();
        await Js(browser, "__su.click('#signupStep-phone [data-su-go=owner]')", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        await Js(browser, $"__su.set('signupOwnerPhone', '{second.Digits}')", ct);
        Assert.Equal(Text("Signup.Flow.ToPhone", "tr-TR"), await browser.EvaluateAsync<string>($"{OwnerNext}.textContent.trim()", ct));
        Assert.True(await browser.EvaluateAsync<bool>($"{OwnerNext}.hasAttribute('data-su-autosend')", ct));
        Assert.Empty(Outbox.MessagesTo(second.E164));

        // The visitor's send: one code to the second number. Its check is answered "verified" by the server, but the
        // answer is held while the visitor goes back and changes to a third number.
        await ContinueAsync(browser, "phone", ct);
        await WaitForAsync(browser, $"{PhoneState} === 'codeSent'", ct);
        Assert.Single(Outbox.MessagesTo(second.E164));
        await Js(browser, "window.__suVerify.hold = true", ct);
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(second.E164)}')", ct);
        await WaitForAsync(browser, "!!window.__suVerify.held", ct);
        var third = Applicant.New();
        await Js(browser, "__su.click('#signupStep-phone [data-su-go=owner]')", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        await Js(browser, $"__su.set('signupOwnerPhone', '{third.Digits}')", ct);
        await ContinueAsync(browser, "phone", ct);
        Assert.Equal(third.Digits, (await SavedDraftAsync(browser, ct)).GetProperty("owner").GetProperty("mobilePhone").GetString());

        // The late "verified" for the second number verifies nothing and moves nothing.
        await Js(browser, "window.__suVerify.hold = false; window.__suVerify.held()", ct);
        await WaitForAsync(browser, $"{PhoneState} !== 'verified' && !document.querySelector('[data-su-phone-primary]').hasAttribute('aria-busy')", ct);
        Assert.Equal("phone", await CurrentAsync(browser, ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__suMove.scheduled", ct));
        Assert.NotEqual("verified", (await SavedDraftAsync(browser, ct)).GetProperty("phone").GetProperty("state").GetString());

        // No submit before the third number is verified.
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

        // The third number gets its code by the visitor's send and is verified like any new number.
        if (await browser.EvaluateAsync<string>(PhoneState, ct) != "codeSent")
        {
            await Js(browser, "__su.click('[data-su-phone-primary]')", ct);
            await WaitForAsync(browser, $"{PhoneState} === 'codeSent'", ct);
        }
        Assert.Single(Outbox.MessagesTo(third.E164));
        await Js(browser, $"__su.set('signupPhoneCode', '{Outbox.LatestCodeTo(third.E164)}')", ct);
        await WaitForAsync(browser, "window.__suMove.scheduled === 2", ct);
        await Js(browser, "window.__suMove.pending()", ct);
        await WaitForAsync(browser, "__su.current() === 'review'", ct);
        Assert.Equal(third.Display, (await ReviewAsync(browser, ct)).GetProperty("ownerPhone").GetString());
        Assert.Equal(sent, Outbox.MessagesTo(applicant.E164).Count);
    }

    // A number with more digits than a Türkiye number keeps them all in its field, also through a type switch, and is
    // refused at its field (business phone and owner mobile) instead of being saved as another number.
    [Fact]
    public async Task APhoneNumberTooLong_KeepsEveryDigitInSight_AndIsRefusedAtItsField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await StartBrowserAsync("tr-TR", 1440, 900, mobile: false, ct, CountRestaurantSaves);
        var applicant = Applicant.New();
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, applicant, ct);

        await Js(browser, """
            const p = document.getElementById('signupBusinessPhone');
            p.focus(); p.select();
            const data = new DataTransfer(); data.setData('text', '+90 212 345 67 89 12');
            p.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }))
            """, ct);
        Assert.Equal("212 345 67 89 12", await ValueAsync(browser, "signupBusinessPhone", ct));
        await Js(browser, "document.getElementById('signupBusinessPhoneType-Mobile').click()", ct);
        await Js(browser, "document.getElementById('signupBusinessPhone').blur()", ct);
        await Js(browser, "document.getElementById('signupBusinessPhoneType-Landline').click()", ct);
        Assert.Equal("212 345 67 89 12", await ValueAsync(browser, "signupBusinessPhone", ct));

        await Js(browser, NextButton + ".click()", ct);
        await WaitForAsync(browser, "document.getElementById('signupBusinessPhone-error').textContent.trim().length > 0", ct);
        Assert.Equal(Text("Validation.BusinessPhoneTooLong", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupBusinessPhone-error').textContent.trim()", ct));
        Assert.Equal("signupBusinessPhone", await ActiveIdAsync(browser, ct));
        Assert.Equal("restaurant", await CurrentAsync(browser, ct));
        Assert.Equal(0, await RestaurantSavesAsync(browser, ct));
        Assert.Equal("212 345 67 89 12", await ValueAsync(browser, "signupBusinessPhone", ct));

        // Corrected, it saves as typed.
        await Js(browser, "__su.set('signupBusinessPhone', '212 345 67 89')", ct);
        await ContinueAsync(browser, "owner", ct);
        Assert.Equal("2123456789", (await SavedRestaurantAsync(browser, ct)).GetProperty("businessPhone").GetString());

        // The owner's mobile with one digit too many: kept in sight, refused at its field, and no code is sent.
        await FillOwnerAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupOwnerPhone', '0{applicant.Digits}8')", ct);
        Assert.Equal(applicant.Grouped + " 8", await ValueAsync(browser, "signupOwnerPhone", ct));
        await Js(browser, OwnerNext + ".click()", ct);
        await WaitForAsync(browser, "document.getElementById('signupOwnerPhone-error').textContent.trim().length > 0", ct);
        Assert.Equal(Text("Validation.MobilePhoneInvalid", "tr-TR"), await browser.EvaluateAsync<string>("document.getElementById('signupOwnerPhone-error').textContent.trim()", ct));
        Assert.Equal("owner", await CurrentAsync(browser, ct));
        Assert.Empty(Outbox.MessagesTo(applicant.E164));
    }

    // The bottom of what the visitor sees: the top of the phone's strip when it shows, otherwise the window's bottom.
    private const string ViewBottom = "(() => { const s = document.querySelector('[data-su-strip]'); return s && s.getClientRects().length ? s.getBoundingClientRect().top : innerHeight; })()";

    private static Task<JsonElement> AddressListAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const input = document.getElementById('signupStreetAddress');
              const popup = document.querySelector('#signupStreetCombo [data-su-combo-popup]');
              const list = document.getElementById('signupStreetList');
              const option = list.querySelector('[role=option]:not([hidden])');
              const i = input.getBoundingClientRect();
              const p = popup.getBoundingClientRect();
              return {
                open: !popup.hidden && input.getAttribute('aria-expanded') === 'true',
                inputLeft: i.left, inputRight: i.right, inputTop: i.top, inputBottom: i.bottom,
                left: p.left, right: p.right, top: p.top, bottom: p.bottom,
                listHeight: list.clientHeight,
                row: option ? option.getBoundingClientRect().height : 1,
                scrolls: list.scrollHeight > list.clientHeight + 1,
                viewBottom: {{ViewBottom}},
                direction: getComputedStyle(list).direction
              };
            })()
            """, ct);

    // The option the field's list marks as active (aria-activedescendant): its name, or its id for the typed-text row;
    // empty when none is.
    private static Task<string> ActiveOptionAsync(HeadlessChromium browser, string inputId, CancellationToken ct) =>
        browser.EvaluateAsync<string>($"(() => {{ const id = document.getElementById('{inputId}').getAttribute('aria-activedescendant'); const o = id && document.getElementById(id); return o ? (o.getAttribute('data-value') || o.id) : ''; }})()", ct);

    private static Task<string?> AttributeAsync(HeadlessChromium browser, string id, string name, CancellationToken ct) =>
        browser.EvaluateAsync<string?>($"document.getElementById('{id}').getAttribute('{name}')", ct);

    // A real mouse click in the middle of the element, scrolled into view first (instantly: the page scrolls smoothly):
    // whatever is on top there receives it.
    private static async Task ClickAsync(HeadlessChromium browser, string element, CancellationToken ct)
    {
        var point = await browser.EvaluateAsync<JsonElement>($"(() => {{ const e = {element}; e.scrollIntoView({{ block: 'center', behavior: 'instant' }}); const r = e.getBoundingClientRect(); return {{ x: r.left + r.width / 2, y: r.top + r.height / 2 }}; }})()", ct);
        await browser.ClickAtAsync(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble(), ct);
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

    private static string DistrictsOf(int cityId) => "districts:" + cityId.ToString(CultureInfo.InvariantCulture);

    private static string NeighborhoodsOf(int districtId) => "neighborhoods:" + districtId.ToString(CultureInfo.InvariantCulture);

    private static string StreetsOf(int neighborhoodId) => "streets:" + neighborhoodId.ToString(CultureInfo.InvariantCulture);

    // Keeps the server's answer to chosen address lookups open until they are released: the browser really waits, and a
    // request it gives up on is seen here as cancelled. A failing lookup answers with the server's own error (500).
    private sealed class LookupHold
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);
        private int _started;
        private int _cancelled;
        private int _failed;

        public int Started => Volatile.Read(ref _started);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int Failed => Volatile.Read(ref _failed);

        public void Hold(params string[] lookups)
        {
            lock (_gate)
            {
                foreach (var lookup in lookups)
                    _held[lookup] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _started, 0);
                Volatile.Write(ref _cancelled, 0);
            }
        }

        public void Fail(params string[] lookups)
        {
            lock (_gate)
                _failing.UnionWith(lookups);
        }

        public void Recover(params string[] lookups)
        {
            lock (_gate)
                _failing.ExceptWith(lookups);
        }

        public void Release(string lookup)
        {
            lock (_gate)
            {
                if (_held.Remove(lookup, out var open))
                    open.TrySetResult();
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                foreach (var open in _held.Values)
                    open.TrySetResult();
                _held.Clear();
            }
        }

        public async Task PassAsync(string lookup, CancellationToken ct)
        {
            Task open;
            lock (_gate)
            {
                if (_failing.Contains(lookup))
                {
                    Interlocked.Increment(ref _failed);
                    throw new InvalidOperationException("The test made this address lookup fail.");
                }
                if (!_held.TryGetValue(lookup, out var held))
                    return;
                open = held.Task;
                Interlocked.Increment(ref _started);
            }

            try
            {
                await open.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }
    }

    private sealed class HeldReferenceData(ISignupReferenceDataService inner, LookupHold hold) : ISignupReferenceDataService
    {
        public Task<IReadOnlyList<SignupBusinessTypeOption>> GetActiveBusinessTypesAsync(CancellationToken ct) => inner.GetActiveBusinessTypesAsync(ct);

        public Task<IReadOnlyList<SignupCityOption>> GetActiveCitiesAsync(string countryCode, CancellationToken ct) => inner.GetActiveCitiesAsync(countryCode, ct);

        public async Task<IReadOnlyList<SignupDistrictOption>> GetDistrictsByCityIdAsync(int cityId, CancellationToken ct)
        {
            await hold.PassAsync(DistrictsOf(cityId), ct);
            return await inner.GetDistrictsByCityIdAsync(cityId, ct);
        }

        public async Task<IReadOnlyList<SignupNeighborhoodOption>> GetNeighborhoodsByDistrictIdAsync(int districtId, CancellationToken ct)
        {
            await hold.PassAsync(NeighborhoodsOf(districtId), ct);
            return await inner.GetNeighborhoodsByDistrictIdAsync(districtId, ct);
        }

        public async Task<IReadOnlyList<SignupStreetOption>> GetStreetsByNeighborhoodIdAsync(int neighborhoodId, CancellationToken ct)
        {
            await hold.PassAsync(StreetsOf(neighborhoodId), ct);
            return await inner.GetStreetsByNeighborhoodIdAsync(neighborhoodId, ct);
        }

        public Task<SignupCityOption?> GetCityByIdAsync(int cityId, CancellationToken ct) => inner.GetCityByIdAsync(cityId, ct);

        public Task<IReadOnlyList<SignupBusinessTypeOption>> ResolveBusinessTypesByCodesAsync(IReadOnlyList<string> codes, CancellationToken ct) => inner.ResolveBusinessTypesByCodesAsync(codes, ct);

        public Task<SignupCityDistrictNames?> ResolveCityDistrictAsync(int cityId, int districtId, string countryCode, CancellationToken ct) => inner.ResolveCityDistrictAsync(cityId, districtId, countryCode, ct);

        public Task<SignupNeighborhoodNames?> ResolveNeighborhoodAsync(int districtId, int neighborhoodId, CancellationToken ct) => inner.ResolveNeighborhoodAsync(districtId, neighborhoodId, ct);

        public Task<SignupStreetNames?> ResolveStreetAsync(int neighborhoodId, int streetId, CancellationToken ct) => inner.ResolveStreetAsync(neighborhoodId, streetId, ct);
    }

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

    // The restaurant step without its address.
    private static Task FillBusinessAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct) =>
        Js(browser, $$"""
            (() => {
              __su.set('signupBusinessName', '{{applicant.BusinessName}}');
              const toggle = document.querySelector('[data-category-toggle]');
              if (!toggle.checked) toggle.click();
              const subtype = document.querySelector('input[data-category="' + toggle.getAttribute('data-category-toggle') + '"]');
              if (!subtype.checked) subtype.click();
              __su.set('signupSlug', '{{applicant.Slug}}');
              document.getElementById('signupBusinessPhoneType-Landline').click();
              __su.set('signupBusinessPhone', '2125551234');
              return true;
            })()
            """, ct);

    private async Task FillRestaurantAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct)
    {
        await FillBusinessAsync(browser, applicant, ct);
        await Js(browser, $"__su.set('signupCityId', '{CityId}')", ct);
        await WaitForAsync(browser, $"!!document.querySelector('#signupDistrictId option[value=\"{DistrictId}\"]') && !document.getElementById('signupDistrictId').disabled", ct);
        await Js(browser, $"__su.set('signupDistrictId', '{DistrictId}')", ct);
        // A Türkiye address names its neighbourhood; this one is typed, not on the list, so it has no catalog id.
        await Js(browser, $"__su.set('signupNeighborhood', '{TypedOnlyNeighborhoodName}')", ct);
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

    private async Task ReachPhoneStepAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct, bool expectCode = true, bool withNeighborhood = false, bool withStreet = false)
    {
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, applicant, ct);
        if (withNeighborhood)
        {
            await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList [role=option][data-value=\"{NeighborhoodName}\"]')", ct);
            await Js(browser, $"__su.set('signupNeighborhood', '{NeighborhoodName}')", ct);
            await WaitForAsync(browser, $"document.getElementById('signupNeighborhoodId').value === '{NeighborhoodId}'", ct);
            if (withStreet)
            {
                // The street chosen from the list, so the draft holds its id as well.
                await WaitForAsync(browser, $"!!document.querySelector('#signupStreetList [role=option][data-value=\"{StreetName}\"]')", ct);
                await Js(browser, $"__su.set('signupStreetAddress', '{StreetName}')", ct);
                await WaitForAsync(browser, $"document.getElementById('signupStreetId').value === '{StreetId}'", ct);
            }
            else
            {
                await Js(browser, "__su.set('signupStreetAddress', 'Moda Cad.')", ct);
            }
        }
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
            ["neighborhood"] = "Osmanağa",
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

    private static async Task WaitForAsync(HeadlessChromium browser, string expression, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (await browser.WaitForAsync(expression, timeout ?? Wait, ct))
            return;
        var state = await browser.EvaluateAsync<string>(
            "JSON.stringify({ step: window.__su && __su.current(), path: location.pathname + location.search, alert: (document.querySelector('[data-su-page-alert-text]') || {}).textContent, phone: (document.querySelector('[data-su-phone-alert-text]') || {}).textContent, active: document.activeElement && (document.activeElement.id || document.activeElement.tagName), street: (document.getElementById('signupStreetAddress') || {}).value, streetError: (document.getElementById('signupStreetAddress-error') || {}).textContent })", ct);
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
