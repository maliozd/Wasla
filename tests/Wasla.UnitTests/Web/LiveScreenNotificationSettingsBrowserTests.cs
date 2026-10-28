using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Notifications;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-13 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views and
/// scripts). With real mouse clicks, the Live Screen settings menu's notification settings entry and the order-training
/// step button open the one shared notification settings modal; closing it returns focus to the control the user
/// started from (the settings button for the menu entry). Opening, reading and closing the modal never ask for browser
/// notification permission and never change the user's saved preferences. A stand-in for <c>Notification</c> counts
/// every permission request, because a headless browser has no real prompt.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenNotificationSettingsBrowserTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string Instrumentation = """
        window.__permissionRequests = 0;
        window.__modalShown = 0;
        window.__modalHidden = 0;
        // Snapshots the Live Screen has rendered. The first one fills the settings menu's automation status and the
        // training panel's real-order line and practice-order button, which moves the controls below them.
        window.__liveRendered = 0;
        document.addEventListener('wasla:live-rendered', function () { window.__liveRendered++; });
        // What each real click landed on, so a click that missed its target says so.
        window.__clicked = null;
        document.addEventListener('click', function (e) { window.__clicked = e.target; }, true);
        // The form a click submitted: the submit event fires while the click is dispatched, before the page leaves.
        window.__submitted = null;
        document.addEventListener('submit', function (e) { window.__submitted = e.target; }, true);
        // Bootstrap ignores a close while the modal still fades in; the test acts once it is fully shown or hidden.
        document.addEventListener('shown.bs.modal', function (e) { if (e.target.id === 'notificationSettingsModal') window.__modalShown++; });
        document.addEventListener('hidden.bs.modal', function (e) { if (e.target.id === 'notificationSettingsModal') window.__modalHidden++; });
        (function () {
          function FakeNotification() { }
          FakeNotification.permission = 'default';
          FakeNotification.requestPermission = function () {
            window.__permissionRequests++;
            return Promise.resolve(FakeNotification.permission);
          };
          Object.defineProperty(window, 'Notification', { configurable: true, writable: true, value: FakeNotification });
        })();
        """;

    /// <summary>
    /// The page has rendered its first Live Screen snapshot (its own <c>wasla:live-rendered</c> event). Until then the
    /// controls the test clicks can still move.
    /// </summary>
    private const string SnapshotRendered = "window.__liveRendered >= 1";

    private static string ModalOpen(int times) =>
        "(() => { const m = document.getElementById('notificationSettingsModal'); return !!m && m.classList.contains('show') " +
        $"&& getComputedStyle(m).display === 'block' && !!m.querySelector('#ShowBrowserNotification') && window.__modalShown === {times}; }})()";

    private static string ModalClosed(int times) =>
        "(() => { const m = document.getElementById('notificationSettingsModal'); return !!m && getComputedStyle(m).display === 'none' " +
        $"&& !document.body.classList.contains('modal-open') && window.__modalHidden === {times}; }})()";

    [Theory]
    [InlineData("tr-TR", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task SettingsMenuAndTrainingStep_OpenTheSharedModal_ReturnFocus_AndNeverAskForPermission(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        // A tenant in Setup: the Owner is in order training on the Live Screen, before the practice order.
        await using var host = await TenantVisualWebHost.StartAsync(tenantInSetup: true);
        var settingsBefore = await ServerSettingsAsync(host, ct);
        await using var browser = await OpenLiveScreenAsync(host, culture, ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));

        // 1. The settings menu entry, opened from the settings button.
        await ClickAsync(browser, "#ordersLiveDisplaySettingsToggle", ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('ordersLiveDisplaySettingsMenu').classList.contains('show')", Wait, ct),
            "The Live Screen settings menu did not open.");
        // The compact action on the Notifications heading row shows "Settings" and is named with the heading.
        var label = await browser.EvaluateAsync<string>("document.getElementById('ordersLiveDisplayNotificationSettings').textContent.trim()", ct);
        if (culture == "tr-TR")
            Assert.Equal("Ayarlar", label);
        await ClickAsync(browser, "#ordersLiveDisplayNotificationSettings", ct);
        Assert.True(await browser.WaitForAsync(ModalOpen(1), Wait, ct), $"The settings menu entry did not open the notification settings modal. {host.ServerErrors}");
        Assert.Equal(1, await browser.EvaluateAsync<int>("document.querySelectorAll('#notificationSettingsModal').length", ct));
        Assert.False(string.IsNullOrWhiteSpace(await browser.EvaluateAsync<string>(
            "document.getElementById('browserNotificationPermissionHelp').textContent", ct)), "The modal does not state the browser permission.");
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__permissionRequests", ct));

        await ClickAsync(browser, "#notificationSettingsModal .btn-close", ct);
        Assert.True(await browser.WaitForAsync(ModalClosed(1), Wait, ct), "The notification settings modal did not close.");
        Assert.True(await browser.WaitForAsync("document.activeElement && document.activeElement.id === 'ordersLiveDisplaySettingsToggle'", Wait, ct),
            $"Focus did not return to the settings button; it is on {await ActiveElementAsync(browser, ct)}.");
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__permissionRequests", ct));

        // 2. The training step: start the practice order with the panel's own button, then open the modal from the note
        // shown when it arrives. Starting posts a form and the Live Screen loads again. The practice order exists before
        // that page arrives, so a live update can already show the note on the page being left; the marker makes the test
        // act only on the new page, never on one that is about to be replaced.
        await browser.EvaluateAsync<bool>("(window.__leftForPractice = true)", ct);
        await ClickAsync(browser, "[data-guided-training] [data-training-when='intro'] button[type=submit]", ct, navigates: true);
        // A form post and a full page load: the driver's navigation budget, then the server-rendered step on the new page.
        await browser.WaitForNavigationAsync("__leftForPractice", ct);
        Assert.True(await browser.WaitForAsync(
                "(() => { const n = document.querySelector('[data-training-notifications]'); return !!n && !n.hidden " +
                "&& document.querySelector('[data-guided-training]').getAttribute('data-step') === 'practice-new' " +
                "&& typeof window.__permissionRequests === 'number'; })()", Wait, ct),
            $"The practice-order step and its notification note are not shown on the reloaded Live Screen. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync(
            "!!(window.WaslaOrders && window.WaslaOrders.state && window.WaslaOrders.state.notificationSettings)", Wait, ct));
        // The reloaded page's first snapshot shows the real-order line and the practice-order button above the note.
        Assert.True(await browser.WaitForAsync(SnapshotRendered, Wait, ct),
            $"The reloaded Live Screen did not render its first snapshot. {host.ServerErrors}");

        await ClickAsync(browser, "[data-training-notifications] [data-notification-settings-open]", ct);
        Assert.True(await browser.WaitForAsync(ModalOpen(1), Wait, ct),
            $"The training step button did not open the notification settings modal ({await ModalStateAsync(browser, ct)}). {host.ServerErrors}");
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__permissionRequests", ct));

        // Escape from inside the modal closes only the modal; the training panel stays, and focus returns to its button.
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync(ModalClosed(1), Wait, ct), "Escape did not close the notification settings modal.");
        Assert.True(await browser.WaitForAsync(
                "document.activeElement === document.querySelector('[data-training-notifications] [data-notification-settings-open]')", Wait, ct),
            $"Focus did not return to the training step button; it is on {await ActiveElementAsync(browser, ct)}.");
        Assert.False(await browser.EvaluateAsync<bool>("document.querySelector('[data-training-panel]').hidden", ct));

        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__permissionRequests", ct));
        Assert.Equal("default", await browser.EvaluateAsync<string>("Notification.permission", ct));
        Assert.Equal(settingsBefore, await ServerSettingsAsync(host, ct));
        Assert.Null(host.ServerErrors);
    }

    private static async Task<HeadlessChromium> OpenLiveScreenAsync(TenantVisualWebHost host, string culture, CancellationToken ct)
    {
        var browser = await host.BrowserAsync(culture, 1280, 900, mobile: false, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser checks.");

        return await browser.SetUpAsync(async started =>
        {
            await started.AddScriptBeforePageScriptsAsync(Instrumentation, ct);
            try
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
            }
            catch (TimeoutException)
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
            }

            if (!await started.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", Wait, ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            Assert.True(await started.WaitForAsync("!!document.querySelector('[data-guided-training]') && !!window.bootstrap && !!window.WaslaOrders && !!window.WaslaOrders.notificationSettings", Wait, ct),
                $"The Live Screen with order training did not load. {host.ServerErrors}");
            Assert.True(await started.WaitForAsync(
                "!!(window.WaslaOrders.state && window.WaslaOrders.state.notificationSettings)", Wait, ct),
                $"The Live Screen did not load the user's notification settings. {host.ServerErrors}");
            // The settings are read before the first snapshot is requested, and that snapshot fills the settings menu's
            // automation status (with the pending-setup line above its notification settings entry).
            Assert.True(await started.WaitForAsync(SnapshotRendered, Wait, ct),
                $"The Live Screen did not render its first snapshot. {host.ServerErrors}");
        });
    }

    /// <summary>
    /// A real mouse click at the centre of the first visible match, after checking nothing covers it, and then that the
    /// click reached it. A click that leaves the page (a form post) is not followed onto the next document.
    /// </summary>
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct, bool navigates = false)
    {
        var target = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = [...document.querySelectorAll({{JsonSerializer.Serialize(selector)}})]
                .find(x => { const r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(x).visibility !== 'hidden'; });
              if (!e) return { found: false };
              // Instant, so the position read below is where the element stays (Bootstrap scrolls smoothly by default).
              e.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
              const r = e.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              window.__clickTarget = e;
              window.__clicked = null;
              return { found: true, x, y, hit: !!hit && e.contains(hit), at: {{Describe("hit")}}, rendered: window.__liveRendered };
            })()
            """, ct);
        Assert.True(target.GetProperty("found").GetBoolean(), $"{selector} is not on the page.");
        Assert.True(target.GetProperty("hit").GetBoolean(), $"{selector} is covered by {target.GetProperty("at").GetString()}.");
        var y = target.GetProperty("y").GetDouble();
        await browser.ClickAtAsync(target.GetProperty("x").GetDouble(), y, ct);
        if (navigates)
        {
            // The click must have submitted the form: its submit event fired during the click, so while the old document
            // is still here (it set __clickTarget) the submitted form is known. A new document already means it was sent.
            JsonElement sent;
            try
            {
                sent = await browser.EvaluateAsync<JsonElement>($$"""
                    (() => {
                      const e = window.__clickTarget, c = window.__clicked;
                      if (!e) return { ok: true };
                      return { ok: !!window.__submitted && !!e.form && window.__submitted === e.form, at: {{Describe("c")}} };
                    })()
                    """, ct);
            }
            catch (InvalidOperationException)
            {
                return; // The document is being replaced: the form was sent.
            }

            Assert.True(sent.GetProperty("ok").GetBoolean(),
                $"The click aimed at {selector} (y {y:0}) submitted no form; it landed on {sent.GetProperty("at").GetString()}.");
            return;
        }

        // The click must have reached the control it aimed at; content that moved it in the meantime fails here, by name.
        var landed = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = window.__clickTarget, c = window.__clicked, r = e.getBoundingClientRect();
              return { ok: !!c && e.contains(c), at: {{Describe("c")}}, y: r.top + r.height / 2, rendered: window.__liveRendered };
            })()
            """, ct);
        Assert.True(landed.GetProperty("ok").GetBoolean(),
            $"The click aimed at {selector} (y {y:0}, {target.GetProperty("rendered").GetInt32()} snapshots rendered) landed on " +
            $"{landed.GetProperty("at").GetString()}; the control is now at y {landed.GetProperty("y").GetDouble():0} " +
            $"({landed.GetProperty("rendered").GetInt32()} snapshots rendered).");
    }

    private static string Describe(string node) =>
        $"({node} ? {node}.tagName.toLowerCase() + ({node}.id ? '#' + {node}.id : '') + [...{node}.classList].slice(0, 3).map(c => '.' + c).join('') : 'nothing')";

    /// <summary>For a failure message: the page, the modal's state and the counted Bootstrap events.</summary>
    private static Task<string> ModalStateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>(
            "(() => { const m = document.getElementById('notificationSettingsModal'); return 'path ' + location.pathname + ', left ' + !!window.__leftForPractice " +
            "+ ', classes ' + (m ? m.className : 'no modal') + ', display ' + (m ? getComputedStyle(m).display : '-') " +
            "+ ', shown ' + window.__modalShown + ', hidden ' + window.__modalHidden + ', body ' + document.body.className; })()", ct);

    private static Task<string> ActiveElementAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>(
            "(() => { const a = document.activeElement; return a ? a.tagName.toLowerCase() + (a.id ? '#' + a.id : '') + ' ' + (a.textContent || '').trim().slice(0, 40) : 'nothing'; })()", ct);

    private static async Task<string> ServerSettingsAsync(TenantVisualWebHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IUserNotificationSettingsService>()
            .GetAsync(TenantVisualWebHost.TenantId, TenantVisualWebHost.OwnerId, ct);
        return JsonSerializer.Serialize(settings);
    }
}
