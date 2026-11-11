using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// The report export endpoint (WAS-184) through the real Web pipeline with sessions from the real login: the report role
/// and the session's tenant decide, the filter follows the page's rules, and every answer is either the file or a short
/// text without a file. The shared report is replaced by a known one (<see cref="ReportsPageWebTests.FixedReports"/>), so
/// every figure can be checked against the page.
/// </summary>
[Collection(ReportPdfCollection.Name)]
public sealed class ReportsExportWebTests(ReportsExportWebTests.Hosts hosts) : IClassFixture<ReportsExportWebTests.Hosts>
{
    private const string Range = "preset=custom&startDate=2026-10-06&endDate=2026-10-08";

    private void Reset(ReportsPageWebTests.ReportMode mode = ReportsPageWebTests.ReportMode.Known)
    {
        hosts.Reports.Inner.Calls.Clear();
        hosts.Reports.Inner.Mode = mode;
        hosts.Reports.Hold = null;
        while (hosts.Reports.Entered.Wait(0))
        {
        }
    }

    private async Task<HttpResponseMessage> ExportAsync(string query, string role = "owner") =>
        await hosts.Client(role).GetAsync("/reports/export?" + query);

    private static async Task<string> TextAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("csv", "text/csv", "wasla-orders-report_2026-10-06_2026-10-08.csv", "EFBBBF")]
    [InlineData("pdf", "application/pdf", "wasla-orders-report_2026-10-06_2026-10-08.pdf", "255044462D")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "wasla-orders-report_2026-10-06_2026-10-08.xlsx", "504B0304")]
    public async Task EachFormat_IsADownload_WithSafeHeaders(string format, string mediaType, string fileName, string magic)
    {
        Reset();
        using var response = await ExportAsync($"{Range}&format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal(fileName, disposition.FileName?.Trim('"'));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith(magic, Convert.ToHexString(bytes), StringComparison.Ordinal);
        Assert.Single(hosts.Reports.Inner.Calls);
    }

    [Fact]
    public async Task TheCsv_ReconcilesWithThePage_ForTheSameFilter()
    {
        Reset();
        using var page = await hosts.Client("owner").GetAsync("/reports?" + Range);
        var html = WebUtility.HtmlDecode(await TextAsync(page));
        using var export = await ExportAsync(Range + "&format=csv");
        var records = ParseCsv(CsvText(await export.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        var summary = Section(records, "Summary");
        string Value(string measure) => summary.Single(r => r[0] == measure)[1];

        // The page's figures (ReportsPageWebTests.Overview_ShowsExactlyTheFiguresOfTheSharedReport) and the file's.
        foreach (var shown in new[] { "<bdi>8</bdi>", "<bdi>₺1,310.50</bdi>", "<bdi>₺262.10</bdi>", "<bdi>₺1,563.00</bdi>", "<bdi>12.5%</bdi>" })
            Assert.Contains(shown, html, StringComparison.Ordinal);
        Assert.Equal("8", Value("Total orders"));
        Assert.Equal("5", Value("Delivered"));
        Assert.Equal("1310.50", Value("Completed order value (TRY)"));
        Assert.Equal("262.10", Value("Average completed order (TRY)"));
        Assert.Equal("1563.00", Value("All order value (TRY)"));
        Assert.Equal("0.1250", Value("Cancellation or rejection rate"));
        Assert.Equal("Restaurant alpha", records.Single(r => r.Count == 2 && r[0] == "Restaurant")[1]);

        // Both requests resolved the same filter for the same tenant.
        var calls = hosts.Reports.Inner.Calls.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].TenantId, calls[1].TenantId);
        Assert.Equal(calls[0].Filter.Period.DayStartsUtc, calls[1].Filter.Period.DayStartsUtc);
        Assert.Equal(calls[0].Filter.Platform, calls[1].Filter.Platform);
    }

    [Fact]
    public async Task TheFilter_FollowsThePagesRules_AndIgnoresSectionAndComparison()
    {
        Reset();
        using var response = await ExportAsync("preset=CUSTOM&startDate=2026-10-01&endDate=2026-10-05&platform=getiryemek&section=cancellations&compare=1&format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(hosts.Reports.Inner.Calls);
        Assert.Equal(hosts.AlphaId, call.TenantId);
        Assert.Equal(new DateOnly(2026, 10, 1), call.Filter.Period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 5), call.Filter.Period.EndDate);
        Assert.Equal(FoodPlatform.GetirYemek, call.Filter.Platform);
        // Days start at local midnight in Türkiye (UTC+3).
        Assert.Equal(new DateTime(2026, 9, 30, 21, 0, 0, DateTimeKind.Utc), call.Filter.Period.UtcStartInclusive);
        Assert.Equal("wasla-orders-report_2026-10-01_2026-10-05_getiryemek.csv", response.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
    }

    [Fact]
    public async Task NoFilter_IsThePagesDefault_TheLast30Days()
    {
        Reset();
        using var response = await ExportAsync("format=xlsx");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(hosts.Reports.Inner.Calls);
        Assert.Equal(OrderReportPreset.Last30Days, call.Filter.Period.Preset);
        Assert.Null(call.Filter.Platform);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("manager", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("kitchen", HttpStatusCode.Redirect)]
    [InlineData("cashier", HttpStatusCode.Redirect)]
    public async Task OnlyReportRoles_GetAFile_WhateverTheButtonShows(string role, HttpStatusCode expected)
    {
        Reset();
        foreach (var format in new[] { "csv", "pdf", "xlsx" })
        {
            using var response = await ExportAsync($"{Range}&format={format}", role);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Redirect)
            {
                Assert.Contains("access-denied", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Null(response.Content.Headers.ContentDisposition);
            }
        }

        Assert.Equal(expected == HttpStatusCode.OK ? 3 : 0, hosts.Reports.Inner.Calls.Count);
    }

    [Fact]
    public async Task SignedOut_IsSentToLogin_AndRunsNoReport()
    {
        Reset();
        using var client = hosts.AnonymousClient();
        using var response = await client.GetAsync("/reports/export?format=csv");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/auth/login", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(hosts.Reports.Inner.Calls);
    }

    [Fact]
    public async Task EachTenant_ExportsOnlyItsOwnReport_AndASessionDoesNotCrossHosts()
    {
        Reset();
        using var alpha = await ExportAsync(Range + "&format=csv");
        using var beta = await ExportAsync(Range + "&format=csv", "beta");

        Assert.Equal(new[] { hosts.AlphaId, hosts.BetaId }, hosts.Reports.Inner.Calls.Select(c => c.TenantId));
        Assert.Contains("Restaurant beta", CsvText(await beta.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        Assert.DoesNotContain("Restaurant beta", CsvText(await alpha.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)), StringComparison.Ordinal);

        Reset();
        using var replay = hosts.AlphaCookiesOnBetaHost();
        using var crossed = await replay.GetAsync("/reports/export?format=csv");
        Assert.NotEqual(HttpStatusCode.OK, crossed.StatusCode);
        Assert.Null(crossed.Content.Headers.ContentDisposition);
        Assert.Empty(hosts.Reports.Inner.Calls);
    }

    [Theory]
    [InlineData("preset=custom&startDate=2026-01-01&endDate=2026-10-08&format=csv", "92")]
    [InlineData("preset=custom&startDate=2026-10-01&endDate=2026-12-01&format=pdf", "")]
    [InlineData("preset=custom&startDate=2026-10-08&endDate=2026-10-01&format=xlsx", "")]
    [InlineData("preset=yesterday&preset=today&format=csv", "")]
    [InlineData("platform=Glovo&format=csv", "")]
    [InlineData("preset=custom&startDate=2026-10-06&format=csv", "")]
    public async Task ARefusedFilter_Answers400_WithItsReason_AndRunsNoReport(string query, string mentions)
    {
        Reset();
        using var response = await ExportAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await TextAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(mentions, text, StringComparison.Ordinal);
        Assert.Empty(hosts.Reports.Inner.Calls);
    }

    [Theory]
    [InlineData(Range)]
    [InlineData(Range + "&format=")]
    [InlineData(Range + "&format=docx")]
    [InlineData(Range + "&format=csv&format=pdf")]
    public async Task AMissingOrUnknownFormat_Answers400_AndRunsNoReport(string query)
    {
        Reset();
        using var response = await ExportAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Choose CSV, PDF or Excel (.xlsx).", await TextAsync(response));
        Assert.Empty(hosts.Reports.Inner.Calls);
    }

    [Fact]
    public async Task AFailedReport_Answers500_WithAPlainMessage_AndNoFile()
    {
        Reset(ReportsPageWebTests.ReportMode.Fail);
        using var response = await ExportAsync(Range + "&format=pdf");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("The file could not be created. The report on this page is unchanged; try again.", await TextAsync(response));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task AnEmptyPeriod_IsAFileThatSaysSo()
    {
        Reset(ReportsPageWebTests.ReportMode.Empty);
        using var response = await ExportAsync(Range + "&format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = ParseCsv(CsvText(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("No orders in this period", Section(records, "Notes")[0][0]);
        Assert.Equal("0", Section(records, "Summary").Single(r => r[0] == "Total orders")[1]);
    }

    [Fact]
    public async Task ASecondExportOfTheSameUser_IsRefusedWhileTheFirstRuns_OtherUsersAreNot()
    {
        Reset();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Reports.Hold = hold;

        var first = ExportAsync(Range + "&format=pdf");
        Assert.True(await hosts.Reports.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "The first export did not start.");

        using (var second = await ExportAsync(Range + "&format=csv"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.Equal("5", second.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("An export of yours is already being prepared. Wait for it to finish, then try again.", await TextAsync(second));
            Assert.Null(second.Content.Headers.ContentDisposition);
        }

        hosts.Reports.Hold = null;
        using (var manager = await ExportAsync(Range + "&format=csv", "manager"))
            Assert.Equal(HttpStatusCode.OK, manager.StatusCode);

        hold.SetResult();
        using (var firstResponse = await first)
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // The place is free again once the first file is done.
        using var third = await ExportAsync(Range + "&format=csv");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task ThePage_OffersTheThreeFormats_ForItsCanonicalFilter()
    {
        Reset();
        var html = WebUtility.HtmlDecode(await TextAsync(await hosts.Client("owner").GetAsync("/reports?preset=CUSTOM&startDate=2026-10-06&endDate=2026-10-08&platform=3&section=platforms&compare=1")));

        foreach (var format in new[] { "csv", "pdf", "xlsx" })
            Assert.Contains($"href=\"/reports/export?preset=custom&startDate=2026-10-06&endDate=2026-10-08&platform=TrendyolYemek&format={format}\"", html, StringComparison.Ordinal);
        Assert.Contains(">Excel (.xlsx)<", html, StringComparison.Ordinal);
        Assert.Equal(3, html.Split("data-rpt-export=").Length - 1);
        Assert.DoesNotContain("aria-controls=\"rptExportMenu\" disabled", html, StringComparison.Ordinal);

        // A refused filter offers no file.
        var refused = WebUtility.HtmlDecode(await TextAsync(await hosts.Client("owner").GetAsync("/reports?preset=bogus")));
        Assert.Contains("aria-controls=\"rptExportMenu\" disabled=\"disabled\"", refused, StringComparison.Ordinal);
    }

    public sealed class Hosts : IAsyncLifetime
    {
        private readonly Dictionary<string, TenantSessionClient> _clients = new(StringComparer.Ordinal);
        private TenantSessionWebHost _web = null!;

        public HoldingReports Reports { get; } = new(new ReportsPageWebTests.FixedReports());

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
                services.AddWaslaReportExports();
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

    /// <summary>The known reports, optionally held inside the read so a test can act while an export is running.</summary>
    public sealed class HoldingReports(ReportsPageWebTests.FixedReports inner) : IOrderReportService
    {
        public ReportsPageWebTests.FixedReports Inner { get; } = inner;

        public TaskCompletionSource? Hold { get; set; }

        public SemaphoreSlim Entered { get; } = new(0);

        public async Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct)
        {
            var hold = Hold;
            if (hold is not null)
            {
                Entered.Release();
                await hold.Task.WaitAsync(ct);
            }

            return await Inner.GetAsync(tenantId, filter, ct);
        }
    }
}
