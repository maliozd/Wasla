using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.UnitTests.Signup;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The billing period tabs of the public pricing section (issue #65), as the real Production host renders them: every
/// plan card carries both periods' prices, already worded by the server, and only the monthly block is visible until
/// the tabs choose another period. The prices come from a fixed <see cref="IBillingPlanPriceReader"/>: Starter has a
/// monthly and a yearly price with different VAT choices, Pro only a monthly one, Pro+ none.
/// </summary>
public sealed partial class PricingPeriodTabsRenderedTests : IAsyncLifetime
{
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly string[] SelfServicePlans = ["Starter", "Pro", "ProPlus"];

    internal static readonly BillingPriceQuote[] MixedPrices =
    [
        Quote("Starter", "Monthly", 300m, vatIncluded: true),
        Quote("Starter", "Yearly", 2750m, vatIncluded: false),
        Quote("Pro", "Monthly", 1234.50m, vatIncluded: true)
    ];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-pricing-tabs-keys-").FullName;
    private readonly FixedPlanPrices _prices = new();
    private SignupWebHost _host = null!;

    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
        // The homepage pricing section is part of the full marketing site, which is published here.
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, configuration: SignupWebHost.FullMarketingSite, configureServices: s => s.AddSingleton<IBillingPlanPriceReader>(_prices));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task EveryCard_RendersBothPeriods_WithTheirOwnPriceSuffixAndVat_AndShowsMonthlyByDefault(string culture)
    {
        _prices.Quotes = MixedPrices;

        var home = await GetAsync(culture);

        var starter = Periods(Card(home, "Starter"));
        AssertBlock(starter["Monthly"], hidden: false, priceSet: true, PerMonth(300m, culture), Text("Billing.VatIncluded", culture));
        AssertBlock(starter["Yearly"], hidden: true, priceSet: true, PerYear(2750m, culture), Text("Billing.VatExcluded", culture));
        // The monthly block says nothing about the yearly price, and the reverse.
        Assert.DoesNotContain(Amount(2750m, culture), starter["Monthly"].Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix("Pricing.PricePerYear", culture), starter["Monthly"].Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Amount(300m, culture), starter["Yearly"].Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix("Pricing.PricePerMonth", culture), starter["Yearly"].Markup, StringComparison.Ordinal);

        var pro = Periods(Card(home, "Pro"));
        AssertBlock(pro["Monthly"], hidden: false, priceSet: true, PerMonth(1234.50m, culture), Text("Billing.VatIncluded", culture));
        AssertBlock(pro["Yearly"], hidden: true, priceSet: false, Text("Billing.PriceNotSet", culture), vat: null);

        var proPlus = Periods(Card(home, "ProPlus"));
        AssertBlock(proPlus["Monthly"], hidden: false, priceSet: false, Text("Billing.PriceNotSet", culture), vat: null);
        AssertBlock(proPlus["Yearly"], hidden: true, priceSet: false, Text("Billing.PriceNotSet", culture), vat: null);

        // Enterprise keeps its contact-sales labels: the monthly one and, for yearly, the one signup shows.
        var enterprise = Periods(Card(home, "Enterprise"));
        AssertBlock(enterprise["Monthly"], hidden: false, priceSet: null, Text("Pricing.Plan.Enterprise.Price", culture), vat: null);
        AssertBlock(enterprise["Yearly"], hidden: true, priceSet: null, Text("Signup.PlanPrice.Enterprise.Yearly", culture), vat: null);
    }

    [Fact]
    public async Task WithoutAnyPrice_BothPeriodsOfEverySelfServiceCardSayNotSet_WithoutAnAmountOrVat()
    {
        _prices.Quotes = [];

        var home = await GetAsync("tr-TR");

        foreach (var plan in SelfServicePlans)
        {
            var periods = Periods(Card(home, plan));
            AssertBlock(periods["Monthly"], hidden: false, priceSet: false, Text("Billing.PriceNotSet", "tr-TR"), vat: null);
            AssertBlock(periods["Yearly"], hidden: true, priceSet: false, Text("Billing.PriceNotSet", "tr-TR"), vat: null);
            Assert.DoesNotContain(Text("Billing.CurrencyTry", "tr-TR") + " /", Card(home, plan), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task PeriodTabs_AreHiddenUntilTheScriptRuns_SelectMonthly_AndControlTheCards(string culture)
    {
        _prices.Quotes = MixedPrices;

        var home = await GetAsync(culture);

        var tablist = TabList().Match(home);
        Assert.True(tablist.Success, "No billing period tablist");
        Assert.Contains($"aria-label=\"{Text("Signup.BillingPeriod", culture)}\"", tablist.Value, StringComparison.Ordinal);
        Assert.Matches(@"\shidden(>|\s|=)", tablist.Groups["open"].Value);

        var tabs = Tab().Matches(tablist.Value).ToList();
        Assert.Equal(2, tabs.Count);
        var monthly = tabs[0];
        var yearly = tabs[1];
        Assert.Equal(Text("Signup.BillingMonthly", culture), monthly.Groups["label"].Value);
        Assert.Equal(Text("Signup.BillingYearly", culture), yearly.Groups["label"].Value);
        Assert.Contains("id=\"pricing-period-monthly\" aria-selected=\"true\" aria-controls=\"pricing-plans\" tabindex=\"0\"", monthly.Value, StringComparison.Ordinal);
        Assert.Contains("id=\"pricing-period-yearly\" aria-selected=\"false\" aria-controls=\"pricing-plans\" tabindex=\"-1\"", yearly.Value, StringComparison.Ordinal);
        Assert.Contains("type=\"button\"", monthly.Value, StringComparison.Ordinal);
        Assert.Contains($"data-announce=\"{Text("Pricing.PeriodShownMonthly", culture)}\"", monthly.Value, StringComparison.Ordinal);
        Assert.Contains($"data-announce=\"{Text("Pricing.PeriodShownYearly", culture)}\"", yearly.Value, StringComparison.Ordinal);

        // The controlled region holds every plan card; the announcement region starts empty.
        var panel = home.IndexOf("<div class=\"wm-pricing\" id=\"pricing-plans\"", StringComparison.Ordinal);
        Assert.True(panel > tablist.Index, "The cards do not follow the tabs");
        Assert.All(new[] { "Starter", "Pro", "ProPlus", "Enterprise" },
            plan => Assert.True(home.IndexOf($"aria-labelledby=\"plan-{plan}\"", StringComparison.Ordinal) > panel, plan));
        Assert.Contains("<p class=\"wm-visually-hidden\" role=\"status\" data-wm-period-status></p>", home, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanSignupLinks_StillNameOnlyThePlan()
    {
        _prices.Quotes = MixedPrices;

        var home = await GetAsync("tr-TR");

        foreach (var plan in SelfServicePlans)
            Assert.Contains($"href=\"/signup?plan={plan}\"", Card(home, plan), StringComparison.Ordinal);
    }

    internal static BillingPriceQuote Quote(string plan, string period, decimal amount, bool vatIncluded) =>
        new(Guid.NewGuid(), plan, period, new BillingPrice(amount, "TRY"), vatIncluded);

    private static void AssertBlock(PeriodBlock block, bool hidden, bool? priceSet, string price, string? vat)
    {
        Assert.Equal(hidden, block.Hidden);
        Assert.Equal(priceSet switch { null => null, true => "true", false => "false" }, block.PriceSet);
        Assert.Equal(price, block.Price);
        Assert.Equal(vat, block.Vat);
    }

    private async Task<string> GetAsync(string culture)
    {
        using var browser = _host.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        var page = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        return WebUtility.HtmlDecode(page.Body);
    }

    /// <summary>One plan's pricing card.</summary>
    private static string Card(string body, string planCode)
    {
        var start = body.IndexOf($"aria-labelledby=\"plan-{planCode}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No pricing card for {planCode}");
        return body[start..body.IndexOf("</article>", start, StringComparison.Ordinal)];
    }

    private static Dictionary<string, PeriodBlock> Periods(string card)
    {
        var blocks = PeriodBlockTag().Matches(card).Select(m =>
        {
            var period = m.Groups["period"].Value;
            var attributes = m.Groups["attrs"].Value;
            var body = m.Groups["body"].Value;
            var price = Regex.Matches(body, $"<p class=\"wm-plan__price\" data-plan-price=\"{period}\">(?<t>.*?)</p>", RegexOptions.Singleline);
            var vat = Regex.Matches(body, $"<p class=\"wm-text-s\" data-plan-vat=\"{period}\">(?<t>.*?)</p>", RegexOptions.Singleline);
            Assert.Single(price);
            Assert.True(vat.Count <= 1, $"{period}: more than one VAT line");
            var priceSet = Regex.Match(attributes, "data-price-set=\"(?<v>[a-z]+)\"");
            return new PeriodBlock(
                period,
                Regex.IsMatch(attributes, @"\shidden(\s|=|$)"),
                priceSet.Success ? priceSet.Groups["v"].Value : null,
                price[0].Groups["t"].Value,
                vat.Count == 1 ? vat[0].Groups["t"].Value : null,
                body);
        }).ToList();

        Assert.Equal(new[] { "Monthly", "Yearly" }, blocks.Select(b => b.Period).ToArray());
        return blocks.ToDictionary(b => b.Period, StringComparer.Ordinal);
    }

    internal static string PerMonth(decimal amount, string culture) =>
        string.Format(CultureInfo.GetCultureInfo(culture), Text("Pricing.PricePerMonth", culture), Amount(amount, culture));

    internal static string PerYear(decimal amount, string culture) =>
        string.Format(CultureInfo.GetCultureInfo(culture), Text("Pricing.PricePerYear", culture), Amount(amount, culture));

    private static string Amount(decimal amount, string culture)
    {
        var c = CultureInfo.GetCultureInfo(culture);
        return amount.ToString(decimal.Truncate(amount) == amount ? "N0" : "N2", c) + " " + Text("Billing.CurrencyTry", culture);
    }

    /// <summary>The period suffix of a price format ("/ ay" from "{0} / ay").</summary>
    private static string Suffix(string key, string culture) => Text(key, culture).Replace("{0}", string.Empty, StringComparison.Ordinal).Trim();

    internal static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException(key);

    private sealed record PeriodBlock(string Period, bool Hidden, string? PriceSet, string Price, string? Vat, string Markup);

    internal sealed class FixedPlanPrices : IBillingPlanPriceReader
    {
        public IReadOnlyList<BillingPriceQuote> Quotes { get; set; } = [];

        public Task<IReadOnlyList<BillingPriceQuote>> GetPricesInEffectAsync(CancellationToken ct) => Task.FromResult(Quotes);
    }

    [GeneratedRegex("<div class=\"wm-plan__period\" data-plan-period=\"(?<period>[A-Za-z]+)\"(?<attrs>[^>]*)>(?<body>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex PeriodBlockTag();

    [GeneratedRegex("(?<open><div class=\"wm-period-tabs\" role=\"tablist\"[^>]*>).*?</div>", RegexOptions.Singleline)]
    private static partial Regex TabList();

    [GeneratedRegex("<button [^>]*role=\"tab\"[^>]*>(?<label>[^<]*)</button>", RegexOptions.Singleline)]
    private static partial Regex Tab();
}
