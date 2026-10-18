using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Orders;
using Wasla.Application.Printing;
using Wasla.Application.Time;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-130: every UI language uses the Gregorian calendar, and the Orders date filter means the same Turkey-local days in
/// every language. ar-SA used to default to the Umm al-Qura calendar, so an Arabic page rendered its date inputs as Hijri
/// values and parsed the browser's Gregorian <c>yyyy-MM-dd</c> value with that calendar: the value failed to parse, the
/// filter was dropped without a word and every order was listed. The pages are rendered by the real controllers, model
/// binding and views (<see cref="TenantVisualWebHost"/>) over fake orders at fixed instants around Turkey's midnight
/// (UTC+3 all year).
/// </summary>
public sealed partial class OrdersDateFilterTests
{
    private static readonly string[] Cultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    // 23:59:59 on 9 March 2025 in Turkey, then 00:00:00 on 10 March, 23:59:59 on 11 March and 00:00:00 on 12 March.
    private static readonly DateTime BeforeUtc = new(2025, 3, 9, 20, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime FirstUtc = new(2025, 3, 9, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastUtc = new(2025, 3, 11, 20, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime AfterUtc = new(2025, 3, 11, 21, 0, 0, DateTimeKind.Utc);

    private const string Before = "W130-BEFORE";
    private const string First = "W130-FIRST";
    private const string Last = "W130-LAST";
    private const string After = "W130-AFTER";
    private static readonly Guid FirstId = Guid.Parse("13000000-0000-4000-8000-000000000002");
    private static readonly string[] FixedOrders = [Before, First, Last, After];

    // One of the orders the visual host seeds at the current time; a dropped date filter lists it.
    private const string CurrentOrder = "YS-48211";

    /// <summary>The reported defect: an Arabic page drops the browser's Gregorian dates and lists every order.</summary>
    [Fact]
    public async Task Arabic_KeepsTheBrowserSubmittedGregorianDates_AndFiltersByThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();
        using var client = await SignedInClientAsync(host, "ar-SA", ct);

        var page = await PageAsync(client, host, "/orders?startDate=2025-03-10&endDate=2025-03-11", ct);

        Assert.Equal("2025-03-10", InputValue(page, "ordersStartDate"));
        Assert.Equal("2025-03-11", InputValue(page, "ordersEndDate"));
        AssertOrders(page, "ar-SA", [First, Last]);
        Assert.DoesNotContain(CurrentOrder, page, StringComparison.Ordinal);
        AssertNoDateErrors(page, "ar-SA");
        Assert.Null(host.ServerErrors);
    }

    [Fact]
    public async Task SubmittedRange_RoundTripsAndSelectsTheSameOrders_InEveryCulture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);

            var page = await PageAsync(client, host, "/orders?startDate=2025-03-10&endDate=2025-03-11", ct);
            Assert.Equal("2025-03-10", InputValue(page, "ordersStartDate"));
            Assert.Equal("2025-03-11", InputValue(page, "ordersEndDate"));
            AssertOrders(page, name, [First, Last]);
            AssertNoDateErrors(page, name);

            // Sorting and paging links carry the same machine-readable range, and the page script's "today" too.
            var sortLinks = Regex.Matches(page, "href=\"(/orders\\?[^\"]*sortBy=[^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(sortLinks);
            Assert.All(sortLinks, link => Assert.Contains("startDate=2025-03-10&endDate=2025-03-11", link, StringComparison.Ordinal));
            var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
            Assert.Contains($"todayYmd: \"{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\"", page, StringComparison.Ordinal);

            // The table fragment that re-renders Orders after an action applies the same range.
            var table = await PageAsync(client, host, "/orders/table?startDate=2025-03-10&endDate=2025-03-11", ct);
            AssertOrders(table, name, [First, Last]);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>
    /// Turkey-local days: the start date is included from its local midnight and the end date up to (not including) the
    /// next local midnight, in every culture.
    /// </summary>
    [Fact]
    public async Task OneDayRanges_StartAtLocalMidnight_AndEndBeforeTheNextLocalMidnight_InEveryCulture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);
            AssertOrders(await PageAsync(client, host, "/orders?startDate=2025-03-09&endDate=2025-03-09", ct), name, [Before]);
            AssertOrders(await PageAsync(client, host, "/orders?startDate=2025-03-10&endDate=2025-03-10", ct), name, [First]);
            AssertOrders(await PageAsync(client, host, "/orders?startDate=2025-03-11&endDate=2025-03-11", ct), name, [Last]);
            AssertOrders(await PageAsync(client, host, "/orders?startDate=2025-03-12&endDate=2025-03-12", ct), name, [After]);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>
    /// Only the browser's <c>yyyy-MM-dd</c> value is a date. Culture formats that could mean two days, Hijri-looking
    /// values, other digits and days that do not exist are refused with the localized message, and the query is not run
    /// with the remaining bound: the page lists nothing rather than more than was asked for.
    /// </summary>
    [Theory]
    [InlineData("10/03/2025", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("03/10/2025", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("10.03.2025", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("2025-3-10", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("1446-09-10", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("٢٠٢٥-٠٣-١٠", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("2025-03-10T00:00", "2025-03-11", "Orders.InvalidStartDate")]
    [InlineData("2025-03-10", "2025-02-30", "Orders.InvalidEndDate")]
    [InlineData("2025-03-10", "9999-12-31", "Orders.InvalidEndDate")]
    [InlineData("2025-03-10", "not-a-date", "Orders.InvalidEndDate")]
    public async Task InvalidOrAmbiguousDate_ShowsTheLocalizedError_AndNeverWidensTheQuery(string start, string end, string errorKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();
        var path = $"/orders?startDate={Uri.EscapeDataString(start)}&endDate={Uri.EscapeDataString(end)}";

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);
            var page = await PageAsync(client, host, path, ct);

            Assert.Contains(Resource(name, errorKey), VisibleText(page), StringComparison.Ordinal);
            AssertOrders(page, name, []);
            Assert.DoesNotContain(CurrentOrder, page, StringComparison.Ordinal);

            // The valid bound keeps its value; the invalid one cannot be shown by a date input and is reported instead.
            var invalidInput = errorKey == "Orders.InvalidStartDate" ? "ordersStartDate" : "ordersEndDate";
            var validInput = invalidInput == "ordersStartDate" ? "ordersEndDate" : "ordersStartDate";
            Assert.Equal(invalidInput == "ordersStartDate" ? end : start, InputValue(page, validInput));
            Assert.Null(InputValue(page, invalidInput));
            Assert.Contains("aria-invalid=\"true\"", InputTag(page, invalidInput), StringComparison.Ordinal);

            Assert.DoesNotContain(CurrentOrder, await PageAsync(client, host, "/orders/table" + path[7..], ct), StringComparison.Ordinal);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>
    /// The only upper limit is what the query can represent: an end day is used up to its next Turkey-local midnight in
    /// UTC, so 9999-12-30 is the last end day and 9999-12-31 the last start day. There is no product ceiling below that.
    /// </summary>
    [Fact]
    public void LatestDates_AreLimitedOnlyByTheRepresentableEndBoundary()
    {
        var lastEnd = OrdersReceivedAtQueryRange.ParseWebQueryStrings(null, "9999-12-30");
        Assert.True(lastEnd.IsValid);
        Assert.Equal(new DateTime(9999, 12, 30, 21, 0, 0, DateTimeKind.Utc), lastEnd.UtcEndExclusive);

        var lastStart = OrdersReceivedAtQueryRange.ParseWebQueryStrings("9999-12-31", null);
        Assert.True(lastStart.IsValid);
        Assert.Equal(new DateTime(9999, 12, 30, 21, 0, 0, DateTimeKind.Utc), lastStart.UtcStartInclusive);

        var noNextDay = OrdersReceivedAtQueryRange.ParseWebQueryStrings("2025-03-10", "9999-12-31");
        Assert.True(noNextDay.EndInvalid);
        Assert.Null(noNextDay.EndDate);
        Assert.Null(noNextDay.UtcStartInclusive);

        var afterTheOldCeiling = OrdersReceivedAtQueryRange.ParseWebQueryStrings("2100-01-01", "2100-01-01");
        Assert.True(afterTheOldCeiling.IsValid);
        Assert.Equal(new DateTime(2099, 12, 31, 21, 0, 0, DateTimeKind.Utc), afterTheOldCeiling.UtcStartInclusive);
        Assert.Equal(new DateTime(2100, 1, 1, 21, 0, 0, DateTimeKind.Utc), afterTheOldCeiling.UtcEndExclusive);

        Assert.True(OrdersReceivedAtQueryRange.ParseWebQueryStrings("1999-12-31", null).StartInvalid);
    }

    [Fact]
    public async Task LatestRepresentableDates_FilterNormally_InEveryCulture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);

            var openEnded = await PageAsync(client, host, "/orders?startDate=2025-03-10&endDate=9999-12-30", ct);
            Assert.Equal("2025-03-10", InputValue(openEnded, "ordersStartDate"));
            Assert.Equal("9999-12-30", InputValue(openEnded, "ordersEndDate"));
            AssertOrders(openEnded, name, [First, Last, After]);
            Assert.Contains(CurrentOrder, openEnded, StringComparison.Ordinal);
            AssertNoDateErrors(openEnded, name);

            var lastDay = await PageAsync(client, host, "/orders?startDate=9999-12-31", ct);
            Assert.Equal("9999-12-31", InputValue(lastDay, "ordersStartDate"));
            AssertOrders(lastDay, name, []);
            Assert.DoesNotContain(CurrentOrder, lastDay, StringComparison.Ordinal);
            AssertNoDateErrors(lastDay, name);
        }

        Assert.Null(host.ServerErrors);
    }

    [Fact]
    public async Task InvertedRange_ShowsTheLocalizedError_KeepsBothValues_AndListsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);
            var page = await PageAsync(client, host, "/orders?startDate=2025-03-11&endDate=2025-03-10", ct);

            Assert.Contains(Resource(name, "Orders.InvalidDateRange"), VisibleText(page), StringComparison.Ordinal);
            Assert.Equal("2025-03-11", InputValue(page, "ordersStartDate"));
            Assert.Equal("2025-03-10", InputValue(page, "ordersEndDate"));
            AssertOrders(page, name, []);
            Assert.DoesNotContain(CurrentOrder, page, StringComparison.Ordinal);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>Without dates Orders still opens on today; a single bound still leaves the other side open.</summary>
    [Fact]
    public async Task OmittedDates_KeepTodayAsTheDefault_AndOneSidedRangesStayOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();
        var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var name in Cultures)
        {
            using var client = await SignedInClientAsync(host, name, ct);

            var page = await PageAsync(client, host, "/orders", ct);
            Assert.Equal(today, InputValue(page, "ordersStartDate"));
            Assert.Equal(today, InputValue(page, "ordersEndDate"));
            Assert.Contains(CurrentOrder, page, StringComparison.Ordinal);
            AssertOrders(page, name, []);
            AssertNoDateErrors(page, name);

            var fromOnly = await PageAsync(client, host, "/orders?startDate=2025-03-10", ct);
            Assert.Equal("2025-03-10", InputValue(fromOnly, "ordersStartDate"));
            Assert.Null(InputValue(fromOnly, "ordersEndDate"));
            AssertOrders(fromOnly, name, [First, Last, After]);
            Assert.Contains(CurrentOrder, fromOnly, StringComparison.Ordinal);
            AssertNoDateErrors(fromOnly, name);

            var toOnly = await PageAsync(client, host, "/orders?endDate=2025-03-10", ct);
            Assert.Null(InputValue(toOnly, "ordersStartDate"));
            Assert.Equal("2025-03-10", InputValue(toOnly, "ordersEndDate"));
            AssertOrders(toOnly, name, [Before, First]);
            Assert.DoesNotContain(CurrentOrder, toOnly, StringComparison.Ordinal);
            AssertNoDateErrors(toOnly, name);
        }

        Assert.Null(host.ServerErrors);
    }

    /// <summary>
    /// Server-rendered dates on Dashboard, Orders, order details and the Live Screen use the Gregorian calendar in every
    /// culture, with the language's own month names and separators.
    /// </summary>
    [Fact]
    public async Task ServerRenderedDates_AreGregorian_OnDashboardOrdersDetailsAndLiveScreen_InEveryCulture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
        var firstLocal = TimeZoneInfo.ConvertTimeFromUtc(FirstUtc, tz);
        var hijri = new UmAlQuraCalendar();

        foreach (var name in Cultures)
        {
            var gregorian = GregorianCulture.For(CultureInfo.GetCultureInfo(name));
            Assert.IsType<GregorianCalendar>(gregorian.DateTimeFormat.Calendar);
            using var client = await SignedInClientAsync(host, name, ct);

            var orders = VisibleText(await PageAsync(client, host, "/orders?startDate=2025-03-10&endDate=2025-03-10", ct));
            Assert.Contains(today.ToString("d", gregorian), orders, StringComparison.Ordinal);
            Assert.Contains(firstLocal.ToString("d", gregorian), orders, StringComparison.Ordinal);
            Assert.Contains("2025", orders, StringComparison.Ordinal);
            Assert.DoesNotContain(hijri.GetYear(firstLocal).ToString(CultureInfo.InvariantCulture), orders, StringComparison.Ordinal);

            var dashboard = VisibleText(await PageAsync(client, host, "/dashboard", ct));
            Assert.Contains(today.ToDateTime(TimeOnly.MinValue).ToString("d", gregorian), dashboard, StringComparison.Ordinal);
            Assert.DoesNotContain(hijri.GetYear(today.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture), dashboard, StringComparison.Ordinal);

            var live = VisibleText(await PageAsync(client, host, "/orders/live-display", ct));
            Assert.Contains(today.ToString("m", gregorian), live, StringComparison.Ordinal);

            var details = VisibleText(await PageAsync(client, host, $"/orders/details/{FirstId}", ct));
            Assert.Contains(firstLocal.ToString("d", gregorian), details, StringComparison.Ordinal);
            Assert.DoesNotContain(hijri.GetYear(firstLocal).ToString(CultureInfo.InvariantCulture), details, StringComparison.Ordinal);
        }

        // The Arabic month-day differs between the calendars, so this checks the calendar rather than a shared value.
        var arabic = CultureInfo.GetCultureInfo("ar-SA");
        Assert.IsType<UmAlQuraCalendar>(arabic.DateTimeFormat.Calendar);
        Assert.NotEqual(today.ToString("m", arabic), today.ToString("m", GregorianCulture.For(arabic)));
        Assert.Null(host.ServerErrors);
    }

    [Theory]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void DateFilterMessages_ExistInEveryCulture(string culture)
    {
        foreach (var key in new[] { "Orders.InvalidStartDate", "Orders.InvalidEndDate", "Orders.InvalidDateRange" })
        {
            var value = Resource(culture.TrimStart('.'), key);
            Assert.False(value.StartsWith("[missing", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value), $"{key} in SharedResource{culture}.resx");
        }
    }

    internal static Task<TenantVisualWebHost> StartAsync() => TenantVisualWebHost.StartAsync(seed: (db, _) => SeedFixedOrders(db));

    internal static void SeedFixedOrders(TenantDbContext db)
    {
        db.Orders.Add(FixedOrder(Guid.NewGuid(), Before, BeforeUtc));
        db.Orders.Add(FixedOrder(FirstId, First, FirstUtc));
        db.Orders.Add(FixedOrder(Guid.NewGuid(), Last, LastUtc));
        db.Orders.Add(FixedOrder(Guid.NewGuid(), After, AfterUtc));
    }

    private static Order FixedOrder(Guid id, string code, DateTime atUtc) => new()
    {
        Id = id,
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = code,
        ExternalOrderCode = code,
        IdempotencyKey = $"{FoodPlatform.TrendyolYemek}:{code}",
        InternalStatus = OrderStatus.Delivered,
        PlatformStatus = "Delivered",
        CustomerName = "Takvim Testi",
        TotalAmount = 120m,
        PaymentMethod = PaymentMethod.OnlinePayment,
        PaymentStatus = PaymentStatus.Paid,
        CreatedAtPlatform = atUtc,
        ReceivedAt = atUtc,
        DeliveredAt = atUtc.AddMinutes(30),
        CreatedAt = atUtc,
        UpdatedAt = atUtc,
        RawPayloadJson = "{}",
        Items = [new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 120m, TotalPrice = 120m }]
    };

    private static void AssertOrders(string html, string culture, string[] expected)
    {
        var listed = FixedOrders.Where(code => html.Contains(code, StringComparison.Ordinal)).ToArray();
        Assert.True(listed.SequenceEqual(FixedOrders.Where(expected.Contains)),
            $"{culture}: expected [{string.Join(", ", expected)}], listed [{string.Join(", ", listed)}]");
    }

    private static void AssertNoDateErrors(string html, string culture)
    {
        var text = VisibleText(html);
        foreach (var key in new[] { "Orders.InvalidStartDate", "Orders.InvalidEndDate", "Orders.InvalidDateRange" })
            Assert.DoesNotContain(Resource(culture, key), text, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-invalid=\"true\"", html, StringComparison.Ordinal);
    }

    private static string InputTag(string html, string id)
    {
        var match = Regex.Match(html, $"<input[^>]*\\bid=\"{id}\"[^>]*>");
        Assert.True(match.Success, $"#{id} not found");
        return match.Value;
    }

    /// <summary>The input's value attribute, or null when it has none.</summary>
    private static string? InputValue(string html, string id)
    {
        var value = Regex.Match(InputTag(html, id), "\\bvalue=\"([^\"]*)\"");
        return value.Success ? value.Groups[1].Value : null;
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

    private static string VisibleText(string html) =>
        Tag().Replace(ScriptOrStyle().Replace(html, " "), " ");

    internal static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
}
