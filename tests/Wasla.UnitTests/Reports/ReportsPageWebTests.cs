using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// The report page (WAS-183) through the real Web pipeline with sessions from the real login. The shared report is replaced
/// by a known one, so every figure on the page can be checked exactly and every call shows its tenant and filter. The
/// report's own calculation is covered on SQL Server by <c>OrderReportSqlServerTests</c>.
/// </summary>
public sealed class ReportsPageWebTests(ReportsPageWebTests.Hosts hosts) : IClassFixture<ReportsPageWebTests.Hosts>
{
    private async Task<string> PageAsync(string path, HttpStatusCode expected = HttpStatusCode.OK, string role = "owner")
    {
        using var response = await hosts.Client(role).GetAsync(path);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expected == response.StatusCode, $"{path}: expected {(int)expected}, got {(int)response.StatusCode}");
        return WebUtility.HtmlDecode(body);
    }

    private void Reset(ReportMode mode = ReportMode.Known)
    {
        hosts.Reports.Calls.Clear();
        hosts.Reports.Mode = mode;
    }

    [Fact]
    public async Task Overview_ShowsExactlyTheFiguresOfTheSharedReport()
    {
        Reset();
        var html = await PageAsync("/reports?preset=yesterday");

        foreach (var expected in new[]
                 {
                     "<bdi>8</bdi>", "<bdi>₺1,310.50</bdi>", "<bdi>₺262.10</bdi>", "<bdi>₺1,563.00</bdi>",
                     "5 delivered orders",
                     "<bdi>62.5%</bdi>", "<bdi>12.5%</bdi>"
                 })
            Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Contains("They are not profit, payouts or net income.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_ReachTheSharedReport_ForTheSignedInTenant()
    {
        Reset();
        await PageAsync("/reports/content?preset=custom&startDate=2026-10-01&endDate=2026-10-05&platform=getiryemek");

        var call = Assert.Single(hosts.Reports.Calls);
        Assert.Equal(hosts.AlphaId, call.TenantId);
        Assert.Equal(OrderReportPreset.Custom, call.Filter.Period.Preset);
        Assert.Equal(new DateOnly(2026, 10, 1), call.Filter.Period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 5), call.Filter.Period.EndDate);
        Assert.Equal(FoodPlatform.GetirYemek, call.Filter.Platform);
    }

    [Fact]
    public async Task NoFilter_IsTheLast30Days()
    {
        Reset();
        await PageAsync("/reports");

        var call = Assert.Single(hosts.Reports.Calls);
        Assert.Equal(OrderReportPreset.Last30Days, call.Filter.Period.Preset);
        Assert.Null(call.Filter.Platform);
    }

    [Fact]
    public async Task ContentEndpoint_ReturnsTheResultWithoutTheLayout()
    {
        Reset();
        var html = await PageAsync("/reports/content?preset=yesterday");

        Assert.Contains("data-rpt-result-root", html, StringComparison.Ordinal);
        Assert.Contains("data-rpt-period-source", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Platforms_BreakTheSameFiguresDownByPlatform_ThroughTheSharedReport()
    {
        Reset();
        var html = await PageAsync("/reports?preset=last7days&section=platforms");

        foreach (var expected in new[]
                 {
                     "<bdi>₺900.30</bdi>", "<bdi>68.7%</bdi>", "<bdi>₺300.10</bdi>", "<bdi>25.0%</bdi>",
                     "<bdi>₺410.20</bdi>", "<bdi>31.3%</bdi>", "<bdi>₺205.10</bdi>"
                 })
            Assert.Contains(expected, html, StringComparison.Ordinal);

        // One read holds the summary, the platform rows and the daily charts.
        var call = Assert.Single(hosts.Reports.Calls);
        Assert.Null(call.Filter.Platform);
    }

    /// <summary>The orders change after every read, so a page built from two reads would show two different pictures.</summary>
    [Theory]
    [InlineData("", 4, 3, 1, 8)]
    [InlineData("&platform=TrendyolYemek", 4, 0, 0, 4)]
    [InlineData("&platform=Yemeksepeti", 0, 3, 0, 3)]
    public async Task PlatformSummaryAndDailyFigures_ComeFromOneRead_WhileOrdersChange(string platform, int trendyol, int yemeksepeti, int getir, int total)
    {
        Reset(ReportMode.ChangesAfterEveryRead);
        var html = await PageAsync($"/reports?preset=custom&startDate=2026-10-06&endDate=2026-10-08&section=platforms{platform}");

        Assert.Single(hosts.Reports.Calls);

        var perDay = Regex.Matches(html, """<td class="wasla-table__num"><bdi>(\d+)</bdi></td>""").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        var columns = new[] { trendyol, yemeksepeti, getir }.Count(c => c > 0);
        Assert.Equal(3 * columns, perDay.Length);
        Assert.Equal(total, perDay.Sum());

        // Rows are newest first and platforms are in the order of the platform table: the second day holds a quarter of the orders.
        var expected = new[] { trendyol, yemeksepeti, getir }.Where(c => c > 0).ToArray();
        Assert.Equal(expected.Select(c => c - 1), perDay.Skip(columns * 2).Take(columns));
        Assert.Equal(expected.Select(_ => 1), perDay.Skip(columns).Take(columns));
        Assert.Equal(expected.Select(_ => 0), perDay.Take(columns));
        Assert.Contains($"<bdi>{total}</bdi>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmountsAboveTheIntegerRange_RenderExactly_OnEverySection()
    {
        Reset(ReportMode.Large);
        foreach (var section in new[] { "overview", "platforms", "cancellations" })
        {
            var html = await PageAsync($"/reports?preset=custom&startDate=2026-10-06&endDate=2026-10-08&section={section}");
            Assert.DoesNotContain("data-state=\"Failed\"", html, StringComparison.Ordinal);
        }

        var overview = await PageAsync("/reports?preset=custom&startDate=2026-10-06&endDate=2026-10-08");
        Assert.Contains("<bdi>₺3,000,000,000.57</bdi>", overview, StringComparison.Ordinal);
        Assert.Contains("<bdi>₺4,000,000,000</bdi>", overview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?preset=CUSTOM&startDate=2026-10-01&endDate=2026-10-05", "?preset=custom&startDate=2026-10-01&endDate=2026-10-05", "")]
    [InlineData("?preset=CuStOm&startDate=2026-10-01&endDate=2026-10-05&compare=1", "?preset=custom&startDate=2026-10-01&endDate=2026-10-05&compare=1", "")]
    [InlineData("?PRESET=Yesterday", "?preset=yesterday", "")]
    [InlineData("?preset=LAST30DAYS", "", "")]
    [InlineData("?platform=3", "?platform=TrendyolYemek", "TrendyolYemek")]
    [InlineData("?platform=0002", "?platform=GetirYemek", "GetirYemek")]
    [InlineData("?platform=getiryemek", "?platform=GetirYemek", "GetirYemek")]
    [InlineData("?Platform=YEMEKSEPETI", "?platform=Yemeksepeti", "Yemeksepeti")]
    [InlineData("?preset=TODAY&platform=1&section=PLATFORMS", "?preset=today&platform=Yemeksepeti", "Yemeksepeti")]
    public async Task EverySupportedForm_IsRenderedInItsOneCanonicalForm(string submitted, string canonical, string option)
    {
        Reset();
        foreach (var path in new[] { "/reports", "/reports/content" })
            Assert.Contains($"data-query=\"{canonical}\"", await PageAsync(path + submitted), StringComparison.Ordinal);

        var html = await PageAsync("/reports" + submitted);
        Assert.Contains($"href=\"/reports{canonical}{(canonical.Length == 0 ? "?" : "&")}section=cancellations\"", html, StringComparison.Ordinal);
        if (option.Length > 0)
            Assert.Contains($"<option value=\"{option}\" selected", html, StringComparison.Ordinal);

        // The filter that ran is the one the page shows.
        Assert.All(hosts.Reports.Calls, c => Assert.Equal(option.Length == 0 ? null : Enum.Parse<FoodPlatform>(option), c.Filter.Platform));
    }

    [Theory]
    [InlineData("?platform=1&platform=2")]
    [InlineData("?PLATFORM=1&platform=1")]
    [InlineData("?preset=today&preset=today")]
    [InlineData("?preset=custom&startDate=2026-10-01&startDate=2026-10-01&endDate=2026-10-05")]
    [InlineData("?platform=%203")]
    [InlineData("?platform=3%20")]
    [InlineData("?platform=%2B3")]
    [InlineData("?platform=-3")]
    [InlineData("?platform=4")]
    [InlineData("?platform=getir")]
    [InlineData("?preset=%20custom&startDate=2026-10-01&endDate=2026-10-05")]
    [InlineData("?preset=CUSTOM%20&startDate=2026-10-01&endDate=2026-10-05")]
    [InlineData("?preset=CUSTOM&startDate=2026-10-01")]
    [InlineData("?preset=CUSTOM&startDate=%202026-10-01&endDate=2026-10-05")]
    [InlineData("?preset=TODAY&startDate=2026-10-01")]
    public async Task RefusedForms_StayRefused_AndAreNotRepairedByAnyLink(string submitted)
    {
        Reset();
        foreach (var path in new[] { "/reports", "/reports/content" })
        {
            var html = await PageAsync(path + submitted, HttpStatusCode.BadRequest);
            Assert.Contains("data-state=\"InvalidFilter\"", html, StringComparison.Ordinal);
        }

        var page = await PageAsync("/reports" + submitted, HttpStatusCode.BadRequest);
        var written = Regex.Match(page, "data-query=\"([^\"]*)\"").Groups[1].Value;
        Assert.NotEmpty(written);
        foreach (var query in new[] { written, Regex.Match(page, "href=\"/reports([^\"]*)\"[^>]*data-rpt-section=\"cancellations\"").Groups[1].Value })
            await PageAsync("/reports/content" + query, HttpStatusCode.BadRequest);

        Assert.Empty(hosts.Reports.Calls);
    }

    [Fact]
    public async Task OnePlatformSelected_OffersAllPlatforms_AndRunsOneReport()
    {
        Reset();
        var html = await PageAsync("/reports?preset=last7days&platform=TrendyolYemek&section=platforms");

        Assert.Contains("Show all platforms", html, StringComparison.Ordinal);
        Assert.Single(hosts.Reports.Calls);
    }

    [Fact]
    public async Task Cancellations_ShowTheCombinedCountAndRate_OverAllOrders()
    {
        Reset();
        var html = await PageAsync("/reports?preset=yesterday&section=cancellations");

        Assert.Contains("12.5% of all orders (1 / 8)", html, StringComparison.Ordinal);
        Assert.Contains("Cancelled and rejected orders are counted together.", html, StringComparison.Ordinal);
        Assert.Contains("orders in progress included", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOrders_IsItsOwnState_WithNoRateOrFigures()
    {
        Reset(ReportMode.Empty);
        var html = await PageAsync("/reports?preset=yesterday&section=cancellations");

        Assert.Contains("No orders in this period", html, StringComparison.Ordinal);
        Assert.Contains("data-state=\"NoOrders\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("of all orders", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCompletedOrders_ShowNoAverage_WithItsReason()
    {
        Reset(ReportMode.NoneCompleted);
        var html = await PageAsync("/reports?preset=yesterday");

        Assert.Contains("Not calculated, no orders were delivered", html, StringComparison.Ordinal);
        Assert.Contains($"<bdi>{ReportFormatNoValue}</bdi>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<bdi>₺0.00</bdi>\r\n</dd>", html, StringComparison.Ordinal);
    }

    private const string ReportFormatNoValue = "—";

    [Theory]
    [InlineData("?platform=%20", "PlatformInvalid", "The selected platform is not recognised.")]
    [InlineData("?preset=last90days", "UnknownPreset", "The selected period is not recognised.")]
    [InlineData("?preset=custom&startDate=2026-10-05&endDate=2026-10-01", "RangeInverted", "The end date cannot be before the start date.")]
    [InlineData("?preset=custom&startDate=2026-01-01&endDate=2026-10-01", "RangeTooLong", "You can select at most 92 days.")]
    [InlineData("?preset=custom&startDate=2026-10-01&endDate=2099-01-01", "RangeInFuture", "A day after today cannot be selected.")]
    [InlineData("?platform=1&platform=2", "DuplicateParameter", "The link contains the same filter more than once.")]
    public async Task InvalidFilter_Answers400_WithItsMessage_AndRunsNoReport(string query, string code, string message)
    {
        Reset();
        foreach (var path in new[] { "/reports", "/reports/content" })
        {
            var html = await PageAsync(path + query, HttpStatusCode.BadRequest);
            Assert.True(html.Contains(message, StringComparison.Ordinal), $"{path}{query} ({code}) lacks its message");
            Assert.Contains("data-state=\"InvalidFilter\"", html, StringComparison.Ordinal);
        }

        Assert.Empty(hosts.Reports.Calls);
    }

    [Fact]
    public async Task ReportFailure_IsDistinctFromNoOrders_OnTheContentRouteAndThePage()
    {
        Reset(ReportMode.Fail);
        await PageAsync("/reports/content?preset=yesterday", HttpStatusCode.InternalServerError);

        var html = await PageAsync("/reports?preset=yesterday");
        Assert.Contains("data-state=\"Failed\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rpt-retry", html, StringComparison.Ordinal);
        Assert.Contains("This does not mean you have no orders.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No orders in this period", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_OfACompletePeriod_ReadsTheEqualPrecedingPeriod_WithExplicitDates()
    {
        Reset();
        var html = await PageAsync("/reports?preset=custom&startDate=2026-10-06&endDate=2026-10-10&compare=1");

        var calls = hosts.Reports.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(new DateOnly(2026, 10, 1), calls[1].Filter.Period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 5), calls[1].Filter.Period.EndDate);
        Assert.Contains("6 Oct - 10 Oct 2026", html, StringComparison.Ordinal);
        Assert.Contains("1 Oct - 5 Oct 2026", html, StringComparison.Ordinal);
        Assert.Contains("data-compare-active=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_AgainstAZeroBaseline_ShowsNoPercentage()
    {
        Reset(ReportMode.PreviousEmpty);
        var html = await PageAsync("/reports?preset=custom&startDate=2026-10-06&endDate=2026-10-10&compare=1");

        Assert.Contains("Not comparable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bi-arrow-up", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bi-arrow-down", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_OfAPeriodThatIncludesToday_IsNotMade_AndOffersCompleteDays()
    {
        Reset();
        var html = await PageAsync("/reports?preset=last7days&compare=1");

        Assert.Single(hosts.Reports.Calls);
        Assert.Contains("This period cannot be compared because today is not over.", html, StringComparison.Ordinal);
        Assert.Contains("Use the last 7 complete days", html, StringComparison.Ordinal);
        Assert.Contains("data-compare-active=\"false\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedWork_IsNotOnThePage()
    {
        Reset();
        var html = await PageAsync("/reports");

        foreach (var section in new[] { "data-rpt-section=\"overview\"", "data-rpt-section=\"platforms\"", "data-rpt-section=\"cancellations\"" })
            Assert.Contains(section, html, StringComparison.Ordinal);
        Assert.Equal(3, html.Split("data-rpt-section=").Length - 1);
        Assert.DoesNotContain("CSV", html, StringComparison.Ordinal);
        foreach (var absent in new[] { "Busy", "Products", "Timing", "Reason" })
            Assert.DoesNotContain(absent, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("manager", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("kitchen", HttpStatusCode.Redirect)]
    [InlineData("cashier", HttpStatusCode.Redirect)]
    public async Task OnlyReportRoles_ReachThePageAndItsData(string role, HttpStatusCode expected)
    {
        Reset();
        foreach (var path in new[] { "/reports?preset=yesterday", "/reports/content?preset=yesterday" })
        {
            using var response = await hosts.Client(role).GetAsync(path);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Redirect)
                Assert.Contains("access-denied", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(expected == HttpStatusCode.OK ? 2 : 0, hosts.Reports.Calls.Count);
    }

    [Fact]
    public async Task SignedOut_IsSentToLogin_AndRunsNoReport()
    {
        Reset();
        using var client = hosts.AnonymousClient();
        using var response = await client.GetAsync("/reports/content");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/auth/login", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(hosts.Reports.Calls);
    }

    [Fact]
    public async Task EachTenant_ReadsOnlyItsOwnReport_AndASessionDoesNotCrossHosts()
    {
        Reset();
        await PageAsync("/reports/content?preset=yesterday");
        await PageAsync("/reports/content?preset=yesterday", role: "beta");
        Assert.Equal(new[] { hosts.AlphaId, hosts.BetaId }, hosts.Reports.Calls.Select(c => c.TenantId));

        Reset();
        using var replay = hosts.AlphaCookiesOnBetaHost();
        using var crossed = await replay.GetAsync("/reports/content?preset=yesterday");
        Assert.NotEqual(HttpStatusCode.OK, crossed.StatusCode);
        Assert.Empty(hosts.Reports.Calls);
    }

    [Fact]
    public async Task Navigation_ListsReports_OnlyForReportRoles()
    {
        Reset();
        var owner = await PageAsync("/dashboard");
        Assert.Contains("href=\"/reports\"", owner, StringComparison.Ordinal);

        var kitchen = await PageAsync("/orders", role: "kitchen");
        Assert.DoesNotContain("href=\"/reports\"", kitchen, StringComparison.Ordinal);
    }

    public enum ReportMode
    {
        Known,
        Empty,
        NoneCompleted,
        PreviousEmpty,
        ChangesAfterEveryRead,
        Large,
        Fail
    }

    public sealed class Hosts : IAsyncLifetime
    {
        private readonly Dictionary<string, TenantSessionClient> _clients = new(StringComparer.Ordinal);
        private TenantSessionWebHost _web = null!;

        public FixedReports Reports { get; } = new();

        public Guid AlphaId => _web.AlphaId;

        public Guid BetaId => _web.BetaId;

        internal TenantSessionClient Client(string role) => _clients[role];

        internal TenantSessionClient AnonymousClient() => _web.Client(TenantSessionWebHost.AlphaHost);

        internal TenantSessionClient AlphaCookiesOnBetaHost() => _clients["owner"].ReplayTo(_web.BaseAddress, TenantSessionWebHost.BetaHost);

        public async ValueTask InitializeAsync()
        {
            _web = await TenantSessionWebHost.StartAsync(configureServices: services =>
            {
                services.RemoveAll<IOrderReportService>();
                services.AddSingleton<IOrderReportService>(Reports);
            });

            foreach (var (name, role) in new[] { ("owner", UserRole.Owner), ("manager", UserRole.Manager), ("viewer", UserRole.Viewer), ("kitchen", UserRole.Kitchen), ("cashier", UserRole.Cashier) })
                _clients[name] = await SignInAsync(_web.AlphaId, TenantSessionWebHost.AlphaHost, $"{name}@alpha.test", role);
            _clients["beta"] = await SignInAsync(_web.BetaId, TenantSessionWebHost.BetaHost, "owner@beta.test", UserRole.Owner);
        }

        private async Task<TenantSessionClient> SignInAsync(Guid tenantId, string host, string email, UserRole role)
        {
            var user = _web.SeedUser(tenantId, email, role);
            var client = _web.Client(host);
            await client.LoginAsync(user.Email);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients.Values)
                client.Dispose();
            await _web.DisposeAsync();
        }
    }

    /// <summary>A known cohort on the period's first day: 8 orders, 5 delivered for ₺1,310.50, on three platforms.</summary>
    public sealed class FixedReports : IOrderReportService
    {
        public ConcurrentQueue<(Guid TenantId, OrderReportFilter Filter)> Calls { get; } = new();

        public ReportMode Mode { get; set; }

        public Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct)
        {
            Calls.Enqueue((tenantId, filter));
            if (Mode == ReportMode.Fail)
                throw new InvalidOperationException("The report read failed.");

            var day = filter.Period.StartDate;
            var isPrevious = Calls.Count > 1 && Calls.First().Filter.Period.StartDate > filter.Period.StartDate;
            var groups = new List<OrderReportService.Group>();
            if (Mode == ReportMode.NoneCompleted)
            {
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Preparing, day, 1, 300m));
            }
            else if (Mode == ReportMode.Known || (Mode == ReportMode.PreviousEmpty && !isPrevious))
            {
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day, 3, 900.30m));
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Cancelled, day, 1, 120m));
                groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.Delivered, day, 2, 410.20m));
                groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.New, day, 1, 55.50m));
                groups.Add(new(FoodPlatform.GetirYemek, OrderStatus.Failed, day, 1, 77m));
            }

            else if (Mode == ReportMode.Large)
            {
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day, 2, 2_147_483_648.57m));
                groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.Delivered, day.AddDays(1), 1, 852_516_352m));
            }
            else if (Mode == ReportMode.ChangesAfterEveryRead)
            {
                // Each read after the first sees orders the first one did not.
                var extra = Calls.Count - 1;
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day, 3 + extra * 5, 900.30m));
                groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day.AddDays(1), 1 + extra * 5, 100m));
                groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.New, day, 2 + extra * 5, 410.20m));
                groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.New, day.AddDays(1), 1 + extra * 5, 55.50m));
                groups.Add(new(FoodPlatform.GetirYemek, OrderStatus.Failed, day.AddDays(1), 1 + extra * 5, 77m));
            }

            if (filter.Platform is { } platform)
                groups = groups.Where(g => g.Platform == platform).ToList();
            return Task.FromResult(OrderReportService.Build(filter, groups, DateTime.UtcNow));
        }
    }
}
