using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The report page (WAS-183) in a real headless browser over <see cref="TenantVisualWebHost"/> and the real report service:
/// filters applied in place and kept in the URL, a stale answer, a failed request and retry, a refused range, chart
/// keyboard inspection, and layout in every culture at desktop and phone widths. Skips only when no Chromium browser is
/// installed.
/// </summary>
[Trait("Category", "Browser")]
public sealed class ReportsPageBrowserTests : IAsyncLifetime
{
    private const string Result = "document.querySelector('#rptResult [data-rpt-result-root]')";
    private const string Range = "(document.querySelector('#rptPeriod .rpt-period__range')?.textContent ?? '')";

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, bool mobile, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, width, 900, mobile, ct);
        if (browser is null)
            Assert.Skip("No Chromium browser is installed.");
        return browser;
    }

    private async Task OpenAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        Assert.True(await browser.WaitForAsync($"!!{Result}", TimeSpan.FromSeconds(15), ct), $"{path} did not render. {_host.ServerErrors}");
    }

    private static Task<bool> SettledAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.WaitForAsync($"!!{Result} && document.getElementById('rptResult').getAttribute('aria-busy') !== 'true'", TimeSpan.FromSeconds(15), ct);

    private static async Task ClickPresetAsync(HeadlessChromium browser, string preset, CancellationToken ct) =>
        await browser.EvaluateAsync<bool>($"(document.querySelector('a[data-rpt-preset={preset}]').click(), true)", ct);

    [Fact]
    public async Task FilterChange_UpdatesInPlace_AndTheUrlSurvivesReloadAndBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);
        await browser.EvaluateAsync<bool>("(window.__samePage = true)", ct);
        var defaultRange = await browser.EvaluateAsync<string>(Range, ct);

        await ClickPresetAsync(browser, "yesterday", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '?preset=yesterday' && {Range} !== {JsonSerializer.Serialize(defaultRange)}", TimeSpan.FromSeconds(15), ct));
        Assert.True(await browser.EvaluateAsync<bool>("window.__samePage === true", ct), "The filter reloaded the page.");
        var yesterday = await browser.EvaluateAsync<string>(Range, ct);

        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/reports?preset=yesterday&section=cancellations"), ct);
        Assert.True(await browser.WaitForAsync($"{Range} === {JsonSerializer.Serialize(yesterday)}", TimeSpan.FromSeconds(15), ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('a[data-rpt-preset=yesterday]').getAttribute('aria-current') === 'true'", ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('a[data-rpt-section=cancellations]').getAttribute('aria-current') === 'page'", ct));

        // Coming back through the sidebar link restores the last filter.
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/orders"), ct);
        await browser.EvaluateAsync<bool>("(window.__onOrders = true, setTimeout(() => { location.href = '/reports'; }, 0), true)", ct);
        await browser.WaitForNavigationAsync("__onOrders", ct);
        Assert.True(await browser.WaitForAsync($"location.search.includes('preset=yesterday') && {Range} === {JsonSerializer.Serialize(yesterday)}", TimeSpan.FromSeconds(15), ct),
            "The filter was not restored after navigating away.");
    }

    [Fact]
    public async Task BackAndForward_ReapplyTheFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);
        var defaultRange = await browser.EvaluateAsync<string>(Range, ct);

        await ClickPresetAsync(browser, "yesterday", ct);
        Assert.True(await browser.WaitForAsync($"{Range} !== {JsonSerializer.Serialize(defaultRange)}", TimeSpan.FromSeconds(15), ct));

        await browser.EvaluateAsync<bool>("(history.back(), true)", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '' && {Range} === {JsonSerializer.Serialize(defaultRange)}", TimeSpan.FromSeconds(15), ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('a[data-rpt-preset=last30days]').classList.contains('is-selected')", ct));
    }

    [Fact]
    public async Task ASlowOlderAnswer_NeverReplacesANewerSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);

        var slow = _host.HoldNextRequest("GET", "/reports/content");
        await ClickPresetAsync(browser, "last7days", ct);
        await slow.Reached.WaitAsync(TimeSpan.FromSeconds(15), ct);

        await ClickPresetAsync(browser, "yesterday", ct);
        Assert.True(await browser.WaitForAsync($"location.search === '?preset=yesterday' && !!{Result} && document.getElementById('rptResult').getAttribute('aria-busy') === 'false'", TimeSpan.FromSeconds(15), ct));
        var range = await browser.EvaluateAsync<string>(Range, ct);

        slow.Release();
        await Task.Delay(500, ct);
        Assert.Equal(range, await browser.EvaluateAsync<string>(Range, ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('a[data-rpt-preset=yesterday]').classList.contains('is-selected')", ct));
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task AFailedRequest_KeepsTheFilter_AndRetryRecovers(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, width < 768, ct);
        await OpenAsync(browser, "/reports", ct);
        await browser.EvaluateAsync<bool>("""
            (() => {
              const original = window.fetch;
              window.__failures = 1;
              window.fetch = function (input) {
                if (String(input).includes('/reports/content') && window.__failures-- > 0)
                  return Promise.resolve(new Response('', { status: 500 }));
                return original.apply(this, arguments);
              };
              return true;
            })()
            """, ct);

        // Through the state the page applies directly, so the phone sheet does not need to be opened.
        await browser.EvaluateAsync<bool>("(document.querySelector('a[data-rpt-section=cancellations]').click(), true)", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#rptResult [data-rpt-retry]')", TimeSpan.FromSeconds(15), ct), "The failure state did not appear.");
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
        Assert.True(await browser.EvaluateAsync<bool>("location.search.includes('section=cancellations') && document.querySelector('#rptResult [role=alert]') !== null", ct));

        await browser.EvaluateAsync<bool>("(document.querySelector('#rptResult [data-rpt-retry]').click(), true)", ct);
        Assert.True(await browser.WaitForAsync($"!!{Result} && {Result}.dataset.state !== 'Failed'", TimeSpan.FromSeconds(15), ct), "Retry did not recover.");
    }

    [Fact]
    public async Task ARefusedRange_ShowsTheFieldError_AndNoReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);

        await browser.EvaluateAsync<bool>("""
            (() => {
              document.getElementById('rptCustomToggle').click();
              document.getElementById('rptStartDate').value = '2026-10-05';
              document.getElementById('rptEndDate').value = '2026-10-01';
              document.getElementById('rptCustomForm').requestSubmit();
              return true;
            })()
            """, ct);

        Assert.True(await browser.WaitForAsync($"!!{Result} && {Result}.dataset.state === 'InvalidFilter'", TimeSpan.FromSeconds(15), ct));
        Assert.Contains("cannot be before", await browser.EvaluateAsync<string>("document.getElementById('rptEndError').textContent", ct), StringComparison.Ordinal);
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('rptEndDate').getAttribute('aria-invalid') === 'true' && !document.getElementById('rptCustomForm').hidden", ct));
    }

    [Theory]
    [InlineData("en-US", "ArrowLeft")]
    [InlineData("ar-SA", "ArrowRight")]
    public async Task ChartDays_AreInspectedByKeyboard_FollowingTheScreenDirection(string culture, string towardsEarlier)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);

        var box = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const column = document.querySelector('[data-rpt-col][tabindex="0"]');
              column.scrollIntoView({ block: 'center' });
              const r = column.getBoundingClientRect();
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """, ct);
        await browser.ClickAtAsync(box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(), ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('.rpt-tip') && !!document.activeElement.dataset.title", TimeSpan.FromSeconds(5), ct), "Selecting a day did not show its values.");
        var last = await browser.EvaluateAsync<string>("document.activeElement.dataset.title", ct);

        await browser.PressKeyAsync(towardsEarlier, shift: false, ct);
        Assert.True(await browser.WaitForAsync($"document.activeElement.dataset.title !== {JsonSerializer.Serialize(last)}", TimeSpan.FromSeconds(5), ct), "The arrow key did not move to the earlier day.");
        Assert.True(await browser.EvaluateAsync<bool>("[...document.activeElement.parentElement.children].indexOf(document.activeElement) === document.activeElement.parentElement.children.length - 2", ct));
        Assert.Equal(await browser.EvaluateAsync<string>("document.activeElement.dataset.title", ct), await browser.EvaluateAsync<string>("document.querySelector('.rpt-tip__title').textContent", ct));
    }

    [Fact]
    public async Task ChartAndTable_ShowTheSameDays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, "/reports", ct);

        await browser.EvaluateAsync<bool>("(document.querySelector('[data-rpt-panel=daily] [data-rpt-view=table]').click(), true)", ct);
        Assert.True(await browser.EvaluateAsync<bool>("""
            (() => {
              const panel = document.querySelector('[data-rpt-panel=daily]');
              return panel.querySelector('[data-rpt-panel-chart]').hidden
                && !panel.querySelector('[data-rpt-panel-table]').hidden
                && panel.querySelectorAll('tbody tr').length === 30
                && panel.querySelector('[data-rpt-view=table]').getAttribute('aria-pressed') === 'true';
            })()
            """, ct));
    }

    [Theory]
    [InlineData("tr-TR", "ltr", 1280, false)]
    [InlineData("tr-TR", "ltr", 390, true)]
    [InlineData("en-US", "ltr", 390, true)]
    [InlineData("ar-SA", "rtl", 1280, false)]
    [InlineData("ar-SA", "rtl", 390, true)]
    [InlineData("ru-RU", "ltr", 1280, false)]
    [InlineData("ru-RU", "ltr", 390, true)]
    public async Task EverySection_FitsTheScreen_InEveryCulture(string culture, string direction, int width, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile, ct);

        foreach (var path in new[] { "/reports", "/reports?section=platforms", "/reports?section=cancellations" })
        {
            await OpenAsync(browser, path, ct);
            Assert.True(await SettledAsync(browser, ct));
            Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
            Assert.False(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1", ct), $"{path} {culture} {width}px scrolls sideways");
            Assert.Equal(3, await browser.EvaluateAsync<int>("document.querySelectorAll('.rpt-tabs a').length", ct));
        }
    }

    [Fact]
    public async Task OnAPhone_FilterChoicesWaitForApply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 390, true, ct);
        await OpenAsync(browser, "/reports", ct);
        if (!await browser.EvaluateAsync<bool>("!!window.bootstrap", ct))
            Assert.Skip("Bootstrap could not be loaded from its CDN.");

        await browser.EvaluateAsync<bool>("(document.querySelector('[data-bs-target=\"#rptFilterSheet\"][data-bs-toggle]').click(), true)", ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('rptFilterSheet').classList.contains('show')", TimeSpan.FromSeconds(5), ct));

        await ClickPresetAsync(browser, "yesterday", ct);
        Assert.Equal("", await browser.EvaluateAsync<string>("location.search", ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('a[data-rpt-preset=yesterday]').classList.contains('is-selected')", ct));

        await browser.EvaluateAsync<bool>("(document.getElementById('rptSheetApply').click(), true)", ct);
        Assert.True(await browser.WaitForAsync("location.search === '?preset=yesterday' && !document.getElementById('rptFilterSheet').classList.contains('show')", TimeSpan.FromSeconds(10), ct));
    }
}
