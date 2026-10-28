using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Billing;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;
using Wasla.UnitTests.Signup;
using static Wasla.UnitTests.Web.PricingPeriodTabsRenderedTests;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The billing period tabs of the public pricing section (issue #65) in a real headless Chromium browser: the marketing
/// home page on <c>wasla.local</c>, served by <see cref="TenantSessionWebHost"/> (real controllers, views, script and
/// styles, Production environment) with fixed plan prices. A real click on the yearly tab shows every card's yearly
/// price, suffix, VAT text and "price not set" state; the arrow keys (mirrored right to left), Home and End move the
/// selection back and forth with roving focus. Every wait is on page state. Skips only when no Chromium browser is
/// installed (see <see cref="HeadlessChromium.BrowserVariable"/>).
/// </summary>
[Trait("Category", "Browser")]
public sealed class PricingPeriodTabsBrowserTests : IAsyncLifetime
{
    private const string MarketingHost = "wasla.local";
    private const string MonthlyTab = "pricing-period-monthly";
    private const string YearlyTab = "pricing-period-yearly";

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TenantSessionWebHost.StartAsync(
            // The homepage pricing section is part of the full marketing site, which is published here.
            configuration: SignupWebHost.FullMarketingSite,
            configureServices: services =>
            services.AddSingleton<IBillingPlanPriceReader>(new FixedPlanPrices { Quotes = MixedPrices }));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("tr-TR", "ltr", 1280, 900, false)]
    [InlineData("ar-SA", "rtl", 390, 844, true)]
    public async Task TheYearlyTab_ShowsEveryCardsYearlyPrice_AndTheKeyboardMovesTheSelection(string culture, string dir, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile, ct);

        await browser.NavigateAsync(new Uri($"http://{MarketingHost}/"), ct);
        // The script reveals the tabs once it has wired them; until then the page shows the monthly prices only.
        await WaitForAsync(browser, "!!document.querySelector('[data-wm-period-tabs]') && !document.querySelector('[data-wm-period-tabs]').hidden", ct);

        var initial = await StateAsync(browser, ct);
        Assert.Equal(dir, initial.GetProperty("dir").GetString());
        Assert.Equal("tabpanel", initial.GetProperty("panelRole").GetString());
        Assert.Equal("", initial.GetProperty("status").GetString());
        // Right to left, the first tab (monthly) sits on the right.
        Assert.Equal(dir == "ltr", initial.GetProperty("monthlyFirstFromLeft").GetBoolean());
        Assert.True(initial.GetProperty("overflow").GetDouble() <= 0, $"The page scrolls sideways at {width} px.");
        AssertSelected(initial, "Monthly", culture);

        var tabs = await browser.AccessibleNodesAsync("[data-wm-period-tabs] [role='tab']", ct);
        Assert.Equal(new (string?, string)[] { ("tab", Text("Signup.BillingMonthly", culture)), ("tab", Text("Signup.BillingYearly", culture)) },
            tabs.Select(t => (t.Role, t.Name)).ToArray());
        var tablist = Assert.Single(await browser.AccessibleNodesAsync("[data-wm-period-tabs]", ct));
        Assert.Equal(("tablist", Text("Signup.BillingPeriod", culture)), (tablist.Role, tablist.Name));

        // A real click on the yearly tab.
        var center = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const tab = document.getElementById('{{YearlyTab}}');
              tab.scrollIntoView({ block: 'center', behavior: 'instant' });
              const r = tab.getBoundingClientRect();
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """, ct);
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
        await WaitForAsync(browser, $"document.getElementById('{YearlyTab}').getAttribute('aria-selected') === 'true'", ct);

        var yearly = await StateAsync(browser, ct);
        AssertSelected(yearly, "Yearly", culture);
        Assert.Equal(YearlyTab, yearly.GetProperty("active").GetString());
        Assert.Equal(Text("Pricing.PeriodShownYearly", culture), yearly.GetProperty("status").GetString());
        // Only the shown period's prices are in the accessibility tree.
        var prices = await browser.AccessibleNodesAsync("#pricing-plans [data-plan-price]", ct);
        Assert.Equal(4, prices.Count(p => !p.Ignored));

        // The keyboard: Home and End, then each arrow key (mirrored right to left), wrapping around.
        foreach (var (key, period) in new[] { ("Home", "Monthly"), ("End", "Yearly"), ("ArrowLeft", "Monthly"), ("ArrowRight", "Yearly"), ("ArrowRight", "Monthly") })
        {
            var tab = period == "Monthly" ? MonthlyTab : YearlyTab;
            await browser.PressKeyAsync(key, shift: false, ct);
            await WaitForAsync(browser, $"document.activeElement && document.activeElement.id === '{tab}' && document.activeElement.getAttribute('aria-selected') === 'true'", ct);

            var state = await StateAsync(browser, ct);
            AssertSelected(state, period, culture);
            Assert.True(state.GetProperty("focusVisible").GetBoolean(), $"{key}: the focused tab has no visible focus.");
            Assert.NotEqual("none", state.GetProperty("outline").GetString());
            Assert.Equal(Text(period == "Monthly" ? "Pricing.PeriodShownMonthly" : "Pricing.PeriodShownYearly", culture), state.GetProperty("status").GetString());
        }

        // Only the selected tab is in the tab sequence: Tab leaves the tab list.
        await browser.PressKeyAsync("Tab", shift: false, ct);
        await WaitForAsync(browser, "document.activeElement && document.activeElement.closest('[role=\"tablist\"]') === null", ct);
        AssertSelected(await StateAsync(browser, ct), "Monthly", culture);
    }

    /// <summary>The selected tab, the roving tab stops, the panel's label, and what each card shows.</summary>
    private static void AssertSelected(JsonElement state, string period, string culture)
    {
        var selectedId = period == "Monthly" ? MonthlyTab : YearlyTab;
        var tabs = state.GetProperty("tabs").EnumerateArray().ToList();
        Assert.Equal(2, tabs.Count);
        Assert.All(tabs, tab =>
        {
            var selected = tab.GetProperty("id").GetString() == selectedId;
            Assert.Equal(selected ? "true" : "false", tab.GetProperty("selected").GetString());
            Assert.Equal(selected ? 0 : -1, tab.GetProperty("tabIndex").GetInt32());
        });
        Assert.Equal(selectedId, state.GetProperty("labelledBy").GetString());

        var expected = Expected(period, culture);
        var cards = state.GetProperty("cards").EnumerateArray().ToList();
        Assert.Equal(expected.Keys.ToArray(), cards.Select(c => c.GetProperty("plan").GetString()).ToArray());
        foreach (var card in cards)
        {
            var plan = card.GetProperty("plan").GetString()!;
            var (price, vat) = expected[plan];
            Assert.Equal(new[] { period }, Strings(card, "shown"));
            Assert.Equal(new[] { price }, Strings(card, "prices"));
            Assert.Equal(vat is null ? Array.Empty<string>() : new[] { vat }, Strings(card, "vats"));
        }
    }

    /// <summary>What each card must show for a period, from the fixed prices: Starter both, Pro monthly only, Pro+ none.</summary>
    private static Dictionary<string, (string Price, string? Vat)> Expected(string period, string culture)
    {
        var notSet = Text("Billing.PriceNotSet", culture);
        return period == "Monthly"
            ? new()
            {
                ["Starter"] = (PerMonth(300m, culture), Text("Billing.VatIncluded", culture)),
                ["Pro"] = (PerMonth(1234.50m, culture), Text("Billing.VatIncluded", culture)),
                ["ProPlus"] = (notSet, null),
                ["Enterprise"] = (Text("Pricing.Plan.Enterprise.Price", culture), null)
            }
            : new()
            {
                ["Starter"] = (PerYear(2750m, culture), Text("Billing.VatExcluded", culture)),
                ["Pro"] = (notSet, null),
                ["ProPlus"] = (notSet, null),
                ["Enterprise"] = (Text("Signup.PlanPrice.Enterprise.Yearly", culture), null)
            };
    }

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>("""
            (() => {
              const shown = el => el.getClientRects().length > 0;
              const panel = document.getElementById('pricing-plans');
              const tabs = [...document.querySelectorAll('[data-wm-period-tabs] [role="tab"]')];
              const active = document.activeElement;
              const [monthly, yearly] = tabs.map(t => t.getBoundingClientRect());
              return {
                dir: document.documentElement.getAttribute('dir'),
                tabs: tabs.map(t => ({ id: t.id, selected: t.getAttribute('aria-selected'), tabIndex: t.tabIndex })),
                active: active ? active.id : null,
                focusVisible: !!active && active.matches(':focus-visible'),
                outline: active ? getComputedStyle(active).outlineStyle : 'none',
                panelRole: panel.getAttribute('role'),
                labelledBy: panel.getAttribute('aria-labelledby'),
                status: document.querySelector('[data-wm-period-status]').textContent,
                monthlyFirstFromLeft: monthly.left < yearly.left,
                overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
                cards: [...panel.querySelectorAll('article.wm-plan')].map(card => ({
                  plan: card.getAttribute('aria-labelledby').replace('plan-', ''),
                  shown: [...card.querySelectorAll('[data-plan-period]')].filter(b => !b.hidden).map(b => b.getAttribute('data-plan-period')),
                  prices: [...card.querySelectorAll('[data-plan-price]')].filter(shown).map(p => p.textContent.trim()),
                  vats: [...card.querySelectorAll('[data-plan-vat]')].filter(shown).map(v => v.textContent.trim())
                }))
              };
            })()
            """, ct);

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, bool mobile, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the pricing period tab checks.");

        // The marketing host reaches the in-process server; everything else (the CDN) resolves normally.
        var browser = await HeadlessChromium.StartAsync(
            executable, [$"--host-resolver-rules=MAP {MarketingHost} 127.0.0.1:{_host.BaseAddress.Port}"], ct);
        return await browser.SetUpAsync(async started =>
        {
            var cookie = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            await started.SetCookieAsync(new Uri($"http://{MarketingHost}/"), CookieRequestCultureProvider.DefaultCookieName, cookie, ct);
            await started.SetViewportAsync(width, height, mobile, ct);
        });
    }

    private static async Task WaitForAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        if (!await browser.WaitForAsync(expression, TimeSpan.FromSeconds(30), ct))
            Assert.Fail($"Waited for {expression}; the browser is at {await browser.EvaluateAsync<string>("location.href", ct)}.");
    }
}
