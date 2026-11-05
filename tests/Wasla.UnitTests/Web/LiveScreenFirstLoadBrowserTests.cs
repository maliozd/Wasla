using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-129 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/>. A Live Screen whose first
/// snapshot failed stayed on "Loading…" for good and its header claimed it was "showing the last update"; a new order's
/// List row said "New" twice; the Focus queue said "+1 items"; and refresh failures showed "HTTP 500" and "Check the
/// console" to operators. The failed first load must leave Loading for a localized, keyboard-usable recovery that a
/// later successful read replaces with the normal screen; each badge must be shown once; the product summary must read
/// correctly in every language; practice orders must stay out of every count in Board, List and Focus; and failures must
/// show operator copy only. Every culture, right to left and phone widths. Skips only when no Chromium browser is
/// installed or Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenFirstLoadBrowserTests : IClassFixture<LiveScreenFirstLoadBrowserTests.LiveHost>
{
    private const string ViewModeKey = "Wasla.liveScreen.viewMode";
    private readonly LiveHost _fixture;

    public LiveScreenFirstLoadBrowserTests(LiveHost fixture) => _fixture = fixture;

    // Answers the page's snapshot reads with a failure while __liveData.failures is above 0 (a server error, or a
    // signed-out session), and counts them. Everything else passes through.
    private static string SnapshotProbe(string failures, string kind) => $$"""
        (() => {
          const t = window.__liveData = { requests: 0, failures: {{failures}}, kind: '{{kind}}' };
          const original = window.fetch;
          window.fetch = function (input) {
            const url = String((input && input.url) || input);
            if (url.indexOf('/orders/live-data') < 0) return original.apply(this, arguments);
            t.requests++;
            if (t.failures > 0) {
              t.failures--;
              return Promise.resolve(t.kind === 'session'
                ? new Response('', { status: 401 })
                : new Response('{"title":"Internal Server Error","status":500}', { status: 500, headers: { 'Content-Type': 'application/problem+json' } }));
            }
            return original.apply(this, arguments);
          };
        })();
        """;

    private static string ViewMode(string view) =>
        $"(() => {{ try {{ localStorage.setItem('{ViewModeKey}', '{view}'); }} catch (e) {{ }} }})();";

    private const string LoadErrorState = """
        (() => {
          const host = document.getElementById('ordersLiveScreenHost');
          const states = host.querySelectorAll('[data-live-load-error]');
          const state = states[0];
          const box = state ? state.getBoundingClientRect() : null;
          const action = state ? state.querySelector('[data-live-retry], [data-live-reload]') : null;
          const actionBox = action ? action.getBoundingClientRect() : null;
          const connection = document.getElementById('ordersLiveDisplayConnection');
          return {
            count: states.length,
            loading: !!host.querySelector('[data-live-loading]'),
            role: state ? state.getAttribute('role') : null,
            title: state ? state.querySelector('.wasla-live-empty__title').textContent.trim() : null,
            text: state ? state.querySelector('.wasla-live-empty__text').textContent.trim() : null,
            actionTag: action ? action.tagName : null,
            actionText: action ? action.textContent.trim() : null,
            actionHref: action && action.tagName === 'A' ? action.href : null,
            retry: !!(state && state.querySelector('[data-live-retry]')),
            inside: !!box && box.width > 0 && box.left >= -1 && box.right <= document.documentElement.clientWidth + 1,
            actionInside: !!actionBox && actionBox.width > 0 && actionBox.left >= -1 && actionBox.right <= document.documentElement.clientWidth + 1,
            headerStale: !connection.hidden,
            active: document.getElementById('ordersLiveDisplayActiveCount').textContent.trim(),
            cancelled: document.getElementById('ordersLiveDisplayCancelledCount').textContent.trim(),
            dir: document.documentElement.getAttribute('dir'),
            overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
          };
        })()
        """;

    private const string ScreenRendered =
        "!!document.querySelector('#ordersLiveScreenHost .wasla-live-board, #ordersLiveScreenHost .wasla-live-groups, #ordersLiveScreenHost .wasla-live-focus')";

    [Theory]
    [InlineData("tr-TR", "ltr", 1280)]
    [InlineData("tr-TR", "ltr", 390)]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("en-US", "ltr", 390)]
    [InlineData("ar-SA", "rtl", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    [InlineData("ru-RU", "ltr", 1280)]
    [InlineData("ru-RU", "ltr", 390)]
    public async Task FirstSnapshotFails_ShowsLocalizedRecovery_AndTryAgainByKeyboardRestoresTheScreen(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, width, "/orders/live-display", ct, SnapshotProbe("Infinity", "error"));

        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveScreenHost [data-live-load-error]')", TimeSpan.FromSeconds(15), ct),
            $"{culture} {width}px: the Live Screen stayed on Loading after its first snapshot failed. {_fixture.Host.ServerErrors}");
        var state = await browser.EvaluateAsync<JsonElement>(LoadErrorState, ct);
        Assert.False(state.GetProperty("loading").GetBoolean(), "Loading is replaced, not left beside the error");
        Assert.Equal(Resource(culture, "Orders.LiveScreen.LoadFailed"), state.GetProperty("title").GetString());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.LoadFailedDescription"), state.GetProperty("text").GetString());
        Assert.Equal("BUTTON", state.GetProperty("actionTag").GetString());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.DetailRetry"), state.GetProperty("actionText").GetString());
        Assert.Equal("alert", state.GetProperty("role").GetString());
        // There is no earlier update to show, so the header does not claim one.
        Assert.False(state.GetProperty("headerStale").GetBoolean(), "the header says it shows the last update, but nothing was ever shown");
        // Nothing was read, so the header does not claim "0 active orders": the counts stay unknown.
        Assert.Equal("…", state.GetProperty("active").GetString());
        Assert.Equal("…", state.GetProperty("cancelled").GetString());
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        Assert.True(state.GetProperty("inside").GetBoolean(), $"{culture} {width}px: the error state leaves the screen");
        Assert.True(state.GetProperty("actionInside").GetBoolean(), $"{culture} {width}px: Try again leaves the screen");
        Assert.False(state.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: the page overflows horizontally");

        // The browser's accessibility tree: an alert, and a button named by its words.
        var alert = Assert.Single(await browser.AccessibleNodesAsync("#ordersLiveScreenHost [data-live-load-error]", ct));
        Assert.Equal("alert", alert.Role);
        var retryNode = Assert.Single(await browser.AccessibleNodesAsync("#ordersLiveScreenHost [data-live-retry]", ct));
        Assert.Equal("button", retryNode.Role);
        Assert.Equal(Resource(culture, "Orders.LiveScreen.DetailRetry"), retryNode.Name);

        // It keeps trying by itself (2 s backoff first) and stays one state: no second alert, no Loading again.
        Assert.True(await browser.WaitForAsync("window.__liveData.requests >= 2", TimeSpan.FromSeconds(10), ct), "no automatic retry after the failure");
        var again = await browser.EvaluateAsync<JsonElement>(LoadErrorState, ct);
        Assert.Equal(1, again.GetProperty("count").GetInt32());
        Assert.False(again.GetProperty("loading").GetBoolean());

        // Keyboard only: Tab from the orders region reaches Try again, with a visible focus ring.
        await browser.EvaluateAsync<bool>("(() => { document.getElementById('ordersLiveScreenHost').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Tab", false, ct);
        Assert.True(await browser.EvaluateAsync<bool>(
                "!!document.activeElement && document.activeElement.hasAttribute('data-live-retry') && document.activeElement.matches(':focus-visible')", ct),
            $"{culture} {width}px: Tab did not reach Try again with a visible focus ring");

        // The server answers again; Enter reads at once and the normal screen replaces the error.
        var before = await browser.EvaluateAsync<int>("(() => { window.__liveData.failures = 0; return window.__liveData.requests; })()", ct);
        await browser.PressKeyAsync("Enter", false, ct);
        Assert.True(await browser.WaitForAsync(ScreenRendered + " && !document.querySelector('[data-live-load-error]')", TimeSpan.FromSeconds(10), ct),
            $"{culture} {width}px: Try again did not restore the Live Screen. {_fixture.Host.ServerErrors}");
        Assert.True(await browser.EvaluateAsync<int>("window.__liveData.requests", ct) > before, "Try again read the snapshot");
        var after = await browser.EvaluateAsync<JsonElement>("""
            (() => ({
              focusOnHost: document.activeElement === document.getElementById('ordersLiveScreenHost'),
              headerStale: !document.getElementById('ordersLiveDisplayConnection').hidden,
              detailOrder: !!document.querySelector('#ordersLiveScreenHost [data-order-id="81000000-0000-4000-8000-0000000000a1"]'),
              active: document.getElementById('ordersLiveDisplayActiveCount').textContent
            }))()
            """, ct);
        Assert.True(after.GetProperty("focusOnHost").GetBoolean(), "focus moved from the removed Try again button to the orders region, not to the page");
        Assert.False(after.GetProperty("headerStale").GetBoolean());
        Assert.True(after.GetProperty("detailOrder").GetBoolean(), "the seeded orders are shown");
        var active = await ActiveOrdersAsync(ct);
        Assert.Equal(await browser.EvaluateAsync<string>($"new Intl.NumberFormat(document.documentElement.lang).format({active})", ct),
            after.GetProperty("active").GetString());
    }

    [Fact]
    public async Task FirstSnapshotFails_ThenTheServerAnswers_RecoversWithoutAClick()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync("en-US", 1280, "/orders/live-display", ct, SnapshotProbe("1", "error"));
        Assert.True(await browser.WaitForAsync("!!document.querySelector('[data-live-load-error]') || " + ScreenRendered, TimeSpan.FromSeconds(15), ct));
        Assert.True(await browser.WaitForAsync(ScreenRendered + " && !document.querySelector('[data-live-load-error]')", TimeSpan.FromSeconds(15), ct),
            $"the Live Screen did not recover by itself after the server answered again. {_fixture.Host.ServerErrors}");
        var page = await browser.EvaluateAsync<JsonElement>("""
            (() => ({
              requests: window.__liveData.requests,
              loading: !!document.querySelector('[data-live-loading]'),
              headerStale: !document.getElementById('ordersLiveDisplayConnection').hidden
            }))()
            """, ct);
        Assert.True(page.GetProperty("requests").GetInt32() >= 2);
        Assert.False(page.GetProperty("loading").GetBoolean());
        Assert.False(page.GetProperty("headerStale").GetBoolean());
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task FirstSnapshotFindsTheSessionExpired_OffersAReloadLink(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, width, "/orders/live-display", ct, SnapshotProbe("Infinity", "session"));
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveScreenHost [data-live-load-error]')", TimeSpan.FromSeconds(15), ct),
            $"{culture}: the Live Screen stayed on Loading after the session expired.");
        var state = await browser.EvaluateAsync<JsonElement>(LoadErrorState, ct);
        Assert.False(state.GetProperty("loading").GetBoolean());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.LoadFailed"), state.GetProperty("title").GetString());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.SessionExpired"), state.GetProperty("text").GetString());
        // Trying again cannot help a signed-out page: a real link reloads it (and so leads to sign-in).
        Assert.False(state.GetProperty("retry").GetBoolean());
        Assert.Equal("A", state.GetProperty("actionTag").GetString());
        Assert.Equal(Resource(culture, "Orders.LiveScreen.ReloadPage"), state.GetProperty("actionText").GetString());
        Assert.Equal(new Uri(_fixture.Host.BaseAddress, "/orders/live-display").ToString(), state.GetProperty("actionHref").GetString());
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        Assert.True(state.GetProperty("actionInside").GetBoolean());
        Assert.False(state.GetProperty("overflow").GetBoolean());
        // A signed-out page stops reading.
        await Task.Delay(2500, ct);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__liveData.requests", ct));
    }

    [Theory]
    [InlineData("tr-TR", 1280)]
    [InlineData("en-US", 1280)]
    [InlineData("ar-SA", 390)]
    [InlineData("ru-RU", 390)]
    public async Task NewOrder_ShowsOneNewBadge_InListBoardAndFocus(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, width, "/orders/live-display", ct, SnapshotProbe("0", "error"), ViewMode("list"));
        Assert.True(await browser.WaitForAsync(ScreenRendered, TimeSpan.FromSeconds(15), ct), $"the Live Screen did not render. {_fixture.Host.ServerErrors}");

        var orderId = await AddNewOrderAsync(ct);
        try
        {
            await browser.EvaluateAsync<bool>("WaslaOrders.liveStore.requestRefresh().then(() => true)", ct);
            // The arrival is announced on this page: its row is highlighted and its arrival badge is switched on.
            Assert.True(await browser.WaitForAsync(
                    $"(() => {{ const row = document.querySelector('#ordersLiveScreenHost [data-order-id=\"{orderId}\"]'); return !!row && row.classList.contains('order-row-new'); }})()",
                    TimeSpan.FromSeconds(15), ct),
                $"{culture}: the new order did not arrive as new on the List. {_fixture.Host.ServerErrors}");

            var newLabel = Resource(culture, "OrderStatus.New");
            Assert.Equal(newLabel, Resource(culture, "Orders.StatusNewBadge"));
            foreach (var view in new[] { "list", "board", "focus" })
            {
                await browser.EvaluateAsync<bool>($"(() => {{ WaslaOrders.liveView.setView('{view}', {{ animate: false, persist: false }}); return true; }})()", ct);
                var badges = await browser.EvaluateAsync<string[]>($$"""
                    (() => {
                      const entry = document.querySelector('#ordersLiveScreenHost [data-order-id="{{orderId}}"]');
                      if (!entry) return ['(missing)'];
                      return [...entry.querySelectorAll('[data-status-badge], [data-new-badge]')]
                        .filter(b => b.checkVisibility() && b.getBoundingClientRect().width > 0)
                        .map(b => b.textContent.trim());
                    })()
                    """, ct);
                Assert.True(badges.Count(text => text == newLabel) == 1,
                    $"{culture} {view}: the new order shows [{string.Join(", ", badges)}]; expected one '{newLabel}'");
            }
        }
        finally
        {
            await RemoveOrderAsync(orderId, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public async Task FocusQueue_SaysHowManyMoreProducts_InThePageLanguage(string culture)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, 1280, "/orders/live-display", ct, SnapshotProbe("0", "error"), ViewMode("focus"));
        var entry = $"#ordersLiveScreenHost [data-focus-select][data-order-id=\"{TenantVisualWebHost.DetailOrderId}\"] [data-product-summary]";
        Assert.True(await browser.WaitForAsync($"!!document.querySelector('{entry}')", TimeSpan.FromSeconds(15), ct), $"the Focus queue did not render. {_fixture.Host.ServerErrors}");
        var summary = await browser.EvaluateAsync<string>($"document.querySelector('{entry}').textContent", ct);
        var one = await browser.EvaluateAsync<string>("new Intl.NumberFormat(document.documentElement.lang).format(1)", ct);
        // Every seeded order has three products: two are named, the third is counted.
        Assert.Equal("2× Adana Kebap, 2× Ayran, " + Resource(culture, "Orders.LiveScreen.BoardItemsHidden").Replace("{0}", one, StringComparison.Ordinal), summary);
    }

    [Fact]
    public async Task PracticeOrder_IsShown_ButNeverCounted_InBoardListAndFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(tenantInSetup: true);
        await using var browser = await host.BrowserAsync("en-US", 1280, 900, mobile: false, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        await LoadAsync(browser, new Uri(host.BaseAddress, "/orders/live-display"), ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('form[action=\"/guided-setup/training/practice\"]')", TimeSpan.FromSeconds(15), ct),
            $"the order training panel offers no practice order. {host.ServerErrors}");
        await browser.EvaluateAsync<bool>("(() => { window.__beforePractice = true; document.querySelector('form[action=\"/guided-setup/training/practice\"]').requestSubmit(); return true; })()", ct);
        await browser.WaitForNavigationAsync("__beforePractice", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveScreenHost [data-wasla-demo-card], #ordersLiveScreenHost [data-wasla-demo]')", TimeSpan.FromSeconds(15), ct),
            $"the practice order is not shown. {host.ServerErrors}");

        int waiting;
        await using (var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct))
            waiting = db.Orders.Count(o => LiveScreenVisibility.ActiveStatuses.Contains(o.InternalStatus));
        var waitingText = await browser.EvaluateAsync<string>($"new Intl.NumberFormat(document.documentElement.lang).format({waiting})", ct);

        foreach (var view in new[] { "board", "list", "focus" })
        {
            await browser.EvaluateAsync<bool>($"(() => {{ WaslaOrders.liveView.setView('{view}', {{ animate: false, persist: false }}); return true; }})()", ct);
            var page = await browser.EvaluateAsync<JsonElement>("""
                (() => {
                  const host = document.getElementById('ordersLiveScreenHost');
                  const column = host.querySelector('[data-board-column="new"]');
                  return {
                    counts: [...host.querySelectorAll('[data-board-count]')].map(c => c.textContent),
                    practiceInNew: !!column && !!column.querySelector('[data-wasla-demo]'),
                    newEmptyHidden: !!column && column.querySelector('[data-board-empty]').hidden,
                    more: host.querySelectorAll('[data-live-more]').length,
                    active: document.getElementById('ordersLiveDisplayActiveCount').textContent,
                    label: document.getElementById('ordersLiveDisplayActiveLabel').textContent.trim()
                  };
                })()
                """, ct);
            // The documented rule (live-screen.md, order training): a practice order is never counted. Its card is shown
            // in its column, and the column does not say it is empty.
            Assert.All(page.GetProperty("counts").EnumerateArray(), count => Assert.Equal("0", count.GetString()));
            Assert.True(page.GetProperty("practiceInNew").GetBoolean(), $"{view}: the practice order is not in New");
            Assert.True(page.GetProperty("newEmptyHidden").GetBoolean(), $"{view}: New says it is empty beside the practice order");
            Assert.Equal(0, page.GetProperty("more").GetInt32());
            Assert.Equal(waitingText, page.GetProperty("active").GetString());
            Assert.Equal(Resource("en-US", "Orders.LiveScreen.WaitingRealOrders"), page.GetProperty("label").GetString());
        }
    }

    [Theory]
    [InlineData("tr-TR", 1280)]
    [InlineData("en-US", 1280)]
    [InlineData("ar-SA", 390)]
    [InlineData("ru-RU", 390)]
    public async Task OrdersRefreshFailures_ShowOperatorCopy_WithoutHttpStatusOrConsole(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await OpenAsync(culture, width, "/orders", ct);
        Assert.True(await browser.WaitForAsync("!!(window.WaslaOrders && WaslaOrders.table && WaslaOrders.table.refreshOrdersTable)", TimeSpan.FromSeconds(10), ct));

        string[] Expected(string key) => [Resource(culture, key)];
        foreach (var (mode, key) in new[] { ("500", "Orders.TableRefreshFailed"), ("throw", "Orders.TableRefreshException"), ("page", "Orders.TableReturnedFullPage") })
        {
            var shown = await browser.EvaluateAsync<string[]>($$"""
                (async () => {
                  const original = window.fetch;
                  window.fetch = function (input) {
                    if (String((input && input.url) || input).indexOf('/orders/table') < 0) return original.apply(this, arguments);
                    if ('{{mode}}' === 'throw') return Promise.reject(new TypeError('Failed to fetch'));
                    if ('{{mode}}' === 'page') return Promise.resolve(new Response('<!DOCTYPE html><html><body>Sign in</body></html>', { status: 200, headers: { 'Content-Type': 'text/html' } }));
                    return Promise.resolve(new Response('error', { status: 500 }));
                  };
                  document.querySelectorAll('.wasla-toast').forEach(t => t.remove());
                  try { await WaslaOrders.table.refreshOrdersTable(); } finally { window.fetch = original; }
                  return [...document.querySelectorAll('.wasla-toast__message, #ordersMessageHost .alert')].map(e => e.textContent.trim());
                })()
                """, ct);
            Assert.Equal(Expected(key), shown);
            Assert.DoesNotContain("HTTP", shown[0], StringComparison.Ordinal);
            Assert.DoesNotContain("500", shown[0], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("en-US", 1280)]
    [InlineData("ar-SA", 390)]
    public async Task NotificationSettingsFailOnLoad_ShowsOperatorCopy_AndTheScreenStillLoads(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        const string FailSettings = """
            (() => {
              const original = window.fetch;
              window.fetch = function (input) {
                if (String((input && input.url) || input).indexOf('/notification-settings/current') >= 0)
                  return Promise.resolve(new Response('error', { status: 500 }));
                return original.apply(this, arguments);
              };
            })();
            """;
        await using var browser = await OpenAsync(culture, width, "/orders/live-display", ct, FailSettings);
        Assert.True(await browser.WaitForAsync(ScreenRendered, TimeSpan.FromSeconds(15), ct), $"the Live Screen did not load. {_fixture.Host.ServerErrors}");
        var shown = await browser.EvaluateAsync<string[]>("[...document.querySelectorAll('.wasla-toast__message, #ordersMessageHost .alert')].map(e => e.textContent.trim())", ct);
        Assert.Equal([Resource(culture, "Orders.NotificationSettingsLoadFailed")], shown);
    }

    private async Task<int> ActiveOrdersAsync(CancellationToken ct)
    {
        await using var db = await _fixture.Host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        return db.Orders.Count(o => LiveScreenVisibility.ActiveStatuses.Contains(o.InternalStatus));
    }

    private async Task<Guid> AddNewOrderAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var code = "YS-" + Random.Shared.Next(100000, 999999);
        var now = DateTime.UtcNow;
        await using var db = await _fixture.Host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.Yemeksepeti,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{FoodPlatform.Yemeksepeti}:{code}",
            InternalStatus = OrderStatus.New,
            PlatformStatus = nameof(OrderStatus.New),
            CustomerName = "Test Customer",
            CustomerPhone = "5551112233",
            CustomerAddress = "Moda Cd. No: 1, Kadıköy",
            TotalAmount = 120m,
            PaymentMethod = PaymentMethod.OnlinePayment,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = now,
            ReceivedAt = now,
            RawPayloadJson = "{}",
            Items = [new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 120m, TotalPrice = 120m }]
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    private async Task RemoveOrderAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _fixture.Host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == id, ct);
        db.Orders.Remove(order);
        await db.SaveChangesAsync(ct);
    }

    private async Task<HeadlessChromium> OpenAsync(string culture, int width, string path, CancellationToken ct, params string[] scripts)
    {
        var browser = await _fixture.Host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        return await browser.SetUpAsync(async started =>
        {
            foreach (var script in scripts)
                await started.AddScriptBeforePageScriptsAsync(script, ct);
            await LoadAsync(started, new Uri(_fixture.Host.BaseAddress, path), ct);
        });
    }

    private static async Task LoadAsync(HeadlessChromium browser, Uri url, CancellationToken ct)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(url, ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(url, ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
    }

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";

    /// <summary>One live (not in Setup) tenant host for the class; each test opens its own browser.</summary>
    public sealed class LiveHost : IAsyncLifetime
    {
        internal TenantVisualWebHost Host { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Host = await TenantVisualWebHost.StartAsync();

        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }
}
