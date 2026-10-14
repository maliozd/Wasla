using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-5 in a real headless Chromium browser: the Live Screen with about 5,755 active orders (<see cref="LargeActiveOrderSeed"/>),
/// the set that kept a developer's screen on "Loading" for minutes. Before the fix the first Board render took over three
/// minutes, an unchanged refresh over two and a half, and the page froze on every snapshot. The screen must become
/// usable quickly and stay responsive, keep the header counts, render each column one page at a time with every order
/// reachable through "Show more", survive invalidation bursts and failed refreshes, and keep the Board scrollable at
/// tablet widths, stacked on phones, right to left and in dark mode. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class LiveScreenLargeSnapshotBrowserTests : IClassFixture<LiveScreenLargeSnapshotBrowserTests.LargeHost>
{
    private const int PageSize = 100;
    private const int NewColumn = LargeActiveOrderSeed.NewOrders + LargeActiveOrderSeed.AcceptedOrders + 2;

    // Counts and optionally slows down or fails the page's snapshot requests; everything else passes through.
    private const string SnapshotProbe = """
        (() => {
          const t = window.__liveData = { requests: 0, inFlight: 0, maxInFlight: 0, delayMs: 0, fail: false };
          const original = window.fetch;
          window.fetch = function (input) {
            const url = String((input && input.url) || input);
            if (url.indexOf('/orders/live-data') < 0) return original.apply(this, arguments);
            t.requests++; t.inFlight++; t.maxInFlight = Math.max(t.maxInFlight, t.inFlight);
            const args = arguments;
            const send = () => t.fail
              ? Promise.resolve(new Response('<!DOCTYPE html><html><body>error</body></html>', { status: 500, headers: { 'Content-Type': 'text/html' } }))
              : original.apply(window, args);
            const pending = t.delayMs > 0 ? new Promise(r => setTimeout(r, t.delayMs)).then(send) : send();
            return pending.then(r => { if (t.firstResponseAt == null) t.firstResponseAt = performance.now(); return r; })
              .finally(() => { t.inFlight--; });
          };
          // The first frame after the snapshot replaced "Loading".
          document.addEventListener('DOMContentLoaded', () => {
            const host = document.getElementById('ordersLiveScreenHost');
            if (!host) return;
            const watch = new MutationObserver(() => {
              if (host.querySelector('[data-live-loading]')) return;
              watch.disconnect();
              requestAnimationFrame(() => setTimeout(() => { t.usableAt = performance.now(); }, 0));
            });
            watch.observe(host, { childList: true });
          });
        })();
        """;

    // The longest stretch the main thread could not run a 50 ms timer, over the given time.
    private const string Heartbeat = """
        (ms => new Promise(done => {
          let last = performance.now(), max = 0;
          const timer = setInterval(() => { const now = performance.now(); max = Math.max(max, now - last); last = now; }, 50);
          setTimeout(() => { clearInterval(timer); done(Math.round(max)); }, ms);
        }))
        """;

    private const string Rendered = """
        ((selector) => {
          const ids = [...document.querySelectorAll('#ordersLiveScreenHost ' + selector)].map(e => e.getAttribute('data-order-id'));
          return { count: ids.length, unique: new Set(ids).size };
        })
        """;

    private readonly LargeHost _fixture;

    public LiveScreenLargeSnapshotBrowserTests(LargeHost fixture) => _fixture = fixture;

    [Theory]
    [InlineData("tr-TR", "ltr", "light", 1280)]
    [InlineData("ar-SA", "rtl", "dark", 1280)]
    public async Task LargeSnapshot_BecomesUsable_KeepsCounts_AndPagesEveryColumn(string culture, string direction, string scheme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, width, 900, scheme, ct);

        // The page stays responsive once the screen is shown (it used to freeze for minutes).
        var maxGap = await browser.EvaluateAsync<int>($"({Heartbeat})(5000)", ct);
        Assert.True(maxGap < 2000, $"the main thread was blocked for {maxGap} ms after the large snapshot was shown");

        var page = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const host = document.getElementById('ordersLiveScreenHost');
              const column = host.querySelector('[data-board-column="new"]');
              const more = column.querySelector('[data-live-more]');
              const button = more && more.querySelector('[data-live-show-more]');
              const ids = [...host.querySelectorAll('.wasla-live-screen-card')].map(c => c.getAttribute('data-order-id'));
              return {
                active: document.getElementById('ordersLiveDisplayActiveCount').textContent,
                newCount: column.querySelector('[data-board-count]').textContent,
                newRendered: column.querySelectorAll('[data-board-list] > .wasla-live-screen-card').length,
                cards: ids.length, unique: new Set(ids).size,
                status: more ? more.querySelector('[data-live-more-status]').textContent : null,
                button: button ? button.textContent : null,
                buttonTag: button ? button.tagName : null,
                describedBy: button ? document.getElementById(button.getAttribute('aria-describedby')) === more.querySelector('[data-live-more-status]') : false,
                otherMore: host.querySelectorAll('[data-live-more]').length,
                expectedStatus: null,
                dir: document.documentElement.getAttribute('dir'),
                theme: document.documentElement.getAttribute('data-bs-theme'),
                lang: document.documentElement.lang
              };
            })()
            """, ct);

        Assert.Equal(LargeActiveOrderSeed.ActiveOrders.ToString(CultureInfo.InvariantCulture), page.GetProperty("active").GetString());
        Assert.Equal(NewColumn.ToString(CultureInfo.InvariantCulture), page.GetProperty("newCount").GetString());
        Assert.Equal(PageSize, page.GetProperty("newRendered").GetInt32());
        Assert.Equal(page.GetProperty("cards").GetInt32(), page.GetProperty("unique").GetInt32());
        Assert.InRange(page.GetProperty("cards").GetInt32(), PageSize, PageSize + 10);
        Assert.Equal(1, page.GetProperty("otherMore").GetInt32());
        Assert.Equal("BUTTON", page.GetProperty("buttonTag").GetString());
        Assert.True(page.GetProperty("describedBy").GetBoolean(), "the Show more button is described by the 'showing x of y' line");
        var formatted = await browser.EvaluateAsync<JsonElement>(
            $"({{ shown: new Intl.NumberFormat(document.documentElement.lang).format({PageSize}), total: new Intl.NumberFormat(document.documentElement.lang).format({NewColumn}) }})", ct);
        Assert.Equal(Resource(culture, "Orders.LiveScreen.GroupShowing")
                .Replace("{0}", formatted.GetProperty("shown").GetString(), StringComparison.Ordinal)
                .Replace("{1}", formatted.GetProperty("total").GetString(), StringComparison.Ordinal),
            page.GetProperty("status").GetString());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.GroupShowMore").Replace("{0}", formatted.GetProperty("shown").GetString(), StringComparison.Ordinal),
            page.GetProperty("button").GetString());
        Assert.Equal(direction, page.GetProperty("dir").GetString());
        Assert.Equal(scheme, page.GetProperty("theme").GetString());

        // A real click reveals the next page; the button keeps focus and the first page is not rebuilt.
        await browser.EvaluateAsync<bool>("(() => { window.__firstCard = document.querySelector('#ordersLiveScreenHost [data-board-column=\"new\"] .wasla-live-screen-card'); document.querySelector('[data-live-show-more]').scrollIntoView({ block: 'center', inline: 'center' }); return true; })()", ct);
        var point = await browser.EvaluateAsync<JsonElement>("(() => { const r = document.querySelector('[data-live-show-more]').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()", ct);
        await browser.ClickAtAsync(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble(), ct);
        Assert.True(await browser.WaitForAsync(
            $"document.querySelectorAll('#ordersLiveScreenHost [data-board-column=\"new\"] [data-board-list] > .wasla-live-screen-card').length === {2 * PageSize}",
            TimeSpan.FromSeconds(10), ct), "Show more did not reveal the next page");
        var afterClick = await browser.EvaluateAsync<JsonElement>("""
            (() => ({
              focused: document.activeElement && document.activeElement.hasAttribute('data-live-show-more'),
              sameFirst: document.querySelector('#ordersLiveScreenHost [data-board-column="new"] .wasla-live-screen-card') === window.__firstCard
            }))()
            """, ct);
        Assert.True(afterClick.GetProperty("focused").GetBoolean(), "focus stays on Show more");
        Assert.True(afterClick.GetProperty("sameFirst").GetBoolean(), "the first page is kept, not rebuilt");

        // An unchanged snapshot replaces, moves or rebuilds no card.
        var unchanged = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              const host = document.getElementById('ordersLiveScreenHost');
              let added = 0, removed = 0;
              const watch = new MutationObserver(list => { for (const r of list) { for (const n of r.addedNodes) if (n.nodeType === 1) added++; for (const n of r.removedNodes) if (n.nodeType === 1) removed++; } });
              watch.observe(host, { childList: true, subtree: true });
              const result = await WaslaOrders.liveStore.requestRefresh();
              await new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
              watch.disconnect();
              const t = WaslaOrders.liveStore.lastTimings;
              return { applied: !!(result && result.applied), added, removed, moved: t.movedCards, changed: t.changedCards, renderMs: Math.round(t.renderMs) };
            })()
            """, ct);
        Assert.True(unchanged.GetProperty("applied").GetBoolean());
        Assert.Equal(0, unchanged.GetProperty("moved").GetInt32());
        Assert.Equal(0, unchanged.GetProperty("changed").GetInt32());
        Assert.Equal(0, unchanged.GetProperty("added").GetInt32());
        Assert.Equal(0, unchanged.GetProperty("removed").GetInt32());

        // Board → List → Focus → Board: the same orders, once each, with the revealed page kept.
        foreach (var (view, selector) in new[] { ("list", ".wasla-live-screen-card"), ("focus", "[data-focus-select]"), ("board", ".wasla-live-screen-card") })
        {
            await browser.EvaluateAsync<bool>($"(() => {{ WaslaOrders.liveView.setView('{view}', {{ animate: false, persist: false }}); return true; }})()", ct);
            var rendered = await browser.EvaluateAsync<JsonElement>($"({Rendered})('{selector}')", ct);
            Assert.Equal(rendered.GetProperty("count").GetInt32(), rendered.GetProperty("unique").GetInt32());
            Assert.Equal(2 * PageSize, await browser.EvaluateAsync<int>(
                $"document.querySelectorAll('#ordersLiveScreenHost [data-board-column=\"new\"] [data-board-list] > [data-order-id]').length", ct));
            Assert.Equal(LargeActiveOrderSeed.ActiveOrders.ToString(CultureInfo.InvariantCulture),
                await browser.EvaluateAsync<string>("document.getElementById('ordersLiveDisplayActiveCount').textContent", ct));
        }

        // The "showing x of y" line stays readable in the page's theme.
        var contrast = await browser.EvaluateAsync<double>("""
            (() => {
              const el = document.querySelector('[data-live-more-status]');
              const parse = s => {
                if (s.startsWith('color(srgb')) { const m = s.slice(10).match(/-?[\d.]+(e-?\d+)?/g).map(Number); return [m[0] * 255, m[1] * 255, m[2] * 255, m[3] ?? 1]; }
                const m = s.match(/[\d.]+/g); if (!m) return [0, 0, 0, 0];
                const n = m.map(Number); return [n[0], n[1], n[2], n[3] ?? 1];
              };
              const lum = ([r, g, b]) => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
              let bg = null;
              for (let p = el; p && !bg; p = p.parentElement) { const c = parse(getComputedStyle(p).backgroundColor); if (c[3] >= 1) bg = c; }
              bg = bg || parse(getComputedStyle(document.body).backgroundColor);
              const fg = parse(getComputedStyle(el).color);
              const a = lum(fg), b = lum(bg);
              return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
            })()
            """, ct);
        Assert.True(contrast >= 4.5, $"{culture} {scheme}: the paging line contrast is {contrast:0.00}");
    }

    [Fact]
    public async Task InvalidationBurst_DuringSlowSnapshots_ReadsOnceMore_AndShowsTheNewestSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("en-US", 1280, 900, "light", ct);
        var orderId = await browser.EvaluateAsync<string>(
            "document.querySelector('#ordersLiveScreenHost [data-board-column=\"new\"] .wasla-live-screen-card').getAttribute('data-order-id')", ct);
        await browser.EvaluateAsync<bool>("(() => { window.__liveData.requests = 0; window.__liveData.maxInFlight = 0; window.__liveData.delayMs = 1500; return true; })()", ct);

        // Bursts arrive while a slow snapshot is being read; the order changes on the server in the middle of them.
        var burst = browser.EvaluateAsync<bool>("""
            (async () => {
              for (let round = 0; round < 6; round++) {
                for (let i = 0; i < 40; i++) WaslaOrders.liveStore.requestRefresh();
                await new Promise(r => setTimeout(r, 400));
              }
              return true;
            })()
            """, ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(700, ct);
        var previous = await SetStatusAsync(orderId, OrderStatus.Preparing, ct);
        try
        {
            await burst;
            Assert.True(await browser.WaitForAsync(
                $"!!document.querySelector('#ordersLiveScreenHost [data-board-column=\"preparing\"] .wasla-live-screen-card[data-order-id=\"{orderId}\"]')",
                TimeSpan.FromSeconds(20), ct), "the screen did not converge on the newest snapshot");
            // A busy page blocks the waits above as well, so the time is checked (before WAS-5: about five minutes).
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the screen converged after {watch.Elapsed.TotalSeconds:0} s");
            // Let the trailing read finish, then stop slowing reads so a regular poll cannot be caught half-way.
            await browser.EvaluateAsync<bool>("(() => { window.__liveData.delayMs = 0; return true; })()", ct);
            Assert.True(await browser.WaitForAsync("window.__liveData.inFlight === 0", TimeSpan.FromSeconds(10), ct));
        }
        finally
        {
            // The host is shared by the class: put the order back.
            await SetStatusAsync(orderId, previous, CancellationToken.None);
        }
        var stats = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const ids = [...document.querySelectorAll('#ordersLiveScreenHost .wasla-live-screen-card')].map(c => c.getAttribute('data-order-id'));
              return { requests: window.__liveData.requests, maxInFlight: window.__liveData.maxInFlight, inFlight: window.__liveData.inFlight,
                cards: ids.length, unique: new Set(ids).size, stale: !document.getElementById('ordersLiveDisplayConnection').hidden };
            })()
            """, ct);
        Assert.Equal(1, stats.GetProperty("maxInFlight").GetInt32());
        // 240 invalidations over ~2.4 s of 1.5 s reads: each read collects everything that arrived during it (plus at most
        // one regular poll).
        Assert.InRange(stats.GetProperty("requests").GetInt32(), 2, 5);
        Assert.Equal(stats.GetProperty("cards").GetInt32(), stats.GetProperty("unique").GetInt32());
        Assert.False(stats.GetProperty("stale").GetBoolean(), "no read was given up while the page was busy");
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheLastUsableScreen_AndRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("en-US", 1280, 900, "light", ct);
        var before = await browser.EvaluateAsync<int>("document.querySelectorAll('#ordersLiveScreenHost .wasla-live-screen-card').length", ct);

        var failed = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              window.__liveData.fail = true;
              // A poll already on its way may still succeed; the next read is the failing one.
              let result = await WaslaOrders.liveStore.requestRefresh();
              if (result && result.applied) result = await WaslaOrders.liveStore.requestRefresh();
              const status = document.getElementById('ordersLiveDisplayConnection');
              return { applied: !!(result && result.applied), cards: document.querySelectorAll('#ordersLiveScreenHost .wasla-live-screen-card').length,
                active: document.getElementById('ordersLiveDisplayActiveCount').textContent, shown: !status.hidden, text: status.textContent };
            })()
            """, ct);
        Assert.False(failed.GetProperty("applied").GetBoolean());
        Assert.Equal(before, failed.GetProperty("cards").GetInt32());
        Assert.Equal(LargeActiveOrderSeed.ActiveOrders.ToString(CultureInfo.InvariantCulture), failed.GetProperty("active").GetString());
        Assert.True(failed.GetProperty("shown").GetBoolean(), "the failure is announced");
        Assert.Equal(Resource("en-US", "Orders.LiveScreen.ConnectionStale"), failed.GetProperty("text").GetString());

        await browser.EvaluateAsync<bool>("(() => { window.__liveData.fail = false; return true; })()", ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('ordersLiveDisplayConnection').hidden", TimeSpan.FromSeconds(30), ct),
            "the screen did not recover after the server answered again");
        Assert.Equal(before, await browser.EvaluateAsync<int>("document.querySelectorAll('#ordersLiveScreenHost .wasla-live-screen-card').length", ct));
    }

    [Theory]
    [InlineData("en-US", "ltr", "light")]
    [InlineData("ar-SA", "rtl", "dark")]
    public async Task Board_ScrollsHorizontallyOnScreen_At768(string culture, string direction, string scheme)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 768, 900, scheme, ct);
        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const host = document.getElementById('ordersLiveScreenHost');
              const rect = host.getBoundingClientRect();
              const before = host.scrollLeft;
              host.scrollLeft = (document.documentElement.dir === 'rtl' ? -1 : 1) * host.scrollWidth;
              const last = host.querySelector('[data-board-column="completed"]').getBoundingClientRect();
              const reached = last.left >= rect.left - 1 && last.right <= rect.right + 1;
              host.scrollLeft = before;
              return {
                dir: document.documentElement.dir,
                hostBottom: rect.bottom, innerHeight: innerHeight,
                overflows: host.scrollWidth > host.clientWidth, reached,
                pageOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                verticalInside: host.scrollHeight > host.clientHeight
              };
            })()
            """, ct);
        Assert.Equal(direction, layout.GetProperty("dir").GetString());
        Assert.True(layout.GetProperty("overflows").GetBoolean(), "five Board columns are wider than a 768 px screen");
        // The defect: the Board's horizontal scrollbar sat under the last card of the longest column, far below the screen.
        Assert.True(layout.GetProperty("hostBottom").GetDouble() <= layout.GetProperty("innerHeight").GetDouble() + 1,
            $"the Board's bottom edge (and its horizontal scrollbar) is at {layout.GetProperty("hostBottom").GetDouble()} px, below the {layout.GetProperty("innerHeight").GetDouble()} px screen");
        Assert.True(layout.GetProperty("verticalInside").GetBoolean(), "the long columns scroll inside the Board");
        Assert.True(layout.GetProperty("reached").GetBoolean(), "the last column can be scrolled into view");
        Assert.False(layout.GetProperty("pageOverflow").GetBoolean(), "the page itself does not scroll sideways");
    }

    [Theory]
    [InlineData("en-US", "ltr", "light")]
    [InlineData("ar-SA", "rtl", "dark")]
    public async Task Board_StaysStacked_At390(string culture, string direction, string scheme)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 390, 844, scheme, ct);
        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const host = document.getElementById('ordersLiveScreenHost');
              const columns = [...host.querySelectorAll('[data-board-column]')].map(c => c.getBoundingClientRect());
              const more = host.querySelector('[data-live-more]').getBoundingClientRect();
              return {
                dir: document.documentElement.dir,
                stacked: columns.every((c, i) => i === 0 || c.top >= columns[i - 1].bottom - 1) && columns.every(c => Math.abs(c.left - columns[0].left) < 1),
                hostOverflow: host.scrollWidth > host.clientWidth + 1,
                pageOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                moreInside: more.left >= 0 && more.right <= innerWidth
              };
            })()
            """, ct);
        Assert.Equal(direction, layout.GetProperty("dir").GetString());
        Assert.True(layout.GetProperty("stacked").GetBoolean(), "phone columns are stacked");
        Assert.False(layout.GetProperty("hostOverflow").GetBoolean());
        Assert.False(layout.GetProperty("pageOverflow").GetBoolean());
        Assert.True(layout.GetProperty("moreInside").GetBoolean(), "the Show more row fits the phone screen");
    }

    [Fact]
    public async Task DetailsDialog_ReturnsFocusToItsOpener_OrToTheBoardWhenTheOrderLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("en-US", 1280, 900, "light", ct);
        const string active = "(() => { const a = document.activeElement; return a ? (a.getAttribute('data-order-detail') || a.id || a.tagName) : null; })()";

        // An order from the second page of its column.
        await browser.EvaluateAsync<bool>("(() => { WaslaOrders.liveStore.showMoreInGroup('new'); return true; })()", ct);
        var orderId = await browser.EvaluateAsync<string>(
            $"document.querySelectorAll('#ordersLiveScreenHost [data-board-column=\"new\"] [data-board-list] > .wasla-live-screen-card')[{PageSize + 5}].getAttribute('data-order-id')", ct);
        await OpenDetailsAsync(browser, orderId, ct);
        await browser.PressKeyAsync("Escape", false, ct);
        Assert.True(await browser.WaitForAsync("getComputedStyle(document.getElementById('ordersLiveDetailModal')).display === 'none'", TimeSpan.FromSeconds(10), ct));
        Assert.True(await browser.WaitForAsync($"document.activeElement && document.activeElement.getAttribute('data-order-detail') === '{orderId}'", TimeSpan.FromSeconds(5), ct),
            $"focus did not return to the Details button that opened the dialog; it is on {await browser.EvaluateAsync<string>(active, ct)}");

        // The order leaves the Live Screen while its details are open: focus goes to the Board, not to the page body.
        await OpenDetailsAsync(browser, orderId, ct);
        // The next accepted snapshot no longer has the order (it was rejected or delivered elsewhere). Polls are held
        // back meanwhile, so the order cannot come back before the dialog closes.
        await browser.EvaluateAsync<bool>($$"""
            (async () => {
              const response = await fetch('/orders/live-data', { headers: { Accept: 'application/json' } });
              const snapshot = await response.json();
              window.__liveData.fail = true;
              snapshot.orders = snapshot.orders.filter(o => o.id !== '{{orderId}}');
              WaslaOrders.liveStore.renderSnapshot(snapshot);
              return true;
            })()
            """, ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>($"document.querySelectorAll('[data-order-detail=\"{orderId}\"]').length", ct));
        await browser.PressKeyAsync("Escape", false, ct);
        Assert.True(await browser.WaitForAsync("getComputedStyle(document.getElementById('ordersLiveDetailModal')).display === 'none'", TimeSpan.FromSeconds(10), ct));
        Assert.True(await browser.WaitForAsync("document.activeElement === document.getElementById('ordersLiveScreenHost')", TimeSpan.FromSeconds(5), ct),
            $"focus did not fall back to the Board; it is on {await browser.EvaluateAsync<string>(active, ct)}");
    }

    private async Task<OrderStatus> SetStatusAsync(string orderId, OrderStatus status, CancellationToken ct)
    {
        await using var db = await _fixture.Host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        var order = await db.Orders.SingleAsync(o => o.Id == Guid.Parse(orderId), ct);
        var previous = order.InternalStatus;
        order.InternalStatus = status;
        order.ChangeVersion += 1;
        await db.SaveChangesAsync(ct);
        return previous;
    }

    private static async Task OpenDetailsAsync(HeadlessChromium browser, string orderId, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>(
            $"(() => {{ const b = document.querySelector('#ordersLiveScreenHost [data-order-detail=\"{orderId}\"]'); b.scrollIntoView({{ block: 'center' }}); b.focus(); b.click(); return true; }})()", ct);
        // Bootstrap ignores Escape until the dialog's opening transition has finished and focus has moved into it.
        Assert.True(await browser.WaitForAsync(
            "document.getElementById('ordersLiveDetailModal').classList.contains('show') && document.getElementById('ordersLiveDetailModal').contains(document.activeElement) && !!document.querySelector('#ordersLiveDetailModalBody .wasla-live-detail')",
            TimeSpan.FromSeconds(10), ct), "the details dialog did not open");
        await Task.Delay(400, ct);
    }

    private async Task<HeadlessChromium> OpenAsync(string culture, int width, int height, string scheme, CancellationToken ct)
    {
        var browser = await _fixture.Host.BrowserAsync(culture, width, height, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(scheme, ct);
            await started.AddScriptBeforePageScriptsAsync(SnapshotProbe, ct);
            var url = new Uri(_fixture.Host.BaseAddress, "/orders/live-display");
            try
            {
                await started.NavigateAsync(url, ct);
            }
            catch (TimeoutException)
            {
                await started.NavigateAsync(url, ct);
            }

            if (!await started.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            Assert.True(await started.WaitForAsync("window.__liveData.usableAt != null", TimeSpan.FromSeconds(30), ct),
                $"The Live Screen stayed on Loading with a large snapshot. {_fixture.Host.ServerErrors}");
            // Measured in the page, from the first snapshot response to the first frame after it replaced Loading. A
            // blocked page also blocks the wait above, so the time itself is checked. Before WAS-5: over three minutes.
            var usableMs = await started.EvaluateAsync<double>("window.__liveData.usableAt - window.__liveData.firstResponseAt", ct);
            Assert.True(usableMs < 10000, $"the Live Screen became usable {usableMs:0} ms after the large snapshot arrived");
        });
    }

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";

    /// <summary>One tenant host with the large active-order set for the whole class; each test opens its own browser.</summary>
    public sealed class LargeHost : IAsyncLifetime
    {
        internal TenantVisualWebHost Host { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Host = await TenantVisualWebHost.StartAsync(seed: LargeActiveOrderSeed.Seed);

        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }
}
