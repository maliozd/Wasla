using System.Globalization;
using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// GitHub #72 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views,
/// stylesheets and scripts). The Live Screen's settings gear is a square button named "Settings" (aria-label and
/// tooltip) with the height, radius and vertical centre of the Board / List / Focus selector before it, and the Wasla
/// logo comes last in the header. In its popover each group heading
/// shares one row with a compact Settings action: the Notifications one opens the shared notification settings modal
/// from the keyboard and gives focus back to the Settings button, the Automation one links to order settings for Owners
/// and Managers only, and no separate bottom block remains. Below the header, the order summary is one unboxed row of
/// icon, count and label per metric, with the snapshot's counts in the page culture's digits and grouping, Today's first
/// in the reading direction, and on phones wrapping only between metrics. Checked in Turkish, Russian and Arabic (right
/// to left), in light and dark mode, at desktop and phone widths. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
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
              const logo = document.querySelector('.wasla-live-display-brand').getBoundingClientRect();
              return {
                dir: document.documentElement.getAttribute('dir') || 'ltr',
                overflow: document.documentElement.scrollWidth > width,
                text: toggle.textContent.trim(),
                title: toggle.getAttribute('title'),
                toggleHeight: t.height, toggleWidth: t.width, viewsHeight: s.height,
                centreOffset: Math.abs((t.top + t.height / 2) - (s.top + s.height / 2)),
                gap: rtl ? s.left - t.right : t.left - s.right,
                logoLast: rtl ? logo.right <= t.left + 1 : logo.left >= t.right - 1,
                toggleRadius: getComputedStyle(toggle).borderTopLeftRadius, viewsRadius: getComputedStyle(views).borderTopLeftRadius,
                onScreen: t.left >= 0 && t.right <= width && s.left >= 0 && s.right <= width
              };
            })()
            """, ct);

        Assert.Equal(direction, toolbar.GetProperty("dir").GetString());
        Assert.False(toolbar.GetProperty("overflow").GetBoolean(), $"{label}: the page scrolls sideways.");
        // A gear button: no visible words, named "Settings" by its aria-label and tooltip, square and as tall as the switch.
        Assert.Equal("", toolbar.GetProperty("text").GetString());
        Assert.Equal(settings, toolbar.GetProperty("title").GetString());
        Assert.InRange(toolbar.GetProperty("toggleWidth").GetDouble() - toolbar.GetProperty("toggleHeight").GetDouble(), -0.5, 0.5);
        Assert.InRange(toolbar.GetProperty("toggleHeight").GetDouble() - toolbar.GetProperty("viewsHeight").GetDouble(), -0.5, 0.5);
        Assert.True(toolbar.GetProperty("logoLast").GetBoolean(), $"{label}: the Wasla logo is not after the settings gear.");
        Assert.Equal(toolbar.GetProperty("viewsRadius").GetString(), toolbar.GetProperty("toggleRadius").GetString());
        Assert.True(toolbar.GetProperty("onScreen").GetBoolean(), $"{label}: the Settings button or the view selector is off screen.");
        if (width >= 992)
        {
            // The gear follows the switch on one line, separated by the header's gap.
            Assert.True(toolbar.GetProperty("centreOffset").GetDouble() <= 1, $"{label}: the settings gear is not centred on the view selector.");
            Assert.InRange(toolbar.GetProperty("gap").GetDouble(), 10, 14);
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

    public static TheoryData<string, string, string, int> SummaryLayouts() => new()
    {
        { "tr-TR", "ltr", "light", 1440 },
        { "ru-RU", "ltr", "dark", 1024 },
        { "ar-SA", "rtl", "dark", 1024 },
        { "tr-TR", "ltr", "light", 390 },
        { "ar-SA", "rtl", "light", 390 }
    };

    [Theory]
    [MemberData(nameof(SummaryLayouts))]
    public async Task Summary_IsAnUnboxedRowOfCultureFormattedCounts_ThatWrapsOnlyBetweenMetrics(
        string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var label = $"{culture} {theme} {width}px";
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, culture, width, theme, ct);

        var summary = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              const data = await (await fetch('/orders/live-data', { headers: { Accept: 'application/json' } })).json();
              const activeStatuses = ['New', 'Accepted', 'Preparing', 'ReadyForPickup', 'OnTheWay'];
              const active = data.orders.filter(o => !o.isDemo && activeStatuses.includes(o.status)).length;
              const format = new Intl.NumberFormat(document.documentElement.lang);
              const row = document.querySelector('.wasla-live-summary');
              const metrics = [...row.querySelectorAll('.wasla-live-summary__metric')];
              const box = e => e.getBoundingClientRect();
              const rtl = document.documentElement.getAttribute('dir') === 'rtl';
              const first = box(metrics[0]);
              return {
                dir: document.documentElement.getAttribute('dir') || 'ltr',
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                rowHeight: box(row).height,
                lines: new Set(metrics.map(m => Math.round(box(m).top))).size,
                todayAtStart: metrics.every(m => rtl ? box(m).right <= first.right + 1 : box(m).left >= first.left - 1),
                labelsOneLine: metrics.every(m => m.querySelector('.wasla-live-summary__label').getClientRects().length === 1
                  && box(m.querySelector('.wasla-live-summary__label')).height < 24),
                boxed: metrics.some(m => { const c = getComputedStyle(m); return c.borderTopStyle !== 'none' || c.backgroundColor !== 'rgba(0, 0, 0, 0)'; }),
                values: metrics.map(m => m.querySelector('.wasla-live-summary__value').textContent),
                expected: [format.format(data.todayOrderCount), format.format(active), format.format(data.cancelledOrderCount)],
                zero: metrics.map(m => m.classList.contains('is-zero')),
                expectedZero: [data.todayOrderCount === 0, active === 0, data.cancelledOrderCount === 0],
                icons: metrics.map(m => { const c = getComputedStyle(m.querySelector('.wasla-live-summary__icon'), '::before'); return c.maskImage || c.webkitMaskImage; }),
                cancelledIcon: getComputedStyle(metrics[2].querySelector('.wasla-live-summary__icon'), '::before').backgroundColor,
                todayIcon: getComputedStyle(metrics[0].querySelector('.wasla-live-summary__icon'), '::before').backgroundColor
              };
            })()
            """, ct);

        Assert.Equal(direction, summary.GetProperty("dir").GetString());
        Assert.False(summary.GetProperty("overflow").GetBoolean(), $"{label}: the page scrolls sideways.");
        // No box, border or fill: the summary never reads as a button, filter or view switch.
        Assert.False(summary.GetProperty("boxed").GetBoolean(), $"{label}: a metric has a border or a background.");
        // Counts in the culture's digits and grouping, the same numbers as the snapshot; a 0 is quiet.
        Assert.Equal(
            summary.GetProperty("expected").EnumerateArray().Select(v => v.GetString()),
            summary.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(
            summary.GetProperty("expectedZero").EnumerateArray().Select(v => v.GetBoolean()),
            summary.GetProperty("zero").EnumerateArray().Select(v => v.GetBoolean()));
        foreach (var (icon, name) in summary.GetProperty("icons").EnumerateArray().Zip(new[] { "today", "active", "cancelled" }))
            Assert.Contains($"metric-{name}.svg", icon.GetString(), StringComparison.Ordinal);
        if (!summary.GetProperty("zero")[2].GetBoolean())
            Assert.NotEqual(summary.GetProperty("todayIcon").GetString(), summary.GetProperty("cancelledIcon").GetString());
        // Today's orders come first in the reading direction, and no label breaks inside itself.
        Assert.True(summary.GetProperty("todayAtStart").GetBoolean(), $"{label}: today's orders are not at the start of the row.");
        Assert.True(summary.GetProperty("labelsOneLine").GetBoolean(), $"{label}: a label wraps.");
        if (width >= 1024)
        {
            Assert.Equal(1, summary.GetProperty("lines").GetInt32());
            Assert.True(summary.GetProperty("rowHeight").GetDouble() <= 26, $"{label}: the row is {summary.GetProperty("rowHeight").GetDouble()} px tall.");
        }
        Assert.Null(host.ServerErrors);
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
