using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The tenant sidebar with <c>localStorage</c> blocked before any page script runs, in a real headless Chromium browser
/// (Edge or Chrome) against the real tenant layout: no page error, the phone/tablet drawer opens and closes by mouse,
/// keyboard, Escape and the backdrop with focus moved and restored, and the desktop collapse control works, in English
/// LTR and Arabic RTL at 390, 820 and 1366 px. One test checks that the preference is still saved and read when storage
/// works, without touching other keys. Skips only when no Chromium browser is installed (see
/// <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class TenantSidebarStorageBrowserTests : IAsyncLifetime
{
    private const string SidebarKey = "Wasla.sidebarCollapsed";

    // Runs in every document before the page's own scripts: records every uncaught page error, with the script files on
    // its stack, and every unhandled rejection.
    private const string ErrorProbe = """
        (() => {
          const errors = [];
          window.__waslaPageErrors = errors;
          const scripts = stack => [...new Set((String(stack || '').match(/[\w.-]+\.js/g) || []))].join(' < ');
          addEventListener('error', e => errors.push(e.message + ' [' + (scripts(e.error && e.error.stack) || e.filename) + ']'));
          addEventListener('unhandledrejection', e => errors.push('unhandled rejection: ' + String(e.reason)));
        })();
        """;

    // Blocks storage as a browser with storage disabled does: reading window.localStorage throws a SecurityError.
    private const string BlockProperty = """
        Object.defineProperty(window, 'localStorage', {
          configurable: true,
          get() { throw new DOMException('The operation is insecure.', 'SecurityError'); }
        });
        """;

    // The property is reachable but every Storage method throws.
    private const string BlockMethods = """
        ['getItem', 'setItem', 'removeItem', 'clear', 'key'].forEach(name => {
          Storage.prototype[name] = function () { throw new DOMException('The operation is insecure.', 'SecurityError'); };
        });
        """;

    private const string State = """
        (() => {
          const html = document.documentElement, body = document.body;
          const sidebar = document.getElementById('app-sidebar-tenant');
          const open = document.getElementById('tenantNavOpen');
          const collapse = document.getElementById('tenantSidebarToggle');
          const main = document.getElementById('tenant-main');
          const backdrop = document.getElementById('tenantNavBackdrop');
          const box = e => { const r = e.getBoundingClientRect(); return { left: r.left, right: r.right, width: r.width }; };
          const shown = e => !!e && e.getBoundingClientRect().width > 0 && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none';
          return {
            dir: html.getAttribute('dir'), width: innerWidth, scrollWidth: html.scrollWidth, clientWidth: html.clientWidth,
            navOpen: body.classList.contains('wasla-nav-open'), locked: body.classList.contains('wasla-nav-lock'),
            mobileShell: body.classList.contains('wasla-shell-mobile'), desktopShell: body.classList.contains('wasla-shell-desktop'),
            collapsed: body.classList.contains('sidebar-collapsed'),
            sidebar: box(sidebar), sidebarHidden: sidebar.getAttribute('aria-hidden'), sidebarInert: !!sidebar.inert,
            mainInert: !!main.inert, backdropHidden: backdrop.hidden,
            triggerShown: shown(open), triggerExpanded: open.getAttribute('aria-expanded'), triggerOwner: open.getAttribute('data-wasla-nav-owner'),
            collapseShown: shown(collapse), collapseExpanded: collapse.getAttribute('aria-expanded'), collapseTitle: collapse.getAttribute('title'),
            titleExpand: collapse.getAttribute('data-title-expand'), titleCollapse: collapse.getAttribute('data-title-collapse'),
            focused: document.activeElement ? document.activeElement.id || document.activeElement.nodeName : null,
            focusInSidebar: sidebar.contains(document.activeElement),
            errors: window.__waslaPageErrors || null
          };
        })()
        """;

    private ThemeWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await ThemeWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string, string, string, int> BlockedCases()
    {
        var data = new TheoryData<string, string, string, int>();
        foreach (var (culture, direction) in new[] { ("en-US", "ltr"), ("ar-SA", "rtl") })
            foreach (var width in new[] { 390, 820, 1366 })
                data.Add("property", culture, direction, width);
        data.Add("methods", "en-US", "ltr", 390);
        data.Add("methods", "ar-SA", "rtl", 1366);
        return data;
    }

    [Theory]
    [MemberData(nameof(BlockedCases))]
    public async Task WithStorageBlocked_TheSidebarWorks_WithoutAPageError(string blocked, string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"{blocked} blocked, {width}px {culture}";
        await using var browser = await BrowserAsync(culture, width, ct);
        await browser.AddScriptBeforePageScriptsAsync(ErrorProbe + (blocked == "property" ? BlockProperty : BlockMethods), ct);
        await LoadTenantAsync(browser, "/help", ct);

        var state = await StateAsync(browser, ct);
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        Assert.True(await browser.EvaluateAsync<bool>(blocked == "property"
            ? "(() => { try { window.localStorage; return false; } catch (e) { return e.name === 'SecurityError'; } })()"
            : "(() => { try { localStorage.getItem('x'); return false; } catch (e) { return e.name === 'SecurityError'; } })()", ct),
            $"{page}: storage is not blocked");
        AssertNoHorizontalOverflow(state, page);

        if (width < 992)
            await ExerciseDrawerAsync(browser, direction, page, ct);
        else
            await ExerciseDesktopCollapseAsync(browser, page, ct);

        // Nothing could be saved: a reload starts from the expanded default and is still fully bound.
        await ReloadAsync(browser, ct);
        state = await StateAsync(browser, ct);
        Assert.False(state.GetProperty("collapsed").GetBoolean(), $"{page}: not expanded after reload");
        Assert.Equal("sidebar-toggle", state.GetProperty("triggerOwner").GetString());
        if (width < 992)
        {
            await ClickElementAsync(browser, "#tenantNavOpen", ct);
            Assert.True(await browser.WaitForAsync("document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: drawer did not open after reload");
            await browser.PressKeyAsync("Escape", shift: false, ct);
            Assert.True(await browser.WaitForAsync("!document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: drawer did not close after reload");
        }

        AssertNoPageErrors(await StateAsync(browser, ct), page + " at the end");
    }

    [Fact]
    public async Task WithStorageAvailable_TheCollapsedPreferenceIsSavedAndRead_AndOtherKeysStayUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1366, ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
        await browser.EvaluateAsync<bool>("""
            (() => {
              localStorage.clear();
              localStorage.setItem('Wasla.tenant.theme', 'light');
              localStorage.setItem('wasla.test.unrelated', 'keep');
              return true;
            })()
            """, ct);
        await browser.AddScriptBeforePageScriptsAsync(ErrorProbe, ct);
        await LoadTenantAsync(browser, "/help", ct);

        var state = await StateAsync(browser, ct);
        Assert.False(state.GetProperty("collapsed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await StoredAsync(browser, ct)).GetProperty("sidebar").ValueKind);

        await ClickElementAsync(browser, "#tenantSidebarToggle", ct);
        Assert.True(await browser.WaitForAsync("document.body.classList.contains('sidebar-collapsed')", TimeSpan.FromSeconds(5), ct));
        Assert.Equal("true", (await StoredAsync(browser, ct)).GetProperty("sidebar").GetString());

        await ReloadAsync(browser, ct);
        state = await StateAsync(browser, ct);
        Assert.True(state.GetProperty("collapsed").GetBoolean(), "The saved collapsed state was not read after reload.");
        Assert.Equal("false", state.GetProperty("collapseExpanded").GetString());

        await ClickElementAsync(browser, "#tenantSidebarToggle", ct);
        Assert.True(await browser.WaitForAsync("!document.body.classList.contains('sidebar-collapsed')", TimeSpan.FromSeconds(5), ct));
        var stored = await StoredAsync(browser, ct);
        Assert.Equal("false", stored.GetProperty("sidebar").GetString());
        Assert.Equal("light", stored.GetProperty("theme").GetString());
        Assert.Equal("keep", stored.GetProperty("unrelated").GetString());
        Assert.Equal(3, stored.GetProperty("count").GetInt32());
        AssertNoPageErrors(await StateAsync(browser, ct), "storage available");
    }

    private async Task ExerciseDrawerAsync(HeadlessChromium browser, string direction, string page, CancellationToken ct)
    {
        var state = await StateAsync(browser, ct);
        Assert.True(state.GetProperty("mobileShell").GetBoolean(), $"{page}: not the mobile shell; page errors: {state.GetProperty("errors")}");
        Assert.True(state.GetProperty("triggerShown").GetBoolean(), $"{page}: no visible navigation trigger");
        Assert.True("sidebar-toggle" == state.GetProperty("triggerOwner").GetString(),
            $"{page}: the navigation trigger was never bound; page errors: {state.GetProperty("errors")}");
        AssertDrawerClosed(state, direction, page + " initially", expectFocusOnTrigger: false);

        // Mouse: open, then Escape.
        await ClickElementAsync(browser, "#tenantNavOpen", ct);
        AssertDrawerOpen(await WaitForDrawerAsync(browser, open: true, page, ct), direction, page + " after a click");
        await browser.PressKeyAsync("Escape", shift: false, ct);
        AssertDrawerClosed(await WaitForDrawerAsync(browser, open: false, page, ct), direction, page + " after Escape", expectFocusOnTrigger: true);

        // Keyboard: Enter on the focused trigger opens; Enter on the focused close button closes.
        await browser.EvaluateAsync<bool>("(() => { document.getElementById('tenantNavOpen').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        AssertDrawerOpen(await WaitForDrawerAsync(browser, open: true, page, ct), direction, page + " after Enter");
        await browser.PressKeyAsync("Enter", shift: false, ct);
        AssertDrawerClosed(await WaitForDrawerAsync(browser, open: false, page, ct), direction, page + " after Enter on close", expectFocusOnTrigger: true);

        // Space opens; a real click on the backdrop beside the drawer closes.
        await browser.PressKeyAsync(" ", shift: false, ct);
        state = await WaitForDrawerAsync(browser, open: true, page, ct);
        AssertDrawerOpen(state, direction, page + " after Space");
        var width = state.GetProperty("width").GetDouble();
        await browser.ClickAtAsync(direction == "rtl" ? 12 : width - 12, 500, ct);
        // Existing behavior, independent of storage: pressing on the backdrop moves focus to <body> before the drawer
        // closes, so focus is not restored to the trigger here; it must not stay inside the hidden drawer.
        state = await WaitForDrawerAsync(browser, open: false, page, ct);
        AssertDrawerClosed(state, direction, page + " after the backdrop", expectFocusOnTrigger: false);
        Assert.False(state.GetProperty("focusInSidebar").GetBoolean(), $"{page}: focus stayed inside the closed drawer");
        AssertNoHorizontalOverflow(await StateAsync(browser, ct), page + " after the drawer");
    }

    private static async Task ExerciseDesktopCollapseAsync(HeadlessChromium browser, string page, CancellationToken ct)
    {
        var state = await StateAsync(browser, ct);
        Assert.True(state.GetProperty("desktopShell").GetBoolean(), $"{page}: not the desktop shell; page errors: {state.GetProperty("errors")}");
        Assert.False(state.GetProperty("triggerShown").GetBoolean(), $"{page}: the phone trigger is shown on desktop");
        Assert.True(state.GetProperty("collapseShown").GetBoolean(), $"{page}: no visible collapse control");
        AssertCollapsed(state, false, page + " initially");
        var expandedWidth = state.GetProperty("sidebar").GetProperty("width").GetDouble();

        // Mouse.
        await ClickElementAsync(browser, "#tenantSidebarToggle", ct);
        state = await WaitForCollapsedAsync(browser, true, page, ct);
        AssertCollapsed(state, true, page + " after a click");
        Assert.True(state.GetProperty("sidebar").GetProperty("width").GetDouble() < expandedWidth,
            $"{page}: the sidebar did not narrow ({state.GetProperty("sidebar")})");

        // Keyboard on the focused control: Enter expands, Space collapses; focus stays on the control.
        await browser.EvaluateAsync<bool>("(() => { document.getElementById('tenantSidebarToggle').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        state = await WaitForCollapsedAsync(browser, false, page, ct);
        AssertCollapsed(state, false, page + " after Enter");
        Assert.Equal("tenantSidebarToggle", state.GetProperty("focused").GetString());
        Assert.Equal(expandedWidth, state.GetProperty("sidebar").GetProperty("width").GetDouble(), 1);
        await browser.PressKeyAsync(" ", shift: false, ct);
        state = await WaitForCollapsedAsync(browser, true, page, ct);
        AssertCollapsed(state, true, page + " after Space");
        Assert.Equal("tenantSidebarToggle", state.GetProperty("focused").GetString());

        // Escape on desktop does not open or change anything.
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await Task.Delay(200, ct);
        state = await StateAsync(browser, ct);
        Assert.False(state.GetProperty("navOpen").GetBoolean());
        AssertCollapsed(state, true, page + " after Escape");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("sidebarHidden").ValueKind);
        Assert.False(state.GetProperty("sidebarInert").GetBoolean());
        AssertNoHorizontalOverflow(state, page + " collapsed");
    }

    private static void AssertDrawerOpen(JsonElement state, string direction, string page)
    {
        Assert.True(state.GetProperty("navOpen").GetBoolean(), $"{page}: drawer not open");
        Assert.True(state.GetProperty("locked").GetBoolean(), $"{page}: page scroll not locked");
        Assert.False(state.GetProperty("backdropHidden").GetBoolean(), $"{page}: no backdrop");
        Assert.Equal("true", state.GetProperty("triggerExpanded").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("sidebarHidden").ValueKind);
        Assert.False(state.GetProperty("sidebarInert").GetBoolean(), $"{page}: open drawer is inert");
        Assert.True(state.GetProperty("mainInert").GetBoolean(), $"{page}: content behind the drawer is interactive");
        Assert.True("tenantNavClose" == state.GetProperty("focused").GetString(), $"{page}: focus is on {state.GetProperty("focused")}, expected the close button");

        // The drawer is on screen at the inline-start edge: left in LTR, right in RTL.
        var sidebar = state.GetProperty("sidebar");
        var width = state.GetProperty("width").GetDouble();
        if (direction == "rtl")
            Assert.True(Math.Abs(sidebar.GetProperty("right").GetDouble() - width) <= 1, $"{page}: drawer not at the right edge ({sidebar})");
        else
            Assert.True(Math.Abs(sidebar.GetProperty("left").GetDouble()) <= 1, $"{page}: drawer not at the left edge ({sidebar})");
    }

    private static void AssertDrawerClosed(JsonElement state, string direction, string page, bool expectFocusOnTrigger)
    {
        Assert.False(state.GetProperty("navOpen").GetBoolean(), $"{page}: drawer still open");
        Assert.False(state.GetProperty("locked").GetBoolean(), $"{page}: page scroll still locked");
        Assert.True(state.GetProperty("backdropHidden").GetBoolean(), $"{page}: backdrop still shown");
        Assert.Equal("false", state.GetProperty("triggerExpanded").GetString());
        Assert.Equal("true", state.GetProperty("sidebarHidden").GetString());
        Assert.True(state.GetProperty("sidebarInert").GetBoolean(), $"{page}: closed drawer is not inert");
        Assert.False(state.GetProperty("mainInert").GetBoolean(), $"{page}: content is still inert");
        if (expectFocusOnTrigger)
            Assert.True("tenantNavOpen" == state.GetProperty("focused").GetString(), $"{page}: focus is on {state.GetProperty("focused")}, expected the trigger");

        // Off screen beyond the inline-start edge.
        var sidebar = state.GetProperty("sidebar");
        var width = state.GetProperty("width").GetDouble();
        if (direction == "rtl")
            Assert.True(sidebar.GetProperty("left").GetDouble() >= width - 1, $"{page}: closed drawer is on screen ({sidebar})");
        else
            Assert.True(sidebar.GetProperty("right").GetDouble() <= 1, $"{page}: closed drawer is on screen ({sidebar})");
    }

    private static void AssertCollapsed(JsonElement state, bool collapsed, string page)
    {
        Assert.True(collapsed == state.GetProperty("collapsed").GetBoolean(), $"{page}: collapsed is {state.GetProperty("collapsed")}, expected {collapsed}");
        Assert.Equal(collapsed ? "false" : "true", state.GetProperty("collapseExpanded").GetString());
        Assert.Equal(
            state.GetProperty(collapsed ? "titleExpand" : "titleCollapse").GetString(),
            state.GetProperty("collapseTitle").GetString());
    }

    private static void AssertNoPageErrors(JsonElement state, string page)
    {
        var errors = state.GetProperty("errors");
        Assert.True(errors.ValueKind == JsonValueKind.Array, $"{page}: the error probe did not run");
        Assert.True(errors.GetArrayLength() == 0, $"{page}: page errors: {errors}");
    }

    private static void AssertNoHorizontalOverflow(JsonElement state, string page) =>
        Assert.True(state.GetProperty("scrollWidth").GetDouble() <= state.GetProperty("clientWidth").GetDouble() + 1,
            $"{page}: the page scrolls sideways ({state.GetProperty("scrollWidth")} > {state.GetProperty("clientWidth")})");

    private static async Task<JsonElement> WaitForDrawerAsync(HeadlessChromium browser, bool open, string page, CancellationToken ct)
    {
        var expression = open ? "document.body.classList.contains('wasla-nav-open')" : "!document.body.classList.contains('wasla-nav-open')";
        Assert.True(await browser.WaitForAsync(expression, TimeSpan.FromSeconds(5), ct), $"{page}: drawer did not {(open ? "open" : "close")}");
        await Task.Delay(400, ct); // the 0.2 s slide
        return await StateAsync(browser, ct);
    }

    private static async Task<JsonElement> WaitForCollapsedAsync(HeadlessChromium browser, bool collapsed, string page, CancellationToken ct)
    {
        var expression = collapsed ? "document.body.classList.contains('sidebar-collapsed')" : "!document.body.classList.contains('sidebar-collapsed')";
        Assert.True(await browser.WaitForAsync(expression, TimeSpan.FromSeconds(5), ct), $"{page}: sidebar did not {(collapsed ? "collapse" : "expand")}");
        await Task.Delay(400, ct);
        return await StateAsync(browser, ct);
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, signedIn: true, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser sidebar checks.");
        await browser.SetViewportAsync(width, 860, mobile: width < 992, ct);
        return browser;
    }

    private async Task LoadTenantAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
        await Task.Delay(300, ct);
    }

    private static async Task ReloadAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>("(() => { window.__waslaBeforeReload = true; setTimeout(() => location.reload(), 0); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!window.__waslaBeforeReload && document.readyState === 'complete'", TimeSpan.FromSeconds(30), ct), "The page did not reload.");
        await Task.Delay(400, ct);
    }

    private static async Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>(State, ct);

    private static async Task<JsonElement> StoredAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>($$"""
            ({ sidebar: localStorage.getItem('{{SidebarKey}}'), theme: localStorage.getItem('Wasla.tenant.theme'),
               unrelated: localStorage.getItem('wasla.test.unrelated'), count: localStorage.length })
            """, ct);

    // A real mouse click at the element's center, after checking that nothing covers it there.
    private static async Task ClickElementAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var target = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = document.querySelector({{JsonSerializer.Serialize(selector)}});
              const r = e.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              return { x, y, hit: !!hit && e.contains(hit) };
            })()
            """, ct);
        Assert.True(target.GetProperty("hit").GetBoolean(), $"{selector} is covered or off screen.");
        await browser.ClickAtAsync(target.GetProperty("x").GetDouble(), target.GetProperty("y").GetDouble(), ct);
    }
}
