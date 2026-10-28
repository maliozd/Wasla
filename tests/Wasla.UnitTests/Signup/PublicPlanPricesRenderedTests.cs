using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Billing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Customer-facing prices follow the Central Admin plan prices (WAS-147), rendered by the real Production host: the
/// public pricing section, the signup plan summary and the checkout summary. Monthly and yearly are separate prices, a
/// scheduled price stays hidden until its date, a missing price is shown as not set without any built-in amount, and a
/// subscription that has started keeps showing its own price and VAT after a later price takes effect. Each test has
/// its own host, CentralDb and clock; prices are set through the real Central Admin service, subscriptions through the
/// real store.
/// </summary>
public sealed partial class PublicPlanPricesRenderedTests : IAsyncLifetime
{
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly string[] SelfServicePlans = ["Starter", "Pro", "ProPlus"];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-plan-price-keys-").FullName;
    private readonly BillingClock _clock = new(DateTime.UtcNow);
    private SignupWebHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => BillingPlanPriceInput.TurkeyDate(_clock.UtcNow);

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
        // The public pricing section is part of the full marketing site, which is published here.
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, configuration: SignupWebHost.FullMarketingSite, configureServices: s => s.AddSingleton<TimeProvider>(_clock));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task WithoutAPrice_EveryPageSaysItIsNotSet_ShowsNoBuiltInAmount_AndSignupStillWorks(string culture)
    {
        var notSet = Text("Billing.PriceNotSet", culture);
        var amountMarker = Text("Billing.CurrencyTry", culture) + " /";

        var home = await GetAsync("/", culture);
        var signup = await GetAsync("/signup", culture);

        Assert.Equal(HttpStatusCode.OK, home.Status);
        foreach (var plan in SelfServicePlans)
        {
            var card = Card(home.Body, plan);
            Assert.Contains(notSet, card, StringComparison.Ordinal);
            Assert.DoesNotContain(amountMarker, card, StringComparison.Ordinal);
            Assert.DoesNotContain(Text("Billing.VatIncluded", culture), card, StringComparison.Ordinal);
        }

        Assert.Contains(Text("Pricing.Plan.Enterprise.Price", culture), Card(home.Body, "Enterprise"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, signup.Status);
        Assert.Contains("id=\"signupForm\"", signup.Body, StringComparison.Ordinal);
        foreach (var plan in SelfServicePlans)
        {
            var summary = PlanSummary(signup.Body, plan);
            Assert.Equal(notSet, Price(summary, "monthly"));
            Assert.Equal(notSet, Price(summary, "yearly"));
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("yearlySaving").ValueKind);
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("monthly").GetProperty("vat").ValueKind);
        }

        var enterprise = PlanSummary(signup.Body, "Enterprise");
        Assert.Equal(Text("Pricing.Plan.Enterprise.Price", culture), Price(enterprise, "monthly"));
        Assert.Equal(Text("Signup.PlanPrice.Enterprise.Yearly", culture), Price(enterprise, "yearly"));
    }

    [Fact]
    public async Task ACentralAdminPriceChange_ReachesHomeSignupAndANewRegistrationsSummary_AndAFuturePriceWaitsForItsDate()
    {
        // Monthly 300 and a yearly 2,750 that is not 10 x monthly, both from today; monthly 400 from in three days.
        await ScheduleAsync("Starter", "Monthly", 30000, vatIncluded: true, Today);
        await ScheduleAsync("Starter", "Yearly", 275000, vatIncluded: true, Today);
        await ScheduleAsync("Starter", "Monthly", 40000, vatIncluded: true, Today.AddDays(3));
        var applicant = await SeedApplicantAsync();

        var homeBefore = Card((await GetAsync("/", "tr-TR")).Body, "Starter");
        var signupBefore = PlanSummary((await GetAsync("/signup", "tr-TR")).Body, "Starter");
        var reviewBefore = await ReviewAsync(applicant);

        Assert.Contains("300 TL / ay", homeBefore, StringComparison.Ordinal);
        // Each period has its own price and VAT line on the card (the yearly one is shown by the period tabs, issue #65).
        Assert.Contains("data-plan-price=\"Yearly\">2.750 TL / yıl</p>", homeBefore, StringComparison.Ordinal);
        Assert.Contains("data-plan-vat=\"Yearly\">KDV dahil</p>", homeBefore, StringComparison.Ordinal);
        Assert.DoesNotContain("400 TL", homeBefore, StringComparison.Ordinal);
        Assert.DoesNotContain("3.000 TL", homeBefore, StringComparison.Ordinal);
        Assert.Contains(Text("Billing.PriceNotSet", "tr-TR"), Card((await GetAsync("/", "tr-TR")).Body, "Pro"), StringComparison.Ordinal);
        Assert.Equal("300 TL / ay", Price(signupBefore, "monthly"));
        Assert.Equal("2.750 TL / yıl", Price(signupBefore, "yearly"));
        Assert.Equal("KDV dahil", signupBefore.GetProperty("yearly").GetProperty("vat").GetString());
        Assert.Equal(Saving("850 TL"), signupBefore.GetProperty("yearlySaving").GetString());
        Assert.Contains("300 TL / ay", reviewBefore, StringComparison.Ordinal);

        _clock.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(3));

        var homeAfter = Card((await GetAsync("/", "tr-TR")).Body, "Starter");
        var signupAfter = PlanSummary((await GetAsync("/signup", "tr-TR")).Body, "Starter");
        var reviewAfter = await ReviewAsync(applicant);

        Assert.Contains("400 TL / ay", homeAfter, StringComparison.Ordinal);
        Assert.Contains("2.750 TL / yıl", homeAfter, StringComparison.Ordinal);
        Assert.Equal("400 TL / ay", Price(signupAfter, "monthly"));
        Assert.Equal(Saving("2.050 TL"), signupAfter.GetProperty("yearlySaving").GetString());
        // No subscription yet, so the summary follows the price in effect.
        Assert.Contains("400 TL / ay", reviewAfter, StringComparison.Ordinal);
        Assert.DoesNotContain("300 TL", reviewAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubscriptionStartedAtA_KeepsShowingAAndItsVat_AfterBTakesEffect_WhileANewRegistrationGetsB()
    {
        var a = await ScheduleAsync("Starter", "Monthly", 30000, vatIncluded: true, Today);
        var first = await SeedApplicantAsync();
        var started = await StoreAsync(store => store.StartForRegistrationAsync(first.Id, BillingTestData.Sandbox, Ct));
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);

        // B: a higher price with the other VAT choice, from two days later.
        var b = await ScheduleAsync("Starter", "Monthly", 40000, vatIncluded: false, Today.AddDays(2));
        _clock.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(2));

        var repeated = await StoreAsync(store => store.StartForRegistrationAsync(first.Id, BillingTestData.Sandbox, Ct));
        var firstView = await StoreAsync(store => store.GetForRegistrationAsync(first.Id, Ct));
        var reloads = new[] { await ReviewAsync(first), await ReviewAsync(first) };

        Assert.Equal(new BillingStartResult(BillingStoreOutcome.Unchanged, started.SubscriptionId), repeated);
        Assert.Equal((a, 300m), (firstView!.PlanPriceId, firstView.Price.Amount));
        Assert.All(reloads, review =>
        {
            Assert.Contains("300 TL / ay", review, StringComparison.Ordinal);
            Assert.Contains("(KDV dahil)", review, StringComparison.Ordinal);
            Assert.DoesNotContain("400 TL", review, StringComparison.Ordinal);
            Assert.DoesNotContain("KDV hariç", review, StringComparison.Ordinal);
        });

        var second = await SeedApplicantAsync();
        var secondReview = await ReviewAsync(second);
        var secondStart = await StoreAsync(store => store.StartForRegistrationAsync(second.Id, BillingTestData.Sandbox, Ct));
        var secondView = await StoreAsync(store => store.GetForRegistrationAsync(second.Id, Ct));

        Assert.Contains("400 TL / ay", secondReview, StringComparison.Ordinal);
        Assert.Contains("(KDV hariç)", secondReview, StringComparison.Ordinal);
        Assert.Equal(BillingStoreOutcome.Applied, secondStart.Outcome);
        Assert.Equal((b, 400m), (secondView!.PlanPriceId, secondView.Price.Amount));
        // The new price is shown to new customers; the first subscription's summary is unchanged.
        Assert.Contains("300 TL / ay", await ReviewAsync(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMarketingPage_StillRenders_WhenPricesCannotBeRead()
    {
        var keyRing = Directory.CreateTempSubdirectory("wasla-plan-price-keys-").FullName;
        await using var failing = await SignupWebHost.StartAsync(
            _connection,
            keyRing,
            configuration: SignupWebHost.FullMarketingSite,
            configureServices: s => s.AddScoped<IBillingPlanPriceReader, UnreachablePrices>());
        using var browser = failing.NewBrowser();

        var home = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, home.Status);
        var body = WebUtility.HtmlDecode(home.Body);
        Assert.Contains(Text("Billing.PriceNotSet", "tr-TR"), Card(home.Body, "Starter"), StringComparison.Ordinal);
        Assert.Contains(Text("Pricing.Plan.Enterprise.Price", "tr-TR"), body, StringComparison.Ordinal);
    }

    private async Task<Guid> ScheduleAsync(string plan, string period, long kurus, bool vatIncluded, DateOnly date)
    {
        await using var db = OpenDb();
        var result = await new BillingPlanPriceAdminService(db, new WaslaPlanCatalog(), _clock, NullLogger<BillingPlanPriceAdminService>.Instance)
            .ScheduleAsync(
                new BillingPlanPriceScheduleCommand(plan, period, kurus.ToString(CultureInfo.InvariantCulture), vatIncluded, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                BillingTestData.AdminId,
                Ct);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return result.PlanPriceId!.Value;
    }

    private async Task<T> StoreAsync<T>(Func<BillingSubscriptionStore, Task<T>> action)
    {
        await using var db = OpenDb();
        return await action(new BillingSubscriptionStore(db, _clock, NullLogger<BillingSubscriptionStore>.Instance));
    }

    private async Task<SentinelApplicant> SeedApplicantAsync()
    {
        var applicant = new SentinelApplicant(PendingRegistrationStatus.AwaitingPayment);
        await using var db = OpenDb();
        db.PendingRegistrations.Add(applicant.Entity);
        await db.SaveChangesAsync(Ct);
        return applicant;
    }

    /// <summary>The checkout summary as the applicant's own browser sees it.</summary>
    private async Task<string> ReviewAsync(SentinelApplicant applicant)
    {
        using var browser = _host.NewBrowser();
        var proof = new SignupRegistrationOwnership(
                _host.Services.GetRequiredService<IDataProtectionProvider>(),
                _host.Services.GetRequiredService<IWebHostEnvironment>())
            .CreateProof(applicant.Id, DateTimeOffset.UtcNow.AddDays(7));
        browser.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, proof);
        var page = await browser.GetAsync($"/checkout/review/{applicant.Id}");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        return WebUtility.HtmlDecode(page.Body);
    }

    private async Task<TestResponse> GetAsync(string path, string culture)
    {
        using var browser = _host.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        return await browser.GetAsync(path);
    }

    private CentralDbContext OpenDb() => new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    /// <summary>The marketing pricing card of one plan, decoded.</summary>
    private static string Card(string html, string planCode)
    {
        var body = WebUtility.HtmlDecode(html);
        var start = body.IndexOf($"aria-labelledby=\"plan-{planCode}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No pricing card for {planCode}");
        return body[start..body.IndexOf("</article>", start, StringComparison.Ordinal)];
    }

    /// <summary>One plan's entry in the signup page's script configuration.</summary>
    private static JsonElement PlanSummary(string html, string planCode)
    {
        var json = SignupConfig().Match(html);
        Assert.True(json.Success, "No signup configuration");
        using var config = JsonDocument.Parse(json.Groups["json"].Value);
        return config.RootElement.GetProperty("plans").EnumerateArray()
            .Single(p => p.GetProperty("code").GetString() == planCode)
            .Clone();
    }

    private static string? Price(JsonElement summary, string period) => summary.GetProperty(period).GetProperty("price").GetString();

    private static string Saving(string amount) =>
        string.Format(CultureInfo.GetCultureInfo("tr-TR"), Text("Pricing.YearlySaving", "tr-TR"), amount);

    private static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException(key);

    [GeneratedRegex("<script type=\"application/json\" id=\"signupFormConfig\">(?<json>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex SignupConfig();

    private sealed class UnreachablePrices : IBillingPlanPriceReader
    {
        public Task<IReadOnlyList<BillingPriceQuote>> GetPricesInEffectAsync(CancellationToken ct) =>
            throw new InvalidOperationException("CentralDb is not reachable.");
    }
}
