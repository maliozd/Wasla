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
    private const int NeighborhoodId = 94511;
    private const string NeighborhoodName = "Caferağa";
    private const int StreetId = 945111;
    private const string StreetName = "Moda Caddesi"; // name "Moda", type "Caddesi", as the page lists it and the server stores it
    private const int OtherDistrictId = 9452;
    private const string OtherNeighborhoodName = "Salacak";
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
        db.Streets.Add(new Street { Id = StreetId, NeighborhoodId = NeighborhoodId, Name = "Moda", StreetType = "Caddesi", IsActive = true });
        db.Districts.Add(new District { Id = OtherDistrictId, CityId = CityId, Name = "Üsküdar", IsActive = true });
        db.Neighborhoods.Add(new Neighborhood { Id = 94521, DistrictId = OtherDistrictId, Name = OtherNeighborhoodName, IsActive = true });
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
        await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList option[value=\"{NeighborhoodName}\"]')", ct);
        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);

        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);

        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal("Yeni Sokak 1", saved.GetProperty("streetAddress").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("neighborhood").ValueKind);
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
        var deadline = DateTime.UtcNow + Wait;
        while (_lookups.Cancelled == 0 && DateTime.UtcNow < deadline
               && !await browser.EvaluateAsync<bool>($"window.__suAnswers.includes('/signup/neighborhoods?districtId={DistrictId}')", ct))
            await Task.Delay(50, ct);

        Assert.Equal(OtherDistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));
        Assert.True(await browser.EvaluateAsync<bool>($"!!document.querySelector('#signupNeighborhoodList option[value=\"{OtherNeighborhoodName}\"]') && !document.querySelector('#signupNeighborhoodList option[value=\"{NeighborhoodName}\"]')", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhood", ct));
        Assert.Equal("", await ValueAsync(browser, "signupNeighborhoodId", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetAddress", ct));
        Assert.Equal("", await ValueAsync(browser, "signupStreetId", ct));
        Assert.Equal(1, _lookups.Cancelled);

        await Js(browser, "__su.set('signupStreetAddress', 'Yeni Sokak 1')", ct);
        await Js(browser, $"{NextButton}.click()", ct);
        await WaitForAsync(browser, "__su.current() === 'owner'", ct);
        var saved = await SavedRestaurantAsync(browser, ct);
        Assert.Equal(OtherDistrictId, saved.GetProperty("districtId").GetInt32());
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

        Assert.True(await browser.EvaluateAsync<bool>($"!!document.querySelector('#signupNeighborhoodList option[value=\"{OtherNeighborhoodName}\"]') && !document.querySelector('#signupNeighborhoodList option[value=\"{NeighborhoodName}\"]')", ct));
        Assert.Equal(OtherDistrictId.ToString(CultureInfo.InvariantCulture), await ValueAsync(browser, "signupDistrictId", ct));
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

    private async Task ReachPhoneStepAsync(HeadlessChromium browser, Applicant applicant, CancellationToken ct, bool expectCode = true, bool withNeighborhood = false, bool withStreet = false)
    {
        await OpenAsync(browser, "/signup?plan=Pro", ct);
        await ContinueAsync(browser, "restaurant", ct);
        await FillRestaurantAsync(browser, applicant, ct);
        if (withNeighborhood)
        {
            await WaitForAsync(browser, $"!!document.querySelector('#signupNeighborhoodList option[value=\"{NeighborhoodName}\"]')", ct);
            await Js(browser, $"__su.set('signupNeighborhood', '{NeighborhoodName}')", ct);
            await WaitForAsync(browser, $"document.getElementById('signupNeighborhoodId').value === '{NeighborhoodId}'", ct);
            if (withStreet)
            {
                // The street chosen from the list, so the draft holds its id as well.
                await WaitForAsync(browser, $"!!document.querySelector('#signupStreetList option[value=\"{StreetName}\"]')", ct);
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
