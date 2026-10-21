using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Keyboard focus, text selection and table scrolling on the Print Bridge Devices page while its regions refresh
/// (WAS-132), in a real headless Chromium browser (Edge or Chrome) against the populated tenant application of
/// <see cref="TenantVisualWebHost"/>. The print-job history keeps its 3-second poll: an unchanged answer leaves the
/// region and the focused button alone; a changed answer gives focus back to the same job's button and keeps the table's
/// scroll position; while the focused button would disappear, or text is selected in the region, the update waits until
/// the user moves on, and focus never moves into or out of the region on its own. The devices table, refreshed through
/// <c>WaslaPrintBridge.refreshDevices()</c>, follows the same rules. Every wait is on observable page state (requests the
/// page started and rows it shows), never a fixed sleep. Skips only when no Chromium browser is installed (see
/// <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class PrintBridgeDevicesFocusBrowserTests : IAsyncLifetime
{
    // Runs before the page's scripts: counts the print-job reads the page starts (its in-flight guard starts the next one
    // only after the previous answer was applied), counts writes to the job region, and records page errors.
    private const string Probe = """
        (() => {
          const t = window.__printJobs = { requests: 0, writes: 0 };
          const errors = window.__waslaPageErrors = [];
          addEventListener('error', e => errors.push(String(e.message)));
          addEventListener('unhandledrejection', e => errors.push('unhandled rejection: ' + String(e.reason)));
          const original = window.fetch;
          window.fetch = function (input, init) {
            const url = String((input && input.url) || input);
            if (url.indexOf('/print-bridge/print-jobs') === 0 && !(init && init.method && init.method !== 'GET')) t.requests++;
            return original.apply(this, arguments);
          };
          document.addEventListener('DOMContentLoaded', () => {
            const panel = document.getElementById('printBridgeJobsPanel');
            if (panel) new MutationObserver(list => { t.writes += list.length; }).observe(panel, { childList: true });
          });
        })();
        """;

    private const string State = """
        (() => {
          const panel = document.getElementById('printBridgeJobsPanel');
          const devices = document.getElementById('printBridgeDevicesPanel');
          const a = document.activeElement;
          const sel = getSelection();
          const range = sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
          const scroller = panel.querySelector('.table-responsive');
          return {
            dir: document.documentElement.getAttribute('dir'),
            requests: window.__printJobs.requests, writes: window.__printJobs.writes,
            focused: a ? (a.id || a.tagName) : null,
            focusedJob: a && a.matches('.pb-reprint-job-btn') ? a.getAttribute('data-job-id') : null,
            focusedDevice: a && a.closest('tr[data-device-id]') ? a.closest('tr[data-device-id]').getAttribute('data-device-id') : null,
            focusedToggle: !!a && a.matches('.pb-device-active-toggle'),
            sameNode: !!a && a === window.__waslaFocused,
            focusVisible: !!a && a.matches(':focus-visible'),
            focusInJobs: panel.contains(a), focusInDevices: devices.contains(a),
            selection: sel.toString(),
            selectionConnected: !!range && range.startContainer.isConnected,
            jobs: [...panel.querySelectorAll('tr[data-print-job-id]')].map(r => r.getAttribute('data-print-job-id')),
            reprint: [...panel.querySelectorAll('.pb-reprint-job-btn')].map(b => b.getAttribute('data-job-id')),
            deviceNames: [...devices.querySelectorAll('tr[data-device-id] .fw-semibold')].map(e => e.textContent),
            scrollLeft: scroller ? scroller.scrollLeft : 0, scrollY,
            scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
            errors: window.__waslaPageErrors
          };
        })()
        """;

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("tr-TR", 1366, "ltr")]
    [InlineData("en-US", 390, "ltr")]
    [InlineData("ar-SA", 390, "rtl")]
    [InlineData("ar-SA", 1366, "rtl")]
    [InlineData("ru-RU", 1366, "ltr")]
    public async Task UnchangedRefreshes_LeaveTheFocusedButton_TheSelectionAndTheRegionAlone(string culture, int width, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"{culture} {width}px";
        await using var browser = await OpenDevicesPageAsync(culture, width, ct);

        var job = await TabIntoFirstReprintButtonAsync(browser, page, ct);
        var selected = await SelectFirstOrderCodeAsync(browser, ct);
        var before = await StateAsync(browser, ct);
        Assert.Equal(direction, before.GetProperty("dir").GetString());

        await WaitForCompletedPollsAsync(browser, 2, page, ct);

        var after = await StateAsync(browser, ct);
        Assert.True(before.GetProperty("writes").GetInt32() == after.GetProperty("writes").GetInt32(),
            $"{page}: an unchanged print-job answer rewrote the region ({after.GetProperty("writes").GetInt32() - before.GetProperty("writes").GetInt32()} writes)");
        Assert.True(after.GetProperty("sameNode").GetBoolean(),
            $"{page}: focus left the reprint button it was on (now on {after.GetProperty("focused")}, job {after.GetProperty("focusedJob")})");
        Assert.Equal(job, after.GetProperty("focusedJob").GetString());
        Assert.True(after.GetProperty("focusVisible").GetBoolean(), $"{page}: the focused button lost its focus ring");
        Assert.Equal(selected, after.GetProperty("selection").GetString());
        Assert.True(after.GetProperty("selectionConnected").GetBoolean(), $"{page}: the selection points into removed markup");
        AssertNoOverflowOrErrors(after, page);
    }

    [Theory]
    [InlineData("en-US", 1366, "ltr")]
    [InlineData("ar-SA", 390, "rtl")]
    public async Task AChangedRefresh_GivesFocusBackToTheSameJobsButton_AndKeepsTheTableScrolled(string culture, int width, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"{culture} {width}px";
        await using var browser = await OpenDevicesPageAsync(culture, width, ct);

        var job = await TabIntoFirstReprintButtonAsync(browser, page, ct);
        // The phone table scrolls sideways: the user has scrolled it to its inline end (left in RTL).
        await browser.EvaluateAsync<bool>("""
            (() => {
              const s = document.querySelector('#printBridgeJobsPanel .table-responsive');
              const rtl = document.documentElement.dir === 'rtl';
              s.scrollLeft = (rtl ? -1 : 1) * (s.scrollWidth - s.clientWidth);
              return true;
            })()
            """, ct);
        var before = await StateAsync(browser, ct);
        Assert.Equal(direction, before.GetProperty("dir").GetString());
        if (width < 992)
            Assert.True(Math.Abs(before.GetProperty("scrollLeft").GetDouble()) > 10, $"{page}: the job table does not scroll sideways");

        var added = await AddReprintableJobAsync(ct);
        Assert.True(await browser.WaitForAsync(HasJob(added), TimeSpan.FromSeconds(30), ct), $"{page}: the new print job never appeared");

        var after = await StateAsync(browser, ct);
        Assert.False(after.GetProperty("sameNode").GetBoolean(), $"{page}: the region was not replaced, so the change proves nothing");
        Assert.True(job == after.GetProperty("focusedJob").GetString(),
            $"{page}: focus is on {after.GetProperty("focused")} (job {after.GetProperty("focusedJob")}), expected the reprint button of job {job}");
        Assert.True(after.GetProperty("focusInJobs").GetBoolean());
        foreach (var scroll in new[] { "scrollLeft", "scrollY" })
            Assert.True(Math.Abs(before.GetProperty(scroll).GetDouble() - after.GetProperty(scroll).GetDouble()) <= 1,
                $"{page}: {scroll} moved from {before.GetProperty(scroll)} to {after.GetProperty(scroll)}");
        AssertNoOverflowOrErrors(after, page);

        // The restored button still works from the keyboard: Tab moves on to the next job's button.
        await browser.PressKeyAsync("Tab", shift: false, ct);
        var next = await StateAsync(browser, ct);
        Assert.True(next.GetProperty("focusedJob").GetString() is { } nextJob && nextJob != job,
            $"{page}: Tab from the restored button went to {next.GetProperty("focused")}");
    }

    [Fact]
    public async Task WhileTheFocusedButtonWouldDisappear_TheRefreshWaits_UntilTheUserMovesFocusAway()
    {
        var ct = TestContext.Current.CancellationToken;
        const string page = "ru-RU 1366px";
        await using var browser = await OpenDevicesPageAsync("ru-RU", 1366, ct);

        var job = await TabIntoFirstReprintButtonAsync(browser, page, ct);
        await SetJobStatusAsync(Guid.Parse(job), PrintJobStatus.Pending, ct);
        await WaitForCompletedPollsAsync(browser, 2, page, ct);

        var waiting = await StateAsync(browser, ct);
        Assert.True(waiting.GetProperty("sameNode").GetBoolean(),
            $"{page}: the refresh took focus away from the button (now on {waiting.GetProperty("focused")})");
        Assert.Contains(job, waiting.GetProperty("reprint").EnumerateArray().Select(e => e.GetString()));

        // The user moves on (Shift+Tab to the Refresh button, outside the region); the next poll applies the change.
        await browser.PressKeyAsync("Tab", shift: true, ct);
        Assert.Equal("printBridgeRefreshJobsBtn", (await StateAsync(browser, ct)).GetProperty("focused").GetString());
        Assert.True(await browser.WaitForAsync(
            $"![...document.querySelectorAll('#printBridgeJobsPanel .pb-reprint-job-btn')].some(b => b.getAttribute('data-job-id') === {JsonSerializer.Serialize(job)})",
            TimeSpan.FromSeconds(30), ct), $"{page}: the waiting update was never applied");
        var applied = await StateAsync(browser, ct);
        Assert.Equal("printBridgeRefreshJobsBtn", applied.GetProperty("focused").GetString());
        AssertNoOverflowOrErrors(applied, page);
    }

    [Fact]
    public async Task WhileTextIsSelected_AChangedRefreshWaits_AndAppliesOnceTheSelectionEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        const string page = "en-US 1366px";
        await using var browser = await OpenDevicesPageAsync("en-US", 1366, ct);

        var selected = await SelectFirstOrderCodeAsync(browser, ct);
        var added = await AddReprintableJobAsync(ct);
        await WaitForCompletedPollsAsync(browser, 2, page, ct);

        var waiting = await StateAsync(browser, ct);
        Assert.DoesNotContain(added.ToString(), waiting.GetProperty("jobs").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(selected, waiting.GetProperty("selection").GetString());
        Assert.True(waiting.GetProperty("selectionConnected").GetBoolean(), $"{page}: the selection points into removed markup");

        // A click on the page heading ends the selection; the next poll applies the change without moving focus.
        await ClickAsync(browser, "#recent-print-activity h2", ct);
        Assert.Equal("", (await StateAsync(browser, ct)).GetProperty("selection").GetString());
        Assert.True(await browser.WaitForAsync(HasJob(added), TimeSpan.FromSeconds(30), ct), $"{page}: the waiting update was never applied");
        var applied = await StateAsync(browser, ct);
        Assert.False(applied.GetProperty("focusInJobs").GetBoolean(), $"{page}: the update moved focus into the region");
        AssertNoOverflowOrErrors(applied, page);
    }

    [Theory]
    [InlineData("en-US", 1366)]
    [InlineData("ar-SA", 390)]
    public async Task TheDevicesTable_GivesFocusBack_AndWaitsWhileTextIsSelected(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"{culture} {width}px devices";
        await using var browser = await OpenDevicesPageAsync(culture, width, ct);

        // Keyboard: from the first device's details link, Tab reaches the second device's switch.
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('#printBridgeDevicesPanel tr[data-device-id] a').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        var before = await StateAsync(browser, ct);
        Assert.True(before.GetProperty("focusedToggle").GetBoolean(), $"{page}: Tab did not reach a device switch ({before.GetProperty("focused")})");
        var device = before.GetProperty("focusedDevice").GetString()!;
        await browser.EvaluateAsync<bool>("(() => { window.__waslaFocused = document.activeElement; return true; })()", ct);

        // A changed snapshot: the switch of the same device gets focus back.
        await RenameDeviceAsync("Kitchen printer", "Kitchen printer 2", ct);
        await RefreshDevicesAsync(browser, ct);
        var after = await StateAsync(browser, ct);
        AssertDeviceShown(after, "Kitchen printer 2", page);
        Assert.False(after.GetProperty("sameNode").GetBoolean(), $"{page}: the table was not replaced");
        Assert.True(after.GetProperty("focusedToggle").GetBoolean() && device == after.GetProperty("focusedDevice").GetString(),
            $"{page}: focus is on {after.GetProperty("focused")} in device {after.GetProperty("focusedDevice")}, expected the switch of {device}");

        // Text selected in the table: a changed snapshot waits, and applies once the selection has ended.
        var selected = await browser.EvaluateAsync<string>("""
            (() => {
              const cell = [...document.querySelectorAll('#printBridgeDevicesPanel tr[data-device-id] .fw-semibold')].find(e => e.textContent === 'Kitchen printer 2');
              const range = document.createRange(); range.selectNodeContents(cell);
              getSelection().removeAllRanges(); getSelection().addRange(range);
              return getSelection().toString();
            })()
            """, ct);
        await RenameDeviceAsync("Kitchen printer 2", "Kitchen printer 3", ct);
        await RefreshDevicesAsync(browser, ct);
        var waiting = await StateAsync(browser, ct);
        AssertDeviceShown(waiting, "Kitchen printer 2", page);
        Assert.Equal(selected, waiting.GetProperty("selection").GetString());
        Assert.True(waiting.GetProperty("selectionConnected").GetBoolean(), $"{page}: the selection points into removed markup");

        await ClickAsync(browser, "#recent-print-activity h2", ct);
        await RefreshDevicesAsync(browser, ct);
        var applied = await StateAsync(browser, ct);
        AssertDeviceShown(applied, "Kitchen printer 3", page);
        Assert.False(applied.GetProperty("focusInDevices").GetBoolean(), $"{page}: the update moved focus into the table");
        AssertNoOverflowOrErrors(applied, page);
    }

    private async Task<HeadlessChromium> OpenDevicesPageAsync(string culture, int width, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Print Bridge checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.AddScriptBeforePageScriptsAsync(Probe, ct);
            var url = new Uri(_host.BaseAddress, "/print-bridge/devices");
            // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
            try { await started.NavigateAsync(url, ct); }
            catch (TimeoutException) { await started.NavigateAsync(url, ct); }
            if (!await started.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            Assert.True(await started.WaitForAsync("document.querySelectorAll('#printBridgeJobsPanel .pb-reprint-job-btn').length >= 2", TimeSpan.FromSeconds(10), ct),
                (_host.ServerErrors ?? "") + " The seeded reprintable print jobs are not shown.");
        });
    }

    // Real keyboard input: Tab from the Refresh button moves into the job table's first reprint button.
    private static async Task<string> TabIntoFirstReprintButtonAsync(HeadlessChromium browser, string page, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>("(() => { document.getElementById('printBridgeRefreshJobsBtn').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        var job = await browser.EvaluateAsync<string?>("""
            (() => {
              const a = document.activeElement;
              if (!a || !a.matches('#printBridgeJobsPanel .pb-reprint-job-btn')) return null;
              window.__waslaFocused = a;
              return a.getAttribute('data-job-id');
            })()
            """, ct);
        Assert.True(job is not null, $"{page}: Tab from the Refresh button did not reach a reprint button");
        // Bootstrap scrolls smoothly: wait until the focused button is in view and the page has stopped scrolling.
        Assert.True(await browser.WaitForAsync("""
            (() => {
              const r = document.activeElement.getBoundingClientRect(), y = scrollY, still = window.__waslaLastScrollY === y;
              window.__waslaLastScrollY = y;
              return still && r.top >= 0 && r.bottom <= innerHeight;
            })()
            """, TimeSpan.FromSeconds(5), ct), $"{page}: the focused reprint button did not scroll into view");
        return job!;
    }

    // Selects the first job's order code as a user dragging over it would; focus stays where it is.
    private static Task<string> SelectFirstOrderCodeAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>("""
            (() => {
              const cell = document.querySelector('#printBridgeJobsPanel tr[data-print-job-id] td');
              const range = document.createRange(); range.selectNodeContents(cell);
              getSelection().removeAllRanges(); getSelection().addRange(range);
              return getSelection().toString();
            })()
            """, ct);

    /// <summary>
    /// Waits until <paramref name="count"/> print-job reads that started after this call have been applied. The page
    /// starts a read only after the previous one was applied, so read n + count + 1 starting proves it.
    /// </summary>
    private static async Task WaitForCompletedPollsAsync(HeadlessChromium browser, int count, string page, CancellationToken ct)
    {
        var started = await browser.EvaluateAsync<int>("window.__printJobs.requests", ct);
        Assert.True(await browser.WaitForAsync($"window.__printJobs.requests >= {started + count + 1}", TimeSpan.FromSeconds(30), ct),
            $"{page}: the print-job poll did not run {count} more times");
    }

    private static async Task RefreshDevicesAsync(HeadlessChromium browser, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>("window.WaslaPrintBridge.refreshDevices().then(() => true)", ct));

    private static string HasJob(Guid id) =>
        $"!!document.querySelector('#printBridgeJobsPanel tr[data-print-job-id=\"{id}\"]')";

    private async Task<Guid> AddReprintableJobAsync(CancellationToken ct)
    {
        await using var db = await _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        var order = await db.Orders.OrderBy(o => o.ReceivedAt).Select(o => o.Id).FirstAsync(ct);
        var job = new PrintJob { OrderId = order, Type = PrintJobType.Receipt, Status = PrintJobStatus.Printed, PayloadJson = "{}", AttemptCount = 1, PrintedAt = DateTime.UtcNow };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job.Id;
    }

    private async Task SetJobStatusAsync(Guid id, PrintJobStatus status, CancellationToken ct)
    {
        await using var db = await _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        var job = await db.PrintJobs.SingleAsync(j => j.Id == id, ct);
        job.Status = status;
        await db.SaveChangesAsync(ct);
    }

    private async Task RenameDeviceAsync(string from, string to, CancellationToken ct)
    {
        using var scope = _host.Services.CreateScope();
        var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var device = await central.PrintBridgeDevices.SingleAsync(d => d.Name == from, ct);
        device.Name = to;
        await central.SaveChangesAsync(ct);
    }

    // A real mouse click at the element's center, after checking that nothing covers it.
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var target = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = document.querySelector({{JsonSerializer.Serialize(selector)}});
              e.scrollIntoView({ block: 'center', behavior: 'instant' });
              const r = e.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              return { x, y, hit: !!hit && e.contains(hit) };
            })()
            """, ct);
        Assert.True(target.GetProperty("hit").GetBoolean(), $"{selector} is covered or off screen.");
        await browser.ClickAtAsync(target.GetProperty("x").GetDouble(), target.GetProperty("y").GetDouble(), ct);
    }

    private static void AssertDeviceShown(JsonElement state, string name, string page) =>
        Assert.True(state.GetProperty("deviceNames").EnumerateArray().Any(e => e.GetString() == name),
            $"{page}: \"{name}\" is not shown; the table shows {state.GetProperty("deviceNames")}");

    private static void AssertNoOverflowOrErrors(JsonElement state, string page)
    {
        Assert.True(state.GetProperty("scrollWidth").GetDouble() <= state.GetProperty("clientWidth").GetDouble() + 1,
            $"{page}: the page scrolls sideways ({state.GetProperty("scrollWidth")} > {state.GetProperty("clientWidth")})");
        Assert.True(state.GetProperty("errors").GetArrayLength() == 0, $"{page}: page errors: {state.GetProperty("errors")}");
    }

    private static async Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>(State, ct);
}
