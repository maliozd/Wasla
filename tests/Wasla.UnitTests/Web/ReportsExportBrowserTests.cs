using System.Globalization;
using System.Text.Json;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.UnitTests.Admin;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The report export menu (WAS-184) in a real headless browser over <see cref="TenantVisualWebHost"/>, the real report
/// service and the real export endpoint. The file follows the applied filter, not a phone draft. The menu is busy and
/// unavailable while a file or a report loads. One activation gives one download, a failure is reported and the next try
/// works. It runs from the mouse and the keyboard, on a phone, in Turkish and in Arabic (right to left).
/// <para>
/// Downloads are captured at the page's own anchor click (name, type, first bytes and, for CSV, the text) instead of being
/// saved. A server-side hold (<see cref="TenantVisualWebHost.HoldNextRequest"/>) keeps an export in progress. The failure
/// test answers one export with 500 inside the page, because a real server failure cannot be produced through this menu
/// (the server's 400, 429 and 500 answers are covered by ReportsExportWebTests).
/// </para>
/// </summary>
[Trait("Category", "Browser")]
public sealed class ReportsExportBrowserTests : IAsyncLifetime
{
    private const string Result = "document.querySelector('#rptResult [data-rpt-result-root]')";
    private const string Settled = $"!!{Result} && document.getElementById('rptResult').getAttribute('aria-busy') !== 'true'";
    private const string MenuOpen = "document.getElementById('rptExportMenu').classList.contains('show')";
    private const string MenuClosed = $"!{MenuOpen} && {Toggle}.getAttribute('aria-expanded') === 'false'";
    private const string Toggle = "document.getElementById('rptExportToggle')";
    private const string Message = "document.getElementById('rptExportMessage')";

    /// <summary>Records export requests and downloads; <c>window.__failNextExport</c> answers the next export with 500.</summary>
    private const string Instrument = """
        (() => {
          window.__exportRequests = [];
          window.__downloads = [];
          const fetch = window.fetch.bind(window);
          window.fetch = function (input, init) {
            const url = typeof input === 'string' ? input : input.url;
            if (url.includes('/reports/export')) {
              window.__exportRequests.push(url);
              if (window.__failNextExport) {
                window.__failNextExport = false;
                return Promise.resolve(new Response('', { status: 500 }));
              }
            }
            return fetch(input, init);
          };
          const blobs = new Map();
          const createObjectURL = URL.createObjectURL.bind(URL);
          URL.createObjectURL = function (blob) {
            const url = createObjectURL(blob);
            blobs.set(url, blob);
            return url;
          };
          const click = HTMLAnchorElement.prototype.click;
          HTMLAnchorElement.prototype.click = function () {
            if (this.hasAttribute('download') && this.href.startsWith('blob:')) {
              const blob = blobs.get(this.href);
              const entry = { name: this.download, type: blob ? blob.type : '', size: blob ? blob.size : -1, head: null, text: null };
              window.__downloads.push(entry);
              if (blob) {
                blob.slice(0, 5).arrayBuffer().then(b => { entry.head = Array.from(new Uint8Array(b)).map(x => x.toString(16).padStart(2, '0')).join(''); });
                if (blob.type.startsWith('text/csv')) blob.text().then(t => { entry.text = t; });
              }
              return;
            }
            return click.call(this);
          };
        })();
        """;

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<HeadlessChromium> OpenAsync(string culture, int width, bool mobile, string path, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, width, 900, mobile, ct);
        if (browser is null)
            Assert.Skip("No Chromium browser is installed.");
        await browser.AddScriptBeforePageScriptsAsync(Instrument, ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        Assert.True(await browser.WaitForAsync(Settled, TimeSpan.FromSeconds(15), ct), $"{path} did not render. {_host.ServerErrors}");
        if (!await browser.EvaluateAsync<bool>("!!window.bootstrap", ct))
            Assert.Skip("Bootstrap could not be loaded from its CDN.");
        return browser;
    }

    /// <summary>A real mouse click in the middle of the element.</summary>
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var point = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
              element.scrollIntoView({ block: 'center', inline: 'center' });
              const r = element.getBoundingClientRect();
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """, ct);
        await browser.ClickAtAsync(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble(), ct);
    }

    private static async Task ExportWithMouseAsync(HeadlessChromium browser, string format, CancellationToken ct)
    {
        await ClickAsync(browser, "#rptExportToggle", ct);
        Assert.True(await browser.WaitForAsync(MenuOpen, TimeSpan.FromSeconds(5), ct), "The export menu did not open.");
        await ClickAsync(browser, $"[data-rpt-export={format}]", ct);
        Assert.True(await browser.WaitForAsync(MenuClosed, TimeSpan.FromSeconds(5), ct), "The export menu stayed open after a format was chosen.");
    }

    private static Task<int> DownloadsAsync(HeadlessChromium browser, CancellationToken ct) => browser.EvaluateAsync<int>("window.__downloads.length", ct);

    private static Task<int> RequestsAsync(HeadlessChromium browser, CancellationToken ct) => browser.EvaluateAsync<int>("window.__exportRequests.length", ct);

    private static string Text(string key, string culture, string? format = null)
    {
        var text = L(key, culture);
        return format is null ? text : text.Replace("{0}", L("Reports.Export." + format, culture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr-TR", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task TheFile_FollowsTheAppliedFilter_AndEachActivationGivesOneDownload(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 1280, false, "/reports", ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));

        // Apply a period and a platform with the page's own controls.
        await browser.EvaluateAsync<bool>("(document.querySelector('a[data-rpt-preset=last7days]').click(), true)", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '?preset=last7days' && {Settled}", TimeSpan.FromSeconds(15), ct));
        await browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('rptPlatform'); s.value = 'TrendyolYemek'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '?preset=last7days&platform=TrendyolYemek' && {Settled}", TimeSpan.FromSeconds(15), ct));
        foreach (var format in new[] { "csv", "pdf", "xlsx" })
            Assert.Equal($"/reports/export?preset=last7days&platform=TrendyolYemek&format={format}", await browser.EvaluateAsync<string>($"document.querySelector('[data-rpt-export={format}]').getAttribute('href')", ct));

        await ExportWithMouseAsync(browser, "csv", ct);
        Assert.True(await browser.WaitForAsync("window.__downloads.length === 1 && window.__downloads[0].text !== null", TimeSpan.FromSeconds(30), ct), $"No CSV was downloaded. {_host.ServerErrors}");
        var csv = await browser.EvaluateAsync<JsonElement>("window.__downloads[0]", ct);
        var period = OrderReportFilters.ForPreset(OrderReportPreset.Last7Days, DateTime.UtcNow, TimeZoneHelper.ResolveTurkeyTimeZone());
        var dates = $"{period.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{period.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        Assert.Equal($"wasla-orders-report_{dates}_trendyolyemek.csv", csv.GetProperty("name").GetString());
        Assert.StartsWith("text/csv", csv.GetProperty("type").GetString(), StringComparison.Ordinal);
        var text = csv.GetProperty("text").GetString()!;
        Assert.Contains("Visual Test Restaurant", text, StringComparison.Ordinal);
        Assert.Contains($"{L("Reports.PlatformLabel", culture)},Trendyol GO", text, StringComparison.Ordinal);
        Assert.Contains(period.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains("platform=TrendyolYemek", await browser.EvaluateAsync<string>("window.__exportRequests[0]", ct), StringComparison.Ordinal);
        Assert.True(await browser.WaitForAsync(
            $"{Message}.dataset.tone === 'success' && {Message}.textContent === {JsonSerializer.Serialize(Text("Reports.Export.Ready", culture, "Csv"))}",
            TimeSpan.FromSeconds(5), ct));

        // Each further activation gives exactly one more file of its own format.
        var expected = 1;
        foreach (var (format, magic) in new[] { ("xlsx", "504b0304"), ("pdf", "255044462d") })
        {
            await ExportWithMouseAsync(browser, format, ct);
            expected++;
            Assert.True(await browser.WaitForAsync($"window.__downloads.length === {expected} && window.__downloads[{expected - 1}].head !== null", TimeSpan.FromSeconds(60), ct), $"No {format} was downloaded. {_host.ServerErrors}");
            Assert.StartsWith(magic, await browser.EvaluateAsync<string>($"window.__downloads[{expected - 1}].head", ct), StringComparison.Ordinal);
            Assert.EndsWith($"_trendyolyemek.{format}", await browser.EvaluateAsync<string>($"window.__downloads[{expected - 1}].name", ct), StringComparison.Ordinal);
        }

        await Task.Delay(500, ct);
        Assert.Equal(3, await DownloadsAsync(browser, ct));
        Assert.Equal(3, await RequestsAsync(browser, ct));
    }

    [Fact]
    public async Task WhileAFileOrAReportLoads_TheMenuIsBusy_AndASecondActivationStartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("tr-TR", 1280, false, "/reports", ct);

        var hold = _host.HoldNextRequest("GET", "/reports/export");
        await ExportWithMouseAsync(browser, "pdf", ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.True(await browser.WaitForAsync(
            $"{Toggle}.classList.contains('disabled') && {Toggle}.getAttribute('aria-busy') === 'true' && {Toggle}.getAttribute('aria-disabled') === 'true'"
            + $" && !document.querySelector('.rpt-export__spinner').hidden && document.activeElement === {Toggle}"
            + $" && {Message}.textContent === {JsonSerializer.Serialize(Text("Reports.Export.Preparing", "tr-TR", "Pdf"))}",
            TimeSpan.FromSeconds(5), ct), "The menu did not show the file being prepared.");

        // Neither the mouse nor a script click on an item starts a second export.
        await ClickAsync(browser, "#rptExportToggle", ct);
        await Task.Delay(400, ct);
        Assert.False(await browser.EvaluateAsync<bool>(MenuOpen, ct), "The menu opened while a file was being made.");
        await browser.EvaluateAsync<bool>("(document.querySelector('[data-rpt-export=csv]').click(), true)", ct);
        await Task.Delay(400, ct);
        Assert.Equal(1, await RequestsAsync(browser, ct));
        Assert.Equal(0, await DownloadsAsync(browser, ct));

        hold.Release();
        Assert.True(await browser.WaitForAsync(
            $"window.__downloads.length === 1 && window.__downloads[0].head !== null && !{Toggle}.classList.contains('disabled') && {Toggle}.getAttribute('aria-busy') === null"
            + " && document.querySelector('.rpt-export__spinner').hidden",
            TimeSpan.FromSeconds(60), ct), $"The held export did not finish. {_host.ServerErrors}");
        Assert.StartsWith("255044462d", await browser.EvaluateAsync<string>("window.__downloads[0].head", ct), StringComparison.Ordinal);
        Assert.Equal(1, await RequestsAsync(browser, ct));

        // While a report loads, its file is not offered.
        var load = _host.HoldNextRequest("GET", "/reports/content");
        await browser.EvaluateAsync<bool>("(document.querySelector('a[data-rpt-preset=yesterday]').click(), true)", ct);
        await load.Reached.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.True(await browser.WaitForAsync($"{Toggle}.disabled === true", TimeSpan.FromSeconds(5), ct), "The menu stayed available while the report loaded.");
        load.Release();
        Assert.True(await browser.WaitForAsync($"{Settled} && location.search === '?preset=yesterday' && {Toggle}.disabled === false", TimeSpan.FromSeconds(15), ct));
        Assert.Equal("/reports/export?preset=yesterday&format=csv", await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-export=csv]').getAttribute('href')", ct));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public async Task AFailedExport_SaysSo_AndTheNextTrySucceeds(string culture)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 1280, false, "/reports", ct);

        await browser.EvaluateAsync<bool>("(window.__failNextExport = true)", ct);
        await ExportWithMouseAsync(browser, "xlsx", ct);
        Assert.True(await browser.WaitForAsync(
            $"{Message}.dataset.tone === 'danger' && {Message}.textContent === {JsonSerializer.Serialize(Text("Reports.Export.Failed", culture))} && !{Toggle}.classList.contains('disabled') && !{Toggle}.disabled",
            TimeSpan.FromSeconds(10), ct), "The failure was not reported, or the menu stayed busy.");
        Assert.Equal(0, await DownloadsAsync(browser, ct));

        await ExportWithMouseAsync(browser, "xlsx", ct);
        Assert.True(await browser.WaitForAsync($"window.__downloads.length === 1 && window.__downloads[0].head !== null && {Message}.dataset.tone === 'success'", TimeSpan.FromSeconds(60), ct),
            $"The next try did not download. {_host.ServerErrors}");
        Assert.StartsWith("504b0304", await browser.EvaluateAsync<string>("window.__downloads[0].head", ct), StringComparison.Ordinal);
        Assert.Equal(2, await RequestsAsync(browser, ct));
    }

    [Theory]
    [InlineData("tr-TR", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task OnAPhone_TheMenuFits_UsesTheAppliedFilterNotADraft_AndWorksFromTheKeyboard(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 390, true, "/reports", ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
        Assert.False(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1", ct), "The page scrolls sideways.");
        Assert.True(await browser.EvaluateAsync<bool>($"(() => {{ const r = {Toggle}.getBoundingClientRect(); return r.width > 0 && r.left >= 0 && r.right <= innerWidth; }})()", ct),
            "The export button is not fully on screen.");

        // A platform chosen in the filter sheet is a draft until Apply; the file follows what is applied.
        await browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('rptPlatform'); s.value = 'GetirYemek'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()", ct);
        Assert.Equal("/reports/export?format=pdf", await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-export=pdf]').getAttribute('href')", ct));
        await browser.EvaluateAsync<bool>("(document.getElementById('rptSheetApply').click(), true)", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '?platform=GetirYemek' && {Settled}", TimeSpan.FromSeconds(15), ct));
        Assert.Equal("/reports/export?platform=GetirYemek&format=pdf", await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-export=pdf]').getAttribute('href')", ct));

        // Keyboard only: Enter opens the menu, Tab moves through the formats, Enter makes the file.
        await browser.EvaluateAsync<bool>($"(window.scrollTo(0, 0), {Toggle}.focus(), document.activeElement === {Toggle})", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(MenuOpen, TimeSpan.FromSeconds(5), ct), "Enter did not open the export menu.");
        Assert.True(await browser.EvaluateAsync<bool>("(() => { const r = document.getElementById('rptExportMenu').getBoundingClientRect(); return r.left >= -1 && r.right <= innerWidth + 1; })()", ct),
            "The export menu does not fit the phone screen.");
        Assert.False(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1", ct), "The open menu makes the page scroll sideways.");
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("csv", await browser.EvaluateAsync<string>("document.activeElement.dataset.rptExport ?? ''", ct));
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("pdf", await browser.EvaluateAsync<string>("document.activeElement.dataset.rptExport ?? ''", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(MenuClosed, TimeSpan.FromSeconds(5), ct), "The export menu stayed open after Enter chose a format.");

        Assert.True(await browser.WaitForAsync($"window.__downloads.length === 1 && window.__downloads[0].head !== null && !{Toggle}.classList.contains('disabled')", TimeSpan.FromSeconds(60), ct),
            $"The keyboard did not download the PDF. {_host.ServerErrors}");
        Assert.EndsWith("_getiryemek.pdf", await browser.EvaluateAsync<string>("window.__downloads[0].name", ct), StringComparison.Ordinal);
        Assert.True(await browser.EvaluateAsync<bool>($"document.activeElement === {Toggle}", ct), "Focus did not return to the export button.");
        Assert.Equal(1, await RequestsAsync(browser, ct));

        // From the button that kept focus, Enter opens the menu again and the next file follows.
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(MenuOpen, TimeSpan.FromSeconds(5), ct), "Enter did not open the export menu a second time.");
        foreach (var _ in new[] { "csv", "pdf", "xlsx" })
            await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("xlsx", await browser.EvaluateAsync<string>("document.activeElement.dataset.rptExport ?? ''", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(MenuClosed, TimeSpan.FromSeconds(5), ct), "The export menu stayed open after the second choice.");
        Assert.True(await browser.WaitForAsync("window.__downloads.length === 2 && window.__downloads[1].head !== null", TimeSpan.FromSeconds(60), ct),
            $"The second keyboard export did not download. {_host.ServerErrors}");
        Assert.StartsWith("504b0304", await browser.EvaluateAsync<string>("window.__downloads[1].head", ct), StringComparison.Ordinal);
        Assert.Equal(2, await RequestsAsync(browser, ct));
    }

    [Fact]
    public async Task ARefusedFilter_OffersNoFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("tr-TR", 1280, false, "/reports?preset=bogus", ct);

        Assert.True(await browser.EvaluateAsync<bool>($"{Toggle}.disabled", ct));
        Assert.Equal(L("Reports.Export.Unavailable", "tr-TR"), await browser.EvaluateAsync<string>($"{Toggle}.title", ct));
        await ClickAsync(browser, "#rptExportToggle", ct);
        await Task.Delay(300, ct);
        Assert.False(await browser.EvaluateAsync<bool>(MenuOpen, ct));
        Assert.Equal(0, await RequestsAsync(browser, ct));
    }
}
