using System.Text.Json;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Central Admin layout in a real headless Chromium browser (Edge or Chrome), English LTR and Arabic RTL:
/// the mobile navigation drawer (AdminLTE's PushMenu plus wasla-admin-nav.js) and the definition lists on the Admin
/// detail pages. Real keyboard, mouse and wheel input is used throughout. Skips only when no Chromium browser is
/// installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or AdminLTE cannot be loaded from its CDN.
/// </summary>
public sealed class AdminMobileNavigationBrowserTests : IAsyncLifetime
{
    // Long, unbroken values: they must wrap inside their card instead of widening or clipping it.
    private const string LongSlug = "uzun-alanadi-0123456789abcdefghijklmnopqrstuvwxyz-0123456789abcdef";
    private const string LongName = "مطعم الكباب Restaurant-with-an-extraordinarily-long-unbroken-name-0123456789abcdefghij";
    private const string LongRegistrationSlug = "kayit-0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789";

    private const string State = """
        (() => {
          const t = document.getElementById('waslaAdminNavToggle');
          const s = document.getElementById('waslaAdminSidebar');
          const close = s.querySelector('[data-wasla-admin-nav-close]');
          const o = document.querySelector('.sidebar-overlay');
          const logo = document.querySelector('.wasla-admin-header-brand');
          const header = document.getElementById('waslaAppHeader');
          const main = document.querySelector('.app-wrapper > .app-main');
          const r = e => { const b = e.getBoundingClientRect(); return { left: b.left, right: b.right, top: b.top, bottom: b.bottom, width: b.width, height: b.height }; };
          const shown = e => !!e && getComputedStyle(e).display !== 'none' && e.getBoundingClientRect().width > 0;
          const a = document.activeElement;
          return {
            width: innerWidth, height: innerHeight, dir: document.documentElement.dir,
            open: document.body.classList.contains('sidebar-open'), collapsed: document.body.classList.contains('sidebar-collapse'),
            toggleShown: shown(t), toggleExpanded: t.getAttribute('aria-expanded'), toggleControls: t.getAttribute('aria-controls'),
            toggleName: t.getAttribute('aria-label') || '', toggleLteOwned: t.getAttribute('data-lte-toggle'),
            closeShown: shown(close), closeName: (close && close.getAttribute('aria-label')) || '',
            toggle: r(t), logoShown: shown(logo), logo: logo ? r(logo) : null, sidebar: r(s),
            overlayVisible: !!o && getComputedStyle(o).visibility === 'visible' && o.getBoundingClientRect().width > 0 && o.getBoundingClientRect().height > 0, overlay: o ? r(o) : null,
            overlays: document.querySelectorAll('.sidebar-overlay').length,
            sidebarInert: !!s.inert, headerInert: !!header.inert, mainInert: !!main.inert,
            bodyOverflow: getComputedStyle(document.body).overflowY, rootOverflow: getComputedStyle(document.documentElement).overflowY,
            headerControls: [...header.querySelectorAll('.dropdown-toggle, .wasla-theme-toggle, form button')].filter(shown).length,
            active: a === t ? 'toggle' : a === close ? 'close' : s.contains(a) ? 'sidebar' : header.contains(a) ? 'header'
              : main.contains(a) ? 'main' : (a ? a.tagName.toLowerCase() : 'none'),
            scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth, scrollY: window.scrollY
          };
        })()
        """;

    private const string IsOpen = """
        (() => { const b = document.getElementById('waslaAdminSidebar').getBoundingClientRect();
          return document.body.classList.contains('sidebar-open') && b.left >= -1 && b.right <= innerWidth + 1
            && document.getElementById('waslaAdminNavToggle').getAttribute('aria-expanded') === 'true'; })()
        """;

    private const string IsClosed = """
        (() => { const b = document.getElementById('waslaAdminSidebar').getBoundingClientRect();
          return !document.body.classList.contains('sidebar-open') && (b.right <= 1 || b.left >= innerWidth - 1)
            && document.getElementById('waslaAdminNavToggle').getAttribute('aria-expanded') === 'false'; })()
        """;

    private const string DefinitionLists = """
        (() => [...document.querySelectorAll('.app-main dl.row > dd')].map(dd => {
          const card = (dd.closest('.card-body') || dd.closest('.card') || dd.closest('main')).getBoundingClientRect();
          const b = dd.getBoundingClientRect();
          const cs = getComputedStyle(dd);
          return { text: dd.textContent.trim().replace(/\s+/g, ' ').slice(0, 60), left: b.left, right: b.right, cardLeft: card.left, cardRight: card.right,
            scrollWidth: dd.scrollWidth, clientWidth: dd.clientWidth, height: b.height, lineHeight: parseFloat(cs.lineHeight) || 20,
            marginLeft: parseFloat(cs.marginLeft), marginRight: parseFloat(cs.marginRight) };
        }))()
        """;

    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;
    private Guid _tenantId;
    private Guid _registrationId;

    public async ValueTask InitializeAsync()
    {
        var tenant = _central.AddTenant(LongSlug, name: LongName, planCode: "Pro");
        _tenantId = tenant.Id;
        _central.AddDevice(tenant.Id, "Kitchen", true, DateTime.UtcNow);
        _registrationId = _central.AddRegistration(LongRegistrationSlug, Wasla.Domain.Enums.PendingRegistrationStatus.PaymentSucceeded,
            paymentSucceededAt: DateTime.UtcNow.AddHours(-2)).Id;
        _tenants.CreateTenantDatabase(tenant.Id);
        _tenants.Seed(tenant.Id, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));
        _host = await AdminWebHost.StartAsync(_central, _tenants);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    public static TheoryData<string, string, int> MobileCases()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (culture, direction) in new[] { ("en-US", "ltr"), ("ar-SA", "rtl") })
            foreach (var width in new[] { 375, 390, 440, 820 })
                data.Add(culture, direction, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(MobileCases))]
    public async Task MobileDrawer_OpensFromTheInlineStartSide_AndClosesEveryWay(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);
        await browser.SetViewportAsync(width, 860, mobile: true, ct);
        var page = $"{width}px ({culture})";
        await LoadAsync(browser, $"/admin/customers/{_tenantId}", ct);

        // Closed: an accessible toggle at the inline-start of the bar, next to the compact logo; the drawer is
        // off-canvas and out of the focus order; the header controls stay.
        var closed = await StateAsync(browser, ct);
        Assert.True(closed.GetProperty("toggleShown").GetBoolean(), $"{page}: no visible navigation toggle");
        Assert.Equal("sidebar", closed.GetProperty("toggleLteOwned").GetString());
        Assert.Equal("waslaAdminSidebar", closed.GetProperty("toggleControls").GetString());
        Assert.Equal("false", closed.GetProperty("toggleExpanded").GetString());
        Assert.False(string.IsNullOrWhiteSpace(closed.GetProperty("toggleName").GetString()));
        Assert.True(closed.GetProperty("logoShown").GetBoolean(), $"{page}: the compact logo is missing");
        var toggle = closed.GetProperty("toggle");
        var logo = closed.GetProperty("logo");
        Assert.InRange(Math.Abs(Center(toggle, "top", "bottom") - Center(logo, "top", "bottom")), 0, 8);
        if (direction == "ltr")
            Assert.True(toggle.GetProperty("right").GetDouble() <= logo.GetProperty("left").GetDouble() + 1, $"{page}: toggle is not before the logo");
        else
            Assert.True(toggle.GetProperty("left").GetDouble() >= logo.GetProperty("right").GetDouble() - 1, $"{page}: toggle is not before the logo in RTL");
        Assert.Equal(3, closed.GetProperty("headerControls").GetInt32());
        Assert.True(closed.GetProperty("sidebarInert").GetBoolean(), $"{page}: the off-canvas drawer can take focus");
        Assert.False(closed.GetProperty("mainInert").GetBoolean());
        AssertNoHorizontalOverflow(closed, page);

        // Open with a real click: the drawer slides in from the inline-start edge, the backdrop covers the viewport,
        // focus moves to the drawer's close button and everything behind it is inert and does not scroll.
        await ClickCenterAsync(browser, toggle, ct);
        Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct), $"{page}: the drawer did not open");
        await SettleAsync(browser, ct);
        var open = await StateAsync(browser, ct);
        AssertOpenDrawer(open, direction, width, page);
        Assert.Equal("close", open.GetProperty("active").GetString());
        Assert.False(string.IsNullOrWhiteSpace(open.GetProperty("closeName").GetString()));
        await browser.WheelAsync(width / 2.0, 400, 700, ct);
        await Task.Delay(300, ct);
        Assert.Equal(0, (await StateAsync(browser, ct)).GetProperty("scrollY").GetDouble());

        // Escape closes it and returns focus to the toggle.
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await AssertClosedWithFocusOnToggleAsync(browser, page + " Escape", ct);

        // Keyboard: Enter on the toggle opens it; the close button closes it.
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct), $"{page}: Enter did not open the drawer");
        await SettleAsync(browser, ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await AssertClosedWithFocusOnToggleAsync(browser, page + " close button", ct);

        // A click on the backdrop beside the drawer closes it.
        await ClickCenterAsync(browser, toggle, ct);
        Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct));
        await SettleAsync(browser, ct);
        var outsideX = direction == "ltr" ? width - 12 : 12;
        await browser.ClickAtAsync(outsideX, 430, ct);
        await AssertClosedWithFocusOnToggleAsync(browser, page + " backdrop", ct);

        // Closed again, the page scrolls normally.
        await browser.WheelAsync(width / 2.0, 400, 700, ct);
        Assert.True(await browser.WaitForAsync("window.scrollY > 0", TimeSpan.FromSeconds(3), ct), $"{page}: the page did not scroll once the drawer closed");
        await browser.EvaluateAsync<bool>("(() => { window.scrollTo({ top: 0, behavior: 'instant' }); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("window.scrollY === 0", TimeSpan.FromSeconds(3), ct));

        // Repeated opening and closing stays consistent: one backdrop, one state, matching aria-expanded.
        for (var i = 0; i < 4; i++)
        {
            await ClickCenterAsync(browser, toggle, ct);
            Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct), $"{page}: cycle {i} did not open");
            await browser.PressKeyAsync("Escape", shift: false, ct);
            Assert.True(await browser.WaitForAsync(IsClosed, TimeSpan.FromSeconds(5), ct), $"{page}: cycle {i} did not close");
        }

        var after = await StateAsync(browser, ct);
        Assert.Equal(1, after.GetProperty("overlays").GetInt32());
        Assert.False(after.GetProperty("open").GetBoolean() && after.GetProperty("collapsed").GetBoolean());
        Assert.NotEqual("hidden", after.GetProperty("bodyOverflow").GetString());
        AssertNoHorizontalOverflow(after, page);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public async Task MobileDrawer_KeepsKeyboardFocusInsideWhileOpen_AndANavigationLinkClosesIt(string culture)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);
        await browser.SetViewportAsync(375, 812, mobile: true, ct);
        await LoadAsync(browser, $"/admin/customers/{_tenantId}", ct);

        await ClickCenterAsync(browser, (await StateAsync(browser, ct)).GetProperty("toggle"), ct);
        Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct));
        await SettleAsync(browser, ct);

        // Forward and backward through the drawer: focus never reaches the inert header or page content.
        foreach (var shift in new[] { false, true })
        {
            for (var i = 0; i < 9; i++)
            {
                await browser.PressKeyAsync("Tab", shift, ct);
                var active = (await StateAsync(browser, ct)).GetProperty("active").GetString();
                Assert.True(active is not ("header" or "main"), $"Tab {(shift ? "backward" : "forward")} {i}: focus reached '{active}' behind the open drawer");
            }
        }

        // Following a drawer link closes the drawer; the new page opens with it closed.
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('#waslaAdminSidebar a.nav-link[href=\"/admin/pending-registrations\"]').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("location.pathname === '/admin/pending-registrations' && document.readyState === 'complete' && !!document.getElementById('live-region')", TimeSpan.FromSeconds(15), ct));
        Assert.True(await browser.WaitForAsync(IsClosed, TimeSpan.FromSeconds(5), ct));
        Assert.True((await StateAsync(browser, ct)).GetProperty("sidebarInert").GetBoolean());
    }

    [Theory]
    [InlineData("en-US", "ltr", 1024)]
    [InlineData("en-US", "ltr", 1366)]
    [InlineData("ar-SA", "rtl", 1024)]
    [InlineData("ar-SA", "rtl", 1366)]
    public async Task Desktop_KeepsTheExpandedSidebar_WithoutMobileControlsOrInertContent(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);
        await browser.SetViewportAsync(width, 860, mobile: false, ct);
        var page = $"{width}px ({culture})";
        await LoadAsync(browser, $"/admin/customers/{_tenantId}", ct);

        var s = await StateAsync(browser, ct);
        Assert.False(s.GetProperty("toggleShown").GetBoolean(), $"{page}: the mobile toggle shows on desktop");
        Assert.False(s.GetProperty("closeShown").GetBoolean(), $"{page}: the drawer close button shows on desktop");
        Assert.False(s.GetProperty("logoShown").GetBoolean(), $"{page}: the compact header logo shows on desktop");
        foreach (var flag in new[] { "overlayVisible", "sidebarInert", "headerInert", "mainInert" })
            Assert.False(s.GetProperty(flag).GetBoolean(), $"{page}: {flag} on desktop");
        Assert.NotEqual("hidden", s.GetProperty("bodyOverflow").GetString());
        Assert.NotEqual("hidden", s.GetProperty("rootOverflow").GetString());
        Assert.NotEqual("hidden", s.GetProperty("rootOverflow").GetString());
        Assert.Equal(3, s.GetProperty("headerControls").GetInt32());
        var sidebar = s.GetProperty("sidebar");
        // At the inline-start edge of the page. A desktop scrollbar narrows the page, so RTL measures to its client width.
        if (direction == "ltr")
            Assert.InRange(sidebar.GetProperty("left").GetDouble(), -1, 1);
        else
            Assert.InRange(sidebar.GetProperty("right").GetDouble(), s.GetProperty("clientWidth").GetDouble() - 1, width + 1);
        Assert.True(sidebar.GetProperty("width").GetDouble() > 200);
        AssertNoHorizontalOverflow(s, page);

        // Escape is not a desktop sidebar control.
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await Task.Delay(400, ct);
        var afterEscape = (await StateAsync(browser, ct)).GetProperty("sidebar");
        Assert.InRange(afterEscape.GetProperty("left").GetDouble(), sidebar.GetProperty("left").GetDouble() - 1, sidebar.GetProperty("left").GetDouble() + 1);
    }

    [Theory]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task MobileDrawer_WorksTheSameInDarkMode(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);
        await browser.SetViewportAsync(375, 812, mobile: true, ct);
        await LoadAsync(browser, "/admin", ct);
        await browser.EvaluateAsync<bool>("(() => { localStorage.setItem('Wasla.theme', 'dark'); return true; })()", ct);
        await LoadAsync(browser, $"/admin/customers/{_tenantId}", ct);
        Assert.Equal("dark", await browser.EvaluateAsync<string>("document.documentElement.getAttribute('data-bs-theme')", ct));

        await ClickCenterAsync(browser, (await StateAsync(browser, ct)).GetProperty("toggle"), ct);
        Assert.True(await browser.WaitForAsync(IsOpen, TimeSpan.FromSeconds(5), ct));
        await SettleAsync(browser, ct);
        AssertOpenDrawer(await StateAsync(browser, ct), direction, 375, $"dark {culture}");
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await AssertClosedWithFocusOnToggleAsync(browser, $"dark {culture}", ct);
        AssertDefinitionListsFit(await browser.EvaluateAsync<JsonElement>(DefinitionLists, ct), direction, $"dark {culture}");
    }

    public static TheoryData<string, string, int> DetailCases()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (culture, direction) in new[] { ("ar-SA", "rtl"), ("en-US", "ltr") })
            foreach (var width in new[] { 375, 390, 440 })
                data.Add(culture, direction, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(DetailCases))]
    public async Task DetailDefinitionLists_StayInsideTheirCards_AndWrapLongValues(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);
        await browser.SetViewportAsync(width, 860, mobile: true, ct);

        foreach (var path in new[] { $"/admin/customers/{_tenantId}", $"/admin/pending-registrations/{_registrationId}" })
        {
            var page = $"{path} at {width}px ({culture})";
            await LoadAsync(browser, path, ct);
            var lists = await browser.EvaluateAsync<JsonElement>(DefinitionLists, ct);
            Assert.True(lists.GetArrayLength() >= 8, $"{page}: expected the detail definition lists");
            AssertDefinitionListsFit(lists, direction, page);
            AssertNoHorizontalOverflow(await StateAsync(browser, ct), page);

            // The long, unbroken values wrap onto several lines instead of being cut off.
            var slug = path.Contains("customers", StringComparison.Ordinal) ? LongSlug : LongRegistrationSlug;
            var longValues = lists.EnumerateArray().Where(dd => dd.GetProperty("text").GetString()!.Contains(slug[..20], StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(longValues);
            Assert.All(longValues, dd => Assert.True(dd.GetProperty("height").GetDouble() > dd.GetProperty("lineHeight").GetDouble() * 1.5,
                $"{page}: '{dd.GetProperty("text")}' did not wrap"));
        }
    }

    [Fact]
    public async Task WithThePreviousDefinitionListMargins_ArabicValuesAreClipped_SoTheCheckIsMeaningful()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync("ar-SA", ct);
        await browser.SetViewportAsync(375, 812, mobile: true, ct);
        await LoadAsync(browser, $"/admin/customers/{_tenantId}", ct);

        // Targeted mutation of the fix: restore the browser's default dd start margin and normal wrapping.
        await browser.EvaluateAsync<bool>(
            "(async () => { const s = document.createElement('style'); s.textContent = '.app-main dl.row > dd { margin-inline-start: 40px !important; overflow-wrap: normal !important; }'; document.head.appendChild(s); await new Promise(r => setTimeout(r, 200)); return true; })()",
            ct);

        var lists = await browser.EvaluateAsync<JsonElement>(DefinitionLists, ct);
        var outside = lists.EnumerateArray().Count(dd => dd.GetProperty("left").GetDouble() < dd.GetProperty("cardLeft").GetDouble() - 1
                                                         || dd.GetProperty("right").GetDouble() > dd.GetProperty("cardRight").GetDouble() + 1
                                                         || dd.GetProperty("scrollWidth").GetInt32() > dd.GetProperty("clientWidth").GetInt32() + 1);
        Assert.True(outside > 0, "Expected the previous margins to push Arabic values outside their cards.");
    }

    private static void AssertOpenDrawer(JsonElement s, string direction, int width, string page)
    {
        var sidebar = s.GetProperty("sidebar");
        Assert.Equal("true", s.GetProperty("toggleExpanded").GetString());
        Assert.False(s.GetProperty("sidebarInert").GetBoolean());
        Assert.True(s.GetProperty("headerInert").GetBoolean(), $"{page}: the header stays reachable behind the drawer");
        Assert.True(s.GetProperty("mainInert").GetBoolean(), $"{page}: the page stays reachable behind the drawer");
        Assert.Equal("hidden", s.GetProperty("bodyOverflow").GetString());
        Assert.Equal("hidden", s.GetProperty("rootOverflow").GetString());
        if (direction == "ltr")
            Assert.InRange(sidebar.GetProperty("left").GetDouble(), -1, 1);
        else
            Assert.InRange(sidebar.GetProperty("right").GetDouble(), width - 1, width + 1);
        Assert.True(sidebar.GetProperty("width").GetDouble() < width, $"{page}: the drawer leaves no backdrop to tap");
        Assert.True(s.GetProperty("overlayVisible").GetBoolean(), $"{page}: no backdrop");
        var overlay = s.GetProperty("overlay");
        Assert.InRange(overlay.GetProperty("left").GetDouble(), -1, 1);
        Assert.InRange(overlay.GetProperty("top").GetDouble(), -1, 1);
        Assert.True(overlay.GetProperty("right").GetDouble() >= width - 1 && overlay.GetProperty("bottom").GetDouble() >= s.GetProperty("height").GetDouble() - 1,
            $"{page}: the backdrop does not cover the viewport");
        Assert.Equal(1, s.GetProperty("overlays").GetInt32());
        AssertNoHorizontalOverflow(s, page);
    }

    private static void AssertDefinitionListsFit(JsonElement lists, string direction, string page)
    {
        foreach (var dd in lists.EnumerateArray())
        {
            var text = dd.GetProperty("text").GetString();
            Assert.True(dd.GetProperty("left").GetDouble() >= dd.GetProperty("cardLeft").GetDouble() - 1
                        && dd.GetProperty("right").GetDouble() <= dd.GetProperty("cardRight").GetDouble() + 1,
                $"{page}: '{text}' {dd.GetProperty("left")}..{dd.GetProperty("right")} leaves its card {dd.GetProperty("cardLeft")}..{dd.GetProperty("cardRight")}");
            Assert.True(dd.GetProperty("scrollWidth").GetInt32() <= dd.GetProperty("clientWidth").GetInt32() + 1, $"{page}: '{text}' is clipped");
            // No start margin in either direction.
            Assert.InRange(direction == "rtl" ? dd.GetProperty("marginRight").GetDouble() : dd.GetProperty("marginLeft").GetDouble(), -0.5, 0.5);
        }
    }

    private static void AssertNoHorizontalOverflow(JsonElement s, string page) =>
        Assert.True(s.GetProperty("scrollWidth").GetInt32() <= s.GetProperty("clientWidth").GetInt32() + 1,
            $"{page}: document {s.GetProperty("scrollWidth")}px wide in a {s.GetProperty("clientWidth")}px viewport");

    private static async Task AssertClosedWithFocusOnToggleAsync(HeadlessChromium browser, string page, CancellationToken ct)
    {
        Assert.True(await browser.WaitForAsync(IsClosed, TimeSpan.FromSeconds(5), ct), $"{page}: the drawer did not close");
        await SettleAsync(browser, ct);
        var s = await StateAsync(browser, ct);
        Assert.Equal("toggle", s.GetProperty("active").GetString());
        Assert.True(s.GetProperty("sidebarInert").GetBoolean());
        Assert.False(s.GetProperty("headerInert").GetBoolean());
        Assert.False(s.GetProperty("mainInert").GetBoolean());
        Assert.False(s.GetProperty("overlayVisible").GetBoolean());
        Assert.NotEqual("hidden", s.GetProperty("bodyOverflow").GetString());
        Assert.NotEqual("hidden", s.GetProperty("rootOverflow").GetString());
    }

    private static double Center(JsonElement rect, string start, string end) =>
        (rect.GetProperty(start).GetDouble() + rect.GetProperty(end).GetDouble()) / 2;

    private static Task ClickCenterAsync(HeadlessChromium browser, JsonElement rect, CancellationToken ct) =>
        browser.ClickAtAsync(Center(rect, "left", "right"), Center(rect, "top", "bottom"), ct);

    /// <summary>Waits for AdminLTE's sidebar transition to finish.</summary>
    private static Task SettleAsync(HeadlessChromium browser, CancellationToken ct) => Task.Delay(450, ct);

    private static Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>(State, ct);

    private async Task<HeadlessChromium> SignedInBrowserAsync(string culture, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser layout checks.");

        using var session = await _host.SignedInAdminAsync(culture);
        var browser = await HeadlessChromium.StartAsync(executable, ct);
        foreach (var cookie in session.Cookies)
            await browser.SetCookieAsync(_host.BaseAddress, cookie.Name, cookie.Value, ct);
        return browser;
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layout loads Bootstrap and AdminLTE from their CDN; a slow CDN response can hold the load event past the
        // driver's timeout. One retry absorbs that without relaxing any layout assertion.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("!!document.getElementById('live-region')", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("AdminLTE did not load (its CDN is unreachable), so the Admin layout cannot be exercised.");
        // The navigation script initialises right after AdminLTE; its absence is a failure, not a skip.
        await browser.WaitForAsync("document.documentElement.getAttribute('data-wasla-admin-nav') === 'ready'", TimeSpan.FromSeconds(3), ct);
        await SettleAsync(browser, ct);
    }
}
