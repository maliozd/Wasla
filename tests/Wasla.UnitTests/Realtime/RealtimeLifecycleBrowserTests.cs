using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Notifications;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Web;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-25 in one real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views, the
/// vendored SignalR client and the hub over WebSockets). Several Live Screen pages of one signed-in user run at once:
/// the top-level page and extra same-origin pages in iframes, each a separate window with its own page-wide connection,
/// store and announcements (the driver attaches to one page, so further top-level tabs are not available to it; frames
/// get the same instrumentation). Proven: one connection per page, counted by the server; one announcement per page and
/// new order through duplicates, reconnects and a back/forward-cache cycle; closing a page or navigating away releases
/// its connection; and live updates never request notification permission or change the user's sound and notification
/// settings, in every permission state (default, denied, granted), left to right and right to left. The number of
/// pages is bounded (<see cref="ExtraPages"/>) and one browser runs at a time.
/// </summary>
public sealed class RealtimeLifecycleBrowserTests
{
    private const int ExtraPages = 3;
    private static readonly TimeSpan Push = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(20);
    private static Guid Tenant => TenantVisualWebHost.TenantId;

    // In every frame, before the page's scripts: count snapshot reads, connections built, permission requests and shown
    // notifications, and replace Notification with a stand-in in the given permission state (headless browsers have no
    // real prompt, and the stand-in lets the test see any request at all).
    private const string Instrumentation = """
        window.__liveDataReads = 0;
        window.__connectionsBuilt = 0;
        window.__permissionRequests = 0;
        window.__notificationsShown = 0;
        window.__restoredFromBfcache = 0;
        (function () {
          const fetch = window.fetch;
          window.fetch = function (input) {
            if (String(input && input.url || input).indexOf('/orders/live-data') >= 0) window.__liveDataReads++;
            return fetch.apply(this, arguments);
          };
          let client;
          Object.defineProperty(window, 'signalR', {
            configurable: true,
            get: function () { return client; },
            set: function (value) {
              client = value;
              const build = value.HubConnectionBuilder.prototype.build;
              value.HubConnectionBuilder.prototype.build = function () { window.__connectionsBuilt++; return build.apply(this, arguments); };
            }
          });
          function FakeNotification() { window.__notificationsShown++; }
          FakeNotification.permission = '__PERMISSION__';
          FakeNotification.requestPermission = function () {
            window.__permissionRequests++;
            return Promise.resolve(FakeNotification.permission);
          };
          Object.defineProperty(window, 'Notification', { configurable: true, writable: true, value: FakeNotification });
          window.addEventListener('pageshow', function (e) { if (e.persisted) window.__restoredFromBfcache++; });
        })();
        """;

    [Theory]
    [InlineData("en-US", "ltr", "default")]
    [InlineData("ar-SA", "rtl", "denied")]
    [InlineData("en-US", "ltr", "granted")]
    public async Task SeveralPagesOfOneUser_EachHoldOneConnection_AnnounceOnce_ReleaseTheirConnection_AndNeverTouchNotificationPreferences(
        string culture, string direction, string permission)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        using var metrics = new RealtimeMetricsRecorder(host.Services);
        var settingsBefore = await ServerSettingsAsync(host, ct);
        await using var browser = await host.BrowserAsync(culture, 1400, 1000, mobile: false, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser checks.");
        await browser.AddScriptBeforePageScriptsAsync(Instrumentation.Replace("__PERMISSION__", permission, StringComparison.Ordinal), ct);
        await OpenLiveScreenAsync(browser, host, ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));

        // The top-level page plus extra pages of the same user, each connecting on its own.
        Assert.True(await browser.EvaluateAsync<bool>($$"""
            (() => {
              for (let i = 0; i < {{ExtraPages}}; i++) {
                const frame = document.createElement('iframe');
                frame.setAttribute('data-test-page', String(i));
                frame.style.cssText = 'width: 1100px; height: 700px; border: 0;';
                frame.src = '/orders/live-display';
                document.body.appendChild(frame);
              }
              return true;
            })()
            """, ct));
        Assert.True(await browser.WaitForAsync(AllPages("w.WaslaRealtime && w.WaslaRealtime.get() && w.WaslaRealtime.get().state() === 'connected' && w.document.querySelectorAll('#ordersLiveScreenHost [data-order-id]').length >= 3"), Connect, ct),
            $"The extra pages did not connect. {host.ServerErrors}");
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == ExtraPages + 1, Connect, ct), metrics.Describe());
        Assert.True(await browser.EvaluateAsync<bool>(AllPages("w.document.documentElement.getAttribute('dir') === '" + direction + "' && w.__connectionsBuilt === 1"), ct));
        Assert.True(await browser.EvaluateAsync<bool>(AllPages(CountAnnouncements), ct));
        Assert.True(await browser.EvaluateAsync<bool>(AllPages(SoundStaysLocked), ct));
        // The cards are rendered by the server and the connection starts on its own, so a page can be connected while its
        // notification settings (GET /notification-settings/current, loaded once when the page starts) are still on the
        // way. Compare preferences only from the moment every page has loaded them.
        Assert.True(await browser.WaitForAsync(AllPages(SettingsLoaded), Connect, ct),
            "Notification settings did not load on every page: " + await DescribeSettingsAsync(browser, ct));
        var before = await PreferencesAsync(browser, ct);

        // A new order: every page reads it and announces it once, however many duplicates follow.
        var first = await InsertNewOrderAsync(host, $"WAS25-{permission}-1", ct);
        await AnnounceAsync(host, first, ct);
        Assert.True(await browser.WaitForAsync(AllPages(CardShown(first)), Push, ct), $"Not every page showed the new order. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync(AllPages("w.__announcements === 1"), Push, ct), await DescribePagesAsync(browser, ct));
        for (var i = 0; i < 5; i++)
            await AnnounceAsync(host, first, ct);

        // Every page drops and reconnects (stop/start, as after a network drop), the top-level page also goes through a
        // back/forward-cache cycle; then a second order.
        Assert.True(await browser.EvaluateAsync<bool>(AllPages("(w.WaslaRealtime.get().stop(), true)"), ct));
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 0, Connect, ct), metrics.Describe());
        Assert.True(await browser.EvaluateAsync<bool>(AllPages("(w.WaslaRealtime.get().start(), true)"), ct));
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })), true)", ct);
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync(AllPages("w.WaslaRealtime.get().state() === 'connected'"), Connect, ct));
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == ExtraPages + 1, Connect, ct), metrics.Describe());
        var second = await InsertNewOrderAsync(host, $"WAS25-{permission}-2", ct);
        await AnnounceAsync(host, second, ct);
        Assert.True(await browser.WaitForAsync(AllPages(CardShown(second)), Push, ct), $"Not every page showed the second order. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync(AllPages("w.__announcements === 2"), Push, ct), await DescribePagesAsync(browser, ct));
        Assert.True(await browser.EvaluateAsync<bool>(AllPages("w.__connectionsBuilt === 1 && w.WaslaRealtime.get().stats().reconnects >= 1"), ct));

        // Nothing changed the user's choices or asked for permission; a notification appeared only where the browser
        // already granted it.
        Assert.Equal(before, await PreferencesAsync(browser, ct));
        Assert.True(await browser.EvaluateAsync<bool>(AllPages(SoundStaysLocked), ct));
        Assert.Equal(0, await browser.EvaluateAsync<int>(SumPages("w.__permissionRequests"), ct));
        var shown = await browser.EvaluateAsync<int>(SumPages("w.__notificationsShown"), ct);
        if (permission != "granted")
            Assert.Equal(0, shown);
        else
            Assert.InRange(shown, 0, 2 * (ExtraPages + 1));

        // Closing pages releases their connections; announcements already made are not repeated.
        for (var remaining = ExtraPages; remaining > 0; remaining--)
        {
            Assert.True(await browser.EvaluateAsync<bool>("(document.querySelector('iframe[data-test-page]').remove(), true)", ct));
            var expected = remaining;
            Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == expected, Connect, ct), $"after closing a page: {metrics.Describe()}");
        }

        // Navigating away (to Orders, which opens no live connection) releases the last one; going back restores one.
        await browser.NavigateAsync(new Uri(host.BaseAddress, "/orders"), ct);
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 0, Connect, ct), metrics.Describe());
        await browser.EvaluateAsync<bool>("(history.back(), true)", ct);
        Assert.True(await WaitAcrossNavigationAsync(browser,
            "location.pathname === '/orders/live-display' && !!window.WaslaRealtime && !!window.WaslaRealtime.get() && window.WaslaRealtime.get().state() === 'connected'", ct),
            $"The Live Screen did not come back connected. {host.ServerErrors}");
        Assert.True(await metrics.WaitForAsync(m => m.ActiveConnections == 1, Connect, ct), metrics.Describe());
        var restored = await browser.EvaluateAsync<int>("window.__restoredFromBfcache", ct) > 0;
        if (!restored)
            Assert.True(await browser.EvaluateAsync<bool>(AllPages(CountAnnouncements), ct));
        var third = await InsertNewOrderAsync(host, $"WAS25-{permission}-3", ct);
        await AnnounceAsync(host, third, ct);
        Assert.True(await browser.WaitForAsync(CardShown(third).Replace("w.", "window.", StringComparison.Ordinal), Push, ct));
        Assert.True(await browser.WaitForAsync($"window.__announcements === {(restored ? 3 : 1)}", Push, ct), await DescribePagesAsync(browser, ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__permissionRequests", ct));
        Assert.Equal(permission, await browser.EvaluateAsync<string>("Notification.permission", ct));

        Assert.Equal(settingsBefore, await ServerSettingsAsync(host, ct));
        Assert.Equal(0, metrics.Sum("wasla.realtime.connection.closures", ("wasla.realtime.reason", "error")));
        Assert.Null(host.ServerErrors);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{culture}/{permission}: pages={ExtraPages + 1}, back/forward cache used on return={restored}, notifications shown={shown}, " +
            $"admitted={metrics.Sum("wasla.realtime.connection.attempts", ("wasla.realtime.result", "admitted"))}, " +
            $"reconnects declared={metrics.Sum("wasla.realtime.connection.attempts", ("wasla.realtime.attempt", "reconnect"))}");
    }

    // Counts the Live Screen's new-order announcements of one window (sound and notification run together in onNotify).
    private const string CountAnnouncements =
        "(() => { const audio = w.WaslaOrders.audio; w.__announcements = 0; const show = audio.showBrowserNotificationIfAllowed; " +
        "audio.showBrowserNotificationIfAllowed = function () { w.__announcements++; return show && show.apply(this, arguments); }; return true; })()";

    /// <summary>A boolean expression over <c>w</c>, true when it holds for the top-level page and every extra page.</summary>
    private static string AllPages(string predicate) =>
        "(() => { const pages = [window, ...[...document.querySelectorAll('iframe[data-test-page]')].map(f => f.contentWindow)]; " +
        $"return pages.every(w => {{ try {{ return !!w && !!({predicate}); }} catch (e) {{ return false; }} }}); }})()";

    private static string SumPages(string value) =>
        "(() => { const pages = [window, ...[...document.querySelectorAll('iframe[data-test-page]')].map(f => f.contentWindow)]; " +
        $"return pages.reduce((sum, w) => sum + ({value}), 0); }})()";

    private static string CardShown(Guid orderId) => $"!!w.document.querySelector('#ordersLiveScreenHost [data-order-id=\"{orderId}\"]')";

    /// <summary>
    /// Everything the user chose that a live update must not change: the stored browser preferences of this origin, each
    /// page's loaded notification settings, whether sound was unlocked by a gesture, and the permission each page sees.
    /// Not included: the audio state the page detects when the chosen sound is played for a new order (the headless
    /// browser blocks autoplay, so it becomes "blocked"); that is the browser's answer, not a preference, and is the same
    /// with polling. Sound is never unlocked without the user (<see cref="SoundStaysLocked"/>).
    /// </summary>
    private static Task<string> PreferencesAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>(
            "(() => { const pages = [window, ...[...document.querySelectorAll('iframe[data-test-page]')].map(f => f.contentWindow)]; " +
            "const storage = Object.keys(localStorage).sort().map(k => k + '=' + localStorage.getItem(k)); " +
            "return JSON.stringify({ storage, pages: pages.map(w => ({ settings: w.WaslaOrders.state.notificationSettings, " +
            "unlocked: w.WaslaOrders.audio.isSoundUnlocked(), permission: w.Notification.permission })) }); })()", ct);

    /// <summary>
    /// The page has loaded the user's notification settings: <c>loadNotificationSettings</c> replaces the initial null with
    /// the server's values, merged with defaults, so the fields the Live Screen uses for sound and notifications exist.
    /// </summary>
    private const string SettingsLoaded =
        "(() => { const s = w.WaslaOrders && w.WaslaOrders.state && w.WaslaOrders.state.notificationSettings; " +
        "return !!s && typeof s === 'object' && typeof s.newOrderSoundEnabled === 'boolean' && typeof s.showBrowserNotification === 'boolean' " +
        "&& typeof s.newOrderSoundName === 'string'; })()";

    /// <summary>For a timeout: each page, where it is and whether its notification settings are loaded.</summary>
    private static Task<string> DescribeSettingsAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>(
            "(() => { const pages = [window, ...[...document.querySelectorAll('iframe[data-test-page]')].map(f => f.contentWindow)]; " +
            "return JSON.stringify(pages.map((w, i) => { try { const s = w.WaslaOrders && w.WaslaOrders.state ? w.WaslaOrders.state.notificationSettings : undefined; " +
            "return { page: i === 0 ? 'top' : 'frame ' + (i - 1), path: w.location.pathname, readyState: w.document.readyState, " +
            "settings: s === undefined ? 'no WaslaOrders.state' : s === null ? 'null (not loaded)' : 'loaded', " +
            "realtime: w.WaslaRealtime && w.WaslaRealtime.get() ? w.WaslaRealtime.get().state() : 'none' }; } " +
            "catch (e) { return { page: i, error: String(e) }; } })); })()", ct);

    /// <summary>No page has sound unlocked or audio "allowed": only a user gesture does that, never a live update.</summary>
    private const string SoundStaysLocked = "!w.WaslaOrders.audio.isSoundUnlocked() && w.WaslaOrders.audio.getAudioState() !== 'allowed'";

    private static Task<string> DescribePagesAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>(
            "(() => { const pages = [window, ...[...document.querySelectorAll('iframe[data-test-page]')].map(f => f.contentWindow)]; " +
            "return JSON.stringify(pages.map(w => ({ announcements: w.__announcements, reads: w.__liveDataReads, built: w.__connectionsBuilt, " +
            "state: w.WaslaRealtime && w.WaslaRealtime.get() && w.WaslaRealtime.get().state(), live: w.WaslaOrders.liveInvalidation && w.WaslaOrders.liveInvalidation.stats() }))); })()", ct);

    private static async Task<string> ServerSettingsAsync(TenantVisualWebHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IUserNotificationSettingsService>()
            .GetAsync(Tenant, TenantVisualWebHost.OwnerId, ct);
        return JsonSerializer.Serialize(settings);
    }

    private static async Task OpenLiveScreenAsync(HeadlessChromium browser, TenantVisualWebHost host, CancellationToken ct)
    {
        try
        {
            await browser.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
        Assert.True(await browser.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-id]').length >= 3", TimeSpan.FromSeconds(15), ct),
            $"The Live Screen did not render the seeded orders. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("!!window.WaslaRealtime && !!window.WaslaRealtime.get() && window.WaslaRealtime.get().state() === 'connected'", Connect, ct),
            $"The Live Screen did not connect to the live hub. {host.ServerErrors}");
    }

    /// <summary>Like WaitForAsync, but tolerates the moments of a navigation when the page cannot be evaluated.</summary>
    private static async Task<bool> WaitAcrossNavigationAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Connect;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await browser.EvaluateAsync<bool>(expression, ct))
                    return true;
            }
            catch (InvalidOperationException)
            {
                // The document is being replaced.
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    private static async Task<Guid> InsertNewOrderAsync(TenantVisualWebHost host, string externalId, CancellationToken ct)
    {
        var order = PrintServices.Order(Guid.NewGuid(), Tenant, externalId);
        order.InternalStatus = OrderStatus.New;
        order.PlatformStatus = "Created";
        order.ReceivedAt = DateTime.UtcNow;
        order.CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-1);
        order.ChangeVersion = 1;
        await using var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.Id;
    }

    private static async Task AnnounceAsync(TenantVisualWebHost host, Guid orderId, CancellationToken ct)
    {
        long version;
        await using (var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct))
            version = db.Orders.Where(o => o.Id == orderId).Select(o => o.ChangeVersion).Single();
        await host.Services.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(Tenant, new OrderChanged(orderId, version), ct);
    }
}
