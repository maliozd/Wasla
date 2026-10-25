using System.Globalization;
using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// GitHub #72 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views,
/// stylesheets and scripts). The Live Screen's Settings button shows its localized label beside the gear and has the
/// height, radius and vertical centre of the Board / List / Focus selector beside it. In its popover each group heading
/// shares one row with a compact Settings action: the Notifications one opens the shared notification settings modal
/// from the keyboard and gives focus back to the Settings button, the Automation one links to order settings for Owners
/// and Managers only, and no separate bottom block remains. Checked in Turkish, Russian and Arabic (right to left), in
/// light and dark mode, at desktop and phone widths. Skips only when no Chromium browser is installed or Bootstrap cannot
/// be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenSettingsLayoutBrowserTests
{
    private const string TenantThemeKey = "Wasla.tenant.theme";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string Instrumentation = """
        window.__liveRendered = 0;
        window.__modalShown = 0;
        window.__modalHidden = 0;
        document.addEventListener('wasla:live-rendered', function () { window.__liveRendered++; });
        document.addEventListener('shown.bs.modal', function (e) { if (e.target.id === 'notificationSettingsModal') window.__modalShown++; });
        document.addEventListener('hidden.bs.modal', function (e) { if (e.target.id === 'notificationSettingsModal') window.__modalHidden++; });
        """;

    public static TheoryData<string, string, string, int> Layouts() => new()
    {
        { "tr-TR", "ltr", "light", 1280 },
        { "ar-SA", "rtl", "dark", 1280 },
        { "ru-RU", "ltr", "dark", 390 },
        { "ar-SA", "rtl", "light", 390 }
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task SettingsButton_MatchesTheViewSelector_AndThePopoverHeadingsCarryTheirSettingsActions(
        string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var label = $"{culture} {theme} {width}px";
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, culture, width, theme, ct);
        var settings = Resource(culture, "Layout.Settings");

        var toolbar = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const toggle = document.getElementById('ordersLiveDisplaySettingsToggle');
              const views = document.querySelector('.wasla-live-screen-view-switch');
              const t = toggle.getBoundingClientRect(), s = views.getBoundingClientRect();
              const rtl = document.documentElement.getAttribute('dir') === 'rtl';
              const width = document.documentElement.clientWidth;
              return {
                dir: document.documentElement.getAttribute('dir') || 'ltr',
                overflow: document.documentElement.scrollWidth > width,
                text: toggle.textContent.trim(),
                toggleHeight: t.height, viewsHeight: s.height,
                centreOffset: Math.abs((t.top + t.height / 2) - (s.top + s.height / 2)),
                gap: rtl ? t.left - s.right : s.left - t.right,
                toggleRadius: getComputedStyle(toggle).borderTopLeftRadius, viewsRadius: getComputedStyle(views).borderTopLeftRadius,
                onScreen: t.left >= 0 && t.right <= width && s.left >= 0 && s.right <= width
              };
            })()
            """, ct);

        Assert.Equal(direction, toolbar.GetProperty("dir").GetString());
        Assert.False(toolbar.GetProperty("overflow").GetBoolean(), $"{label}: the page scrolls sideways.");
        Assert.Equal(settings, toolbar.GetProperty("text").GetString());
        Assert.InRange(toolbar.GetProperty("toggleHeight").GetDouble() - toolbar.GetProperty("viewsHeight").GetDouble(), -0.5, 0.5);
        Assert.Equal(toolbar.GetProperty("viewsRadius").GetString(), toolbar.GetProperty("toggleRadius").GetString());
        Assert.True(toolbar.GetProperty("onScreen").GetBoolean(), $"{label}: the Settings button or the view selector is off screen.");
        if (width >= 992)
        {
            // Side by side on one line, separated by the toolbar's gap (the Stop sound button between them is hidden).
            Assert.True(toolbar.GetProperty("centreOffset").GetDouble() <= 1, $"{label}: the Settings button is not centred on the view selector.");
            Assert.InRange(toolbar.GetProperty("gap").GetDouble(), 6, 10);
        }

        var toggleNode = Assert.Single(await browser.AccessibleNodesAsync("#ordersLiveDisplaySettingsToggle", ct));
        Assert.Equal(("button", settings), (toggleNode.Role, toggleNode.Name));

        // Opened from the keyboard: the snapshot poll re-renders the header, so a pointer position can move under it.
        await FocusAsync(browser, "#ordersLiveDisplaySettingsToggle", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('ordersLiveDisplaySettingsMenu').classList.contains('show')", Wait, ct),
            $"{label}: the settings popover did not open.");

        var menu = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              {{TenantDarkModeBrowserTests.Library}}
              const menu = document.getElementById('ordersLiveDisplaySettingsMenu'), m = menu.getBoundingClientRect();
              const row = id => {
                const heading = document.getElementById(id), action = heading.parentElement.querySelector('.wasla-live-settings-menu__action');
                const h = heading.getBoundingClientRect(), a = action.getBoundingClientRect();
                return {
                  centreOffset: Math.abs((h.top + h.height / 2) - (a.top + a.height / 2)),
                  inside: a.left >= m.left && a.right <= m.right && a.top >= m.top && a.bottom <= m.bottom,
                  apart: a.left >= h.right - 1 || a.right <= h.left + 1,
                  bottom: Math.max(h.bottom, a.bottom),
                  contrast: ratio(parse(getComputedStyle(action).color), behind(action)),
                  color: getComputedStyle(action).color,
                  text: action.textContent.trim()
                };
              };
              const notifications = row('ordersLiveSettingsNotificationsHeading');
              return {
                onScreen: m.left >= 0 && m.right <= document.documentElement.clientWidth,
                automation: row('ordersLiveSettingsAutomationHeading'),
                notifications,
                soundBelow: document.getElementById('ordersLiveDisplaySoundStatus').getBoundingClientRect().top >= notifications.bottom - 0.5,
                entries: menu.querySelectorAll('[data-notification-settings-open]').length,
                bottomBlock: menu.querySelectorAll('.dropdown-divider, .dropdown-item').length
              };
            })()
            """, ct);

        Assert.True(menu.GetProperty("onScreen").GetBoolean(), $"{label}: the settings popover is off screen.");
        Assert.Equal(1, menu.GetProperty("entries").GetInt32());
        Assert.Equal(0, menu.GetProperty("bottomBlock").GetInt32());
        Assert.True(menu.GetProperty("soundBelow").GetBoolean(), $"{label}: the notification sound status is not below the Notifications heading.");
        foreach (var group in new[] { "automation", "notifications" })
        {
            var row = menu.GetProperty(group);
            Assert.True(row.GetProperty("centreOffset").GetDouble() <= 1.5, $"{label}: the {group} Settings action is not on its heading's row.");
            Assert.True(row.GetProperty("inside").GetBoolean() && row.GetProperty("apart").GetBoolean(), $"{label}: the {group} Settings action overlaps or leaves the popover.");
            Assert.True(row.GetProperty("contrast").GetDouble() >= 4.5, $"{label}: {group} Settings action contrast {row.GetProperty("contrast").GetDouble():0.00}.");
            Assert.Equal(settings, row.GetProperty("text").GetString());
        }
        // The automation action is a link and the notifications one a button; they look the same in both themes.
        Assert.Equal(menu.GetProperty("notifications").GetProperty("color").GetString(), menu.GetProperty("automation").GetProperty("color").GetString());

        // Named "Settings" plus the group heading, so each action is unambiguous out of context.
        AssertNamed(await browser.AccessibleNodesAsync("#ordersLiveDisplayNotificationSettings", ct), "button", settings, Resource(culture, "Notification.Notifications"), culture);
        AssertNamed(await browser.AccessibleNodesAsync("#ordersLiveDisplayAutomationSettings", ct), "link", settings, Resource(culture, "Orders.LiveSettings.AutomationGroup"), culture);

        // Tab from the Settings button reaches the Notifications action; Enter opens the one shared modal.
        for (var i = 0; i < 4 && await ActiveIdAsync(browser, ct) != "ordersLiveDisplayNotificationSettings"; i++)
            await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal("ordersLiveDisplayNotificationSettings", await ActiveIdAsync(browser, ct));
        Assert.NotEqual("none", await browser.EvaluateAsync<string>("getComputedStyle(document.activeElement).outlineStyle", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(
                "(() => { const m = document.getElementById('notificationSettingsModal'); return !!m && m.classList.contains('show') " +
                "&& !!m.querySelector('#ShowBrowserNotification') && window.__modalShown === 1; })()", Wait, ct),
            $"{label}: the Notifications Settings action did not open the notification settings modal. {host.ServerErrors}");
        Assert.Equal(1, await browser.EvaluateAsync<int>("document.querySelectorAll('#notificationSettingsModal').length", ct));

        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("window.__modalHidden === 1 && !document.body.classList.contains('modal-open')", Wait, ct),
            $"{label}: Escape did not close the notification settings modal.");
        Assert.True(await browser.WaitForAsync("document.activeElement && document.activeElement.id === 'ordersLiveDisplaySettingsToggle'", Wait, ct),
            $"{label}: focus did not return to the Settings button; it is on {await ActiveIdAsync(browser, ct)}.");
        Assert.Null(host.ServerErrors);
    }

    [Fact]
    public async Task KitchenUser_GetsTheNotificationsAction_ButNoOrderSettingsLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, "tr-TR", 1280, "light", ct, signInAs: "kitchen");

        var menu = await browser.EvaluateAsync<JsonElement>("""
            (() => ({
              notifications: !!document.querySelector('#ordersLiveDisplaySettingsMenu #ordersLiveDisplayNotificationSettings[data-notification-settings-open]'),
              automationLinks: document.querySelectorAll('#ordersLiveDisplaySettingsMenu a[href="/settings/orders"]').length,
              automationHeading: !!document.getElementById('ordersLiveSettingsAutomationHeading')
            }))()
            """, ct);

        Assert.True(menu.GetProperty("notifications").GetBoolean());
        Assert.True(menu.GetProperty("automationHeading").GetBoolean());
        Assert.Equal(0, menu.GetProperty("automationLinks").GetInt32());
    }

    private static void AssertNamed(IReadOnlyList<AccessibleNode> nodes, string role, string settings, string heading, string culture)
    {
        var node = Assert.Single(nodes);
        Assert.Equal(role, node.Role);
        // The heading is upper-cased by CSS, which the browser may carry into the computed name.
        Assert.True(
            string.Compare(node.Name, $"{settings} {heading}", new CultureInfo(culture), CompareOptions.IgnoreCase) == 0,
            $"Accessible name '{node.Name}', expected '{settings} {heading}'.");
    }

    private static Task<string> ActiveIdAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string>("document.activeElement ? (document.activeElement.id || document.activeElement.tagName.toLowerCase()) : 'nothing'", ct);

    private static async Task FocusAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var focused = await browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); if (!e) return false; e.focus(); return document.activeElement === e; }})()", ct);
        Assert.True(focused, $"{selector} could not be focused.");
    }

    private static async Task<HeadlessChromium> OpenLiveScreenAsync(
        TenantVisualWebHost host, string culture, int width, string theme, CancellationToken ct, string? signInAs = null)
    {
        var browser = await host.BrowserAsync(culture, width, 900, mobile: width < 992, ct, signInAs);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser checks.");

        return await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(theme, ct);
            await started.NavigateAsync(new Uri(host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); localStorage.setItem('{TenantThemeKey}', '{theme}'); return true; }})()", ct);
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
            Assert.True(await started.WaitForAsync(
                    "!!window.bootstrap && !!window.WaslaOrders && !!(window.WaslaOrders.state && window.WaslaOrders.state.notificationSettings)", Wait, ct),
                $"The Live Screen did not load the user's notification settings. {host.ServerErrors}");
            // The first snapshot fills the automation status in the popover, which moves the rows below it.
            Assert.True(await started.WaitForAsync("window.__liveRendered >= 1", Wait, ct),
                $"The Live Screen did not render its first snapshot. {host.ServerErrors}");
        });
    }

    private static string Resource(string culture, string key) =>
        System.Xml.Linq.XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
