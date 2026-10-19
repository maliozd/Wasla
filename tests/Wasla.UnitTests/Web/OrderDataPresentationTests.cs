using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.UnitTests.Admin;
using Wasla.Web.Ui;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-44: what the Dashboard and Orders pages say about order data. Amounts are Turkish lira in every UI culture (the
/// culture only decides separators and where the symbol goes), and the Recent Orders copy describes the list it heads:
/// the latest orders, whatever their day. The pages are rendered by the real controllers and views
/// (<see cref="TenantVisualWebHost"/>) over seeded fake orders.
/// </summary>
public sealed partial class OrderDataPresentationTests
{
    private static readonly string[] Cultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    // Any of the four languages' word for "today".
    private static readonly string[] TodayWords = ["today", "bugün", "اليوم", "сегодня"];

    [Fact]
    public async Task DashboardAndOrders_ShowLiraInEveryCulture_NeverTheCultureCurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        // The host seeds the newest order two minutes before its start. Orders defaults to today in Türkiye, which that
        // order is not in just after midnight there, so the list is asked for the local days the order can fall on.
        var startingUtc = DateTime.UtcNow;
        await using var host = await TenantVisualWebHost.StartAsync();
        var (startDate, endDate) = TurkeyLocalDateRange(startingUtc.AddMinutes(-2), DateTime.UtcNow);

        foreach (var name in Cultures)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            using var client = await SignedInClientAsync(host, name, ct);

            var dashboard = await PageAsync(client, host, "/dashboard", ct);
            var orders = await PageAsync(client, host, $"/orders?startDate={startDate}&endDate={endDate}", ct);
            var details = await PageAsync(client, host, $"/orders/details/{TenantVisualWebHost.DetailOrderId}", ct);

            // The newest seeded order (412.50) is on all three pages; its details add the fee and the line amounts.
            foreach (var (path, html) in new[] { ("/dashboard", dashboard), ("/orders", orders), ("details", details) })
            {
                Assert.True(html.Contains(Lira(412.50m, culture), StringComparison.Ordinal), $"{name} {path}: no {Lira(412.50m, culture)}");
                AssertNoCultureCurrency(name, path, html);
            }

            foreach (var amount in new[] { 19.90m, 140m, 280m, 25m, 50m, 82.50m })
                Assert.True(details.Contains(Lira(amount, culture), StringComparison.Ordinal), $"{name} details: no {Lira(amount, culture)}");
        }

        Assert.Null(host.ServerErrors);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void FormatMoney_IsLira_WithTheCultureNumberFormat(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);

        var formatted = OrderUiHelper.FormatMoney(1234.5m, culture);

        Assert.Equal(Lira(1234.5m, culture), formatted);
        Assert.Contains(1234.5m.ToString("N2", culture), formatted, StringComparison.Ordinal);
        Assert.Equal(1, formatted.Count(c => c == '₺'));
        if (culture.NumberFormat.CurrencySymbol != "₺")
            Assert.DoesNotContain(culture.NumberFormat.CurrencySymbol, formatted, StringComparison.Ordinal);

        // The request's culture is the default.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            Assert.Equal(formatted, OrderUiHelper.FormatMoney(1234.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatMoney_KeepsTheCultureSeparatorsAndPlacement()
    {
        Assert.Equal("₺1.234,50", OrderUiHelper.FormatMoney(1234.5m, CultureInfo.GetCultureInfo("tr-TR")));
        Assert.Equal("₺1,234.50", OrderUiHelper.FormatMoney(1234.5m, CultureInfo.GetCultureInfo("en-US")));
        Assert.EndsWith(" ₺", OrderUiHelper.FormatMoney(1234.5m, CultureInfo.GetCultureInfo("ru-RU")), StringComparison.Ordinal);
        Assert.EndsWith(" ₺", OrderUiHelper.FormatMoney(1234.5m, CultureInfo.GetCultureInfo("ar-SA")), StringComparison.Ordinal);
    }

    /// <summary>One currency for the server and the Live Screen's browser formatter.</summary>
    [Fact]
    public void OrderCurrency_IsTheLira_AndTheLiveScreenFormatterUsesTheSameCode()
    {
        // ICU's Turkish lira: the code and the symbol belong together.
        Assert.Equal(OrderCurrency.Code, new RegionInfo("TR").ISOCurrencySymbol);
        Assert.Equal(OrderCurrency.Symbol, CultureInfo.GetCultureInfo("tr-TR").NumberFormat.CurrencySymbol);

        var store = File.ReadAllText(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-store.js"));
        Assert.Contains($"currency: \"{OrderCurrency.Code}\"", store, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentOrders_ListsTheLatestOrdersOfAnyDay_AndItsCopySaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);
            var dashboard = await PageAsync(client, host, "/dashboard", ct);
            var resources = Resources("." + name);
            var recent = Section(dashboard, "data-tour=\"dashboard-recent\"");

            // The seeded order from yesterday is among the latest ten, so the section must not claim "today".
            Assert.Contains("TY-76002", recent, StringComparison.Ordinal);
            Assert.Contains(resources["Dashboard.RecentOrders"], recent, StringComparison.Ordinal);
            Assert.Contains(resources["Dashboard.LatestOrdersReceived"], recent, StringComparison.Ordinal);
            foreach (var word in TodayWords)
                Assert.DoesNotContain(word, VisibleText(recent), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>
    /// Every resource file, the neutral one included: the Recent Orders copy never says "today", the keys that did are
    /// gone, and no text formats an amount with the culture's currency (<c>{0:C}</c>).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void ResourceFiles_RecentOrdersCopyIsNotTodayOnly_AndNoTextUsesTheCultureCurrency(string culture)
    {
        var resources = Resources(culture);

        Assert.DoesNotContain("Dashboard.LatestOrdersReceivedToday", resources.Keys);
        Assert.DoesNotContain("Dashboard.NoOrdersYetToday", resources.Keys);

        string[] recentOrdersCopy = ["Dashboard.RecentOrders", "Dashboard.LatestOrdersReceived", "Dashboard.NoOrdersYet"];
        foreach (var key in recentOrdersCopy)
        {
            if (culture.Length > 0)
                Assert.True(resources.ContainsKey(key), $"{key} missing in SharedResource{culture}.resx");

            if (!resources.TryGetValue(key, out var value))
                continue;

            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty in SharedResource{culture}.resx");
            foreach (var word in TodayWords)
                Assert.DoesNotContain(word, value, StringComparison.OrdinalIgnoreCase);
        }

        var cultureCurrency = resources.Where(p => CultureCurrencyFormatItem().IsMatch(p.Value)).Select(p => p.Key).ToList();
        Assert.True(cultureCurrency.Count == 0, $"Culture currency format in SharedResource{culture}.resx: {string.Join(", ", cultureCurrency)}");
    }

    /// <summary>
    /// The date arithmetic the two page tests above rely on, at fixed instants around midnight in Türkiye (21:00 UTC),
    /// checked against the Orders filter's own mapping of local dates to UTC bounds. The pages and the Dashboard read the
    /// system clock, so these instants cannot be replayed through them.
    /// </summary>
    [Theory]
    [InlineData("2026-10-18T12:00:00Z")]
    [InlineData("2026-10-18T20:58:30Z")]
    [InlineData("2026-10-18T20:59:59Z")]
    [InlineData("2026-10-18T21:00:00Z")]
    [InlineData("2026-10-18T21:00:15Z")]
    [InlineData("2026-10-18T21:00:59Z")]
    [InlineData("2026-10-18T21:01:00Z")]
    [InlineData("2026-10-18T21:02:30Z")]
    public void AroundMidnightInTurkey_TheRangeCoversTheFixture_AndTheFallbackOrderIsToday(string instant)
    {
        var nowUtc = DateTime.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));

        // The newest fixture order is seeded two minutes before the host starts, which is at or after nowUtc.
        foreach (var hostStartUtc in new[] { nowUtc, nowUtc.AddSeconds(3) })
        {
            var fixtureUtc = hostStartUtc.AddMinutes(-2);
            var (start, end) = TurkeyLocalDateRange(nowUtc.AddMinutes(-2), hostStartUtc.AddSeconds(5));
            var range = OrdersReceivedAtQueryRange.ParseWebQueryStrings(start, end);
            Assert.True(range.IsValid, $"{instant}: {start}..{end} is not a valid filter");
            Assert.True(range.UtcStartInclusive <= fixtureUtc && fixtureUtc < range.UtcEndExclusive,
                $"{instant}: {start}..{end} misses {fixtureUtc:O}");
        }

        var (today, fallbackUtc) = TurkeyTodayAt(nowUtc, nowUtc.AddMinutes(-1));
        Assert.Equal(localToday, today);
        Assert.True(fallbackUtc <= nowUtc);
        var day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todayRange = OrdersReceivedAtQueryRange.ParseWebQueryStrings(day, day);
        Assert.True(todayRange.IsValid, $"{instant}: {day} is not a valid filter");
        var (todayStartUtc, todayEndUtc) = (todayRange.UtcStartInclusive, todayRange.UtcEndExclusive);
        Assert.True(todayStartUtc <= fallbackUtc && fallbackUtc < todayEndUtc, $"{instant}: {fallbackUtc:O} is not on {day}");
        // Away from midnight the fallback order keeps its minute-old import time.
        if (nowUtc.AddMinutes(-1) >= todayStartUtc)
            Assert.Equal(nowUtc.AddMinutes(-1), fallbackUtc);
    }

    /// <summary>
    /// The Türkiye-local calendar days that hold <paramref name="earliestUtc"/> and <paramref name="latestUtc"/>, in the
    /// Orders filter's invariant <c>yyyy-MM-dd</c> form.
    /// </summary>
    private static (string Start, string End) TurkeyLocalDateRange(DateTime earliestUtc, DateTime latestUtc)
    {
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        string Day(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (Day(earliestUtc), Day(latestUtc));
    }

    /// <summary>The expected text of a lira amount: the culture's number format with the lira sign as its currency.</summary>
    private static string Lira(decimal amount, CultureInfo culture)
    {
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = "₺";
        return amount.ToString("C2", format);
    }

    private static void AssertNoCultureCurrency(string culture, string path, string html)
    {
        var text = VisibleText(html);
        switch (culture)
        {
            case "en-US":
                Assert.False(DollarAmount().IsMatch(text), $"{culture} {path}: dollar amount '{DollarAmount().Match(text).Value}'");
                break;
            case "ar-SA":
                Assert.DoesNotContain("ر.س", text, StringComparison.Ordinal);
                break;
            case "ru-RU":
                Assert.DoesNotContain("₽", text, StringComparison.Ordinal);
                break;
        }

        foreach (var code in new[] { "USD", "SAR", "RUB" })
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
    }

    private static async Task<HttpClient> SignedInClientAsync(TenantVisualWebHost host, string culture, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = host.BaseAddress };
        (await client.GetAsync(TenantVisualWebHost.SignInPath, ct)).EnsureSuccessStatusCode();
        cookies.Add(host.BaseAddress, new Cookie(
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)))));
        return client;
    }

    private static async Task<string> PageAsync(HttpClient client, TenantVisualWebHost host, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: HTTP {(int)response.StatusCode}; {host.ServerErrors}");
        return WebUtility.HtmlDecode(body);
    }

    /// <summary>The element that carries <paramref name="marker"/>, up to the next column of the dashboard grid.</summary>
    private static string Section(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} not found");
        var end = html.IndexOf("<div class=\"col-12\">", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    /// <summary>Markup text without scripts, styles and tags.</summary>
    private static string VisibleText(string html) =>
        Tag().Replace(ScriptOrStyle().Replace(html, " "), " ");

    private static Dictionary<string, string> Resources(string culture)
    {
        var path = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in XDocument.Load(path).Root!.Elements("data"))
            values.TryAdd(element.Attribute("name")!.Value, element.Element("value")?.Value ?? string.Empty);
        return values;
    }

    [GeneratedRegex(@"\$\s?\d")]
    private static partial Regex DollarAmount();

    [GeneratedRegex(@"\{\d+(,[^:}]*)?:[Cc]\d*\}")]
    private static partial Regex CultureCurrencyFormatItem();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
}
