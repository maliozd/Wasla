using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The theme preference in a real headless Chromium browser (Edge or Chrome) against the real tenant and Central Admin
/// layouts served from one origin: system defaults, explicit choices across navigation and reload, a system change
/// after a choice, tenant/Admin isolation, the first painted frame, and the toggles on mobile and desktop in English
/// LTR and Arabic RTL. Real mouse input is used for every toggle. Skips only when no Chromium browser is installed
/// (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap/AdminLTE cannot be loaded from their CDN.
/// </summary>
public sealed class ThemePreferenceBrowserTests : IAsyncLifetime
{
    private const string TenantKey = "Wasla.tenant.theme";
    private const string AdminKey = "Wasla.theme";

    private const string State = """
        (() => {
          const html = document.documentElement;
          const shown = e => !!e && e.getBoundingClientRect().width > 0 && getComputedStyle(e).visibility !== 'hidden';
          const toggle = [...document.querySelectorAll('.wasla-theme-toggle')].find(shown) || null;
          const r = toggle ? toggle.getBoundingClientRect() : null;
          const hit = r ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) : null;
          return {
            html: html.getAttribute('data-bs-theme'), body: document.body.getAttribute('data-bs-theme'),
            scope: html.getAttribute('data-wasla-theme-scope'), dir: html.getAttribute('dir'),
            source: window.WaslaTheme ? window.WaslaTheme.current().source : null,
            tenantKey: localStorage.getItem('Wasla.tenant.theme'), adminKey: localStorage.getItem('Wasla.theme'),
            toggleShown: !!toggle, pressed: toggle ? toggle.getAttribute('aria-pressed') : null,
            toggleTitle: toggle ? toggle.getAttribute('title') : null,
            moonShown: shown(toggle && toggle.querySelector('.wasla-theme-moon')), sunShown: shown(toggle && toggle.querySelector('.wasla-theme-sun')),
            toggle: r ? { left: r.left, right: r.right, top: r.top, bottom: r.bottom } : null,
            toggleHit: !!(toggle && hit && toggle.contains(hit)),
            bodyBg: getComputedStyle(document.body).backgroundColor,
            width: innerWidth, scrollWidth: html.scrollWidth, clientWidth: html.clientWidth
          };
        })()
        """;

    // Runs in every document before the page's own scripts. Records the <html> theme in the first rendered frame, what the
    // first frame with a <body> shows (rAF callbacks run just before a frame is painted), and every later change of the
    // theme attribute on <html> or <body>.
    private const string FirstFrameProbe = """
        (() => {
          const log = { first: null, firstWithBody: null, changes: [] };
          window.__waslaThemeProbe = log;
          const snapshot = () => ({
            html: document.documentElement.getAttribute('data-bs-theme'),
            body: document.body ? document.body.getAttribute('data-bs-theme') : null,
            bodyBg: document.body ? getComputedStyle(document.body).backgroundColor : null
          });
          const frame = () => {
            if (!log.first) {
              log.first = snapshot();
              new MutationObserver(records => records.forEach(m => {
                const value = m.target.getAttribute('data-bs-theme');
                if (value !== m.oldValue) log.changes.push(m.target.nodeName + ': ' + m.oldValue + ' -> ' + value);
              })).observe(document.documentElement, { attributes: true, subtree: true, attributeOldValue: true, attributeFilter: ['data-bs-theme'] });
            }
            if (document.body) log.firstWithBody = snapshot();
            else requestAnimationFrame(frame);
          };
          requestAnimationFrame(frame);
        })();
        """;

    private ThemeWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await ThemeWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task WithoutAChoice_TheSystemThemeApplies_AndLaterSystemChangesFollowIt(string system)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);
        await browser.SetSystemColorSchemeAsync(system, ct);
        await LoadTenantAsync(browser, "/help", ct);

        var state = await StateAsync(browser, ct);
        Assert.Equal("tenant", state.GetProperty("scope").GetString());
        AssertTheme(state, system, "system", "no choice");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("tenantKey").ValueKind);
        AssertToggleShows(state, system);

        // The open page follows the system while no choice exists, and nothing is stored.
        var other = system == "dark" ? "light" : "dark";
        await browser.SetSystemColorSchemeAsync(other, ct);
        Assert.True(await browser.WaitForAsync($"document.documentElement.getAttribute('data-bs-theme') === '{other}'", TimeSpan.FromSeconds(5), ct));
        state = await StateAsync(browser, ct);
        AssertTheme(state, other, "system", "after a system change");
        AssertToggleShows(state, other);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("tenantKey").ValueKind);

        await ReloadAsync(browser, ct);
        AssertTheme(await StateAsync(browser, ct), other, "system", "after reload");
    }

    [Theory]
    [InlineData("dark", "light")]
    [InlineData("light", "dark")]
    public async Task AnExplicitChoice_SurvivesNavigationAndReload_AndASystemChangeDoesNotOverwriteIt(string choice, string system)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);
        await browser.SetSystemColorSchemeAsync(system, ct);
        await LoadTenantAsync(browser, "/help", ct);
        AssertTheme(await StateAsync(browser, ct), system, "system", "before the choice");

        // A real click on the sidebar toggle shows the opposite of the system theme: the user's choice.
        await ClickToggleAsync(browser, ct);
        var chosen = await StateAsync(browser, ct);
        AssertTheme(chosen, choice, "explicit", "after the click");
        Assert.Equal(choice, chosen.GetProperty("tenantKey").GetString());
        AssertToggleShows(chosen, choice);

        // The system theme later switches to the chosen one and back: the choice stays.
        await browser.SetSystemColorSchemeAsync(choice, ct);
        await browser.SetSystemColorSchemeAsync(system, ct);
        await Task.Delay(300, ct);
        AssertTheme(await StateAsync(browser, ct), choice, "explicit", "after system changes");

        // Another tenant page, a reload, and back.
        await LoadTenantAsync(browser, "/auth/access-denied", ct);
        AssertTheme(await StateAsync(browser, ct), choice, "explicit", "on another page");
        await ReloadAsync(browser, ct);
        AssertTheme(await StateAsync(browser, ct), choice, "explicit", "after reload");
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/help?again=1"), ct);
        var back = await StateAsync(browser, ct);
        AssertTheme(back, choice, "explicit", "back on Help");
        AssertToggleShows(back, choice);
        Assert.Equal(JsonValueKind.Null, back.GetProperty("adminKey").ValueKind);
    }

    [Fact]
    public async Task TenantAndCentralAdmin_OnOneOrigin_KeepSeparateChoices()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);
        await browser.SetSystemColorSchemeAsync("light", ct);

        await LoadTenantAsync(browser, "/help", ct);
        await ClickToggleAsync(browser, ct);
        AssertTheme(await StateAsync(browser, ct), "dark", "explicit", "tenant choice");

        // Central Admin on the same origin does not see the tenant choice and keeps its own.
        await LoadAdminAsync(browser, "/admin", ct);
        var admin = await StateAsync(browser, ct);
        Assert.Equal("admin", admin.GetProperty("scope").GetString());
        AssertTheme(admin, "light", "default", "Admin before its own choice");
        await ClickToggleAsync(browser, ct);
        admin = await StateAsync(browser, ct);
        AssertTheme(admin, "dark", "explicit", "Admin choice");
        Assert.Equal("dark", admin.GetProperty("adminKey").GetString());
        Assert.Equal("dark", admin.GetProperty("tenantKey").GetString());

        // Changing one app's choice leaves the other's alone, in both directions.
        await ClickToggleAsync(browser, ct);
        Assert.Equal("light", (await StateAsync(browser, ct)).GetProperty("adminKey").GetString());
        await LoadTenantAsync(browser, "/help?from=admin", ct);
        var tenant = await StateAsync(browser, ct);
        AssertTheme(tenant, "dark", "explicit", "tenant after the Admin change");
        Assert.Equal("light", tenant.GetProperty("adminKey").GetString());

        await ClickToggleAsync(browser, ct);
        Assert.Equal("light", (await StateAsync(browser, ct)).GetProperty("tenantKey").GetString());
        await browser.EvaluateAsync<bool>("(() => { localStorage.setItem('Wasla.theme', 'dark'); return true; })()", ct);
        await LoadAdminAsync(browser, "/admin?from=tenant", ct);
        AssertTheme(await StateAsync(browser, ct), "dark", "explicit", "Admin after the tenant change");

        // Central Admin does not follow the system theme (its existing behavior).
        await browser.EvaluateAsync<bool>("(() => { localStorage.removeItem('Wasla.theme'); return true; })()", ct);
        await browser.SetSystemColorSchemeAsync("dark", ct);
        await LoadAdminAsync(browser, "/admin?system=dark", ct);
        AssertTheme(await StateAsync(browser, ct), "light", "default", "Admin with a dark system and no choice");
    }

    public static TheoryData<string, string, string?, string> FirstFrameCases() => new()
    {
        // page, system theme, stored tenant/Admin choice (null = none), expected theme
        { "/help", "dark", null, "dark" },
        { "/help", "light", null, "light" },
        { "/help", "light", "dark", "dark" },
        { "/help", "dark", "light", "light" },
        { "/orders/live-display", "dark", null, "dark" },
        { "/orders/live-display", "light", "dark", "dark" },
        { "/orders/live-display", "dark", "light", "light" },
        { "/admin", "light", "dark", "dark" },
        { "/admin", "dark", null, "light" }
    };

    [Fact]
    public async Task TheLiveScreen_FollowsTheTenantPreference_IncludingAChangeMadeInAnotherPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);
        await browser.SetSystemColorSchemeAsync("dark", ct);

        // No choice: the system theme, on <html> and <body> alike (the body used to stay light here).
        await LoadTenantAsync(browser, "/orders/live-display", ct);
        var live = await StateAsync(browser, ct);
        Assert.Equal("tenant", live.GetProperty("scope").GetString());
        AssertTheme(live, "dark", "system", "Live Screen without a choice");
        var darkBackground = live.GetProperty("bodyBg").GetString();

        // A choice made on another tenant page applies when the Live Screen opens.
        await LoadTenantAsync(browser, "/help", ct);
        await ClickToggleAsync(browser, ct);
        AssertTheme(await StateAsync(browser, ct), "light", "explicit", "Help");
        await LoadTenantAsync(browser, "/orders/live-display?after=choice", ct);
        live = await StateAsync(browser, ct);
        AssertTheme(live, "light", "explicit", "Live Screen after the choice");
        Assert.NotEqual(darkBackground, live.GetProperty("bodyBg").GetString());

        // An open Live Screen follows a choice made in another document of the app (as in another tab).
        await browser.EvaluateAsync<bool>("""
            new Promise(resolve => {
              const frame = document.createElement('iframe');
              frame.src = '/help?frame=1';
              frame.style.cssText = 'position:absolute;inline-size:1px;block-size:1px;opacity:0';
              frame.onload = () => { frame.contentWindow.WaslaTheme.choose('dark'); resolve(true); };
              document.body.appendChild(frame);
            })
            """, ct);
        Assert.True(await browser.WaitForAsync("document.body.getAttribute('data-bs-theme') === 'dark'", TimeSpan.FromSeconds(5), ct),
            "The open Live Screen did not follow the choice made in another page.");
        live = await StateAsync(browser, ct);
        AssertTheme(live, "dark", "explicit", "open Live Screen");
        Assert.Equal(darkBackground, live.GetProperty("bodyBg").GetString());
    }

    [Theory]
    [MemberData(nameof(FirstFrameCases))]
    public async Task TheFirstFrame_IsPaintedInTheResolvedTheme_WithNoLaterSwitch(string path, string system, string? stored, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);
        await browser.SetSystemColorSchemeAsync(system, ct);
        var isAdmin = path.StartsWith("/admin", StringComparison.Ordinal);
        var key = isAdmin ? AdminKey : TenantKey;

        // Store the choice from a page of this origin first, then load the page under test fresh.
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
        var store = stored is null ? $"localStorage.removeItem('{key}')" : $"localStorage.setItem('{key}', '{stored}')";
        await browser.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); {store}; return true; }})()", ct);
        await browser.AddScriptBeforePageScriptsAsync(FirstFrameProbe, ct);
        if (isAdmin)
            await LoadAdminAsync(browser, path, ct);
        else
            await LoadTenantAsync(browser, path, ct);
        await Task.Delay(300, ct);

        var probe = await browser.EvaluateAsync<JsonElement>("window.__waslaThemeProbe", ct);
        var final = await StateAsync(browser, ct);
        var page = $"{path} system={system} stored={stored ?? "none"}";
        Assert.Equal(expected, final.GetProperty("html").GetString());
        Assert.Equal(expected, final.GetProperty("body").GetString());

        var first = probe.GetProperty("first");
        Assert.True(first.ValueKind == JsonValueKind.Object, $"{page}: no frame was recorded");
        Assert.Equal(expected, first.GetProperty("html").GetString());
        var withBody = probe.GetProperty("firstWithBody");
        Assert.True(withBody.ValueKind == JsonValueKind.Object, $"{page}: no frame with a body was recorded");
        Assert.True(expected == withBody.GetProperty("body").GetString(), $"{page}: the first frame painted <body> as {withBody.GetProperty("body")}");
        Assert.Equal(final.GetProperty("bodyBg").GetString(), withBody.GetProperty("bodyBg").GetString());

        Assert.True(probe.GetProperty("changes").GetArrayLength() == 0,
            $"{page}: the theme changed after the first frame: {probe.GetProperty("changes")}");
    }

    public static TheoryData<string, string, int> LayoutCases()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (culture, direction) in new[] { ("en-US", "ltr"), ("ar-SA", "rtl") })
            foreach (var width in new[] { 1280, 390 })
                data.Add(culture, direction, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public async Task TheTenantToggle_WorksOnMobileAndDesktop_InLtrAndRtl(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var mobile = width < 992;
        await using var browser = await BrowserAsync(culture, ct);
        await browser.SetViewportAsync(width, 860, mobile, ct);
        await browser.SetSystemColorSchemeAsync("light", ct);
        await LoadTenantAsync(browser, "/help", ct);
        var page = $"tenant {width}px {culture}";

        var state = await StateAsync(browser, ct);
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        AssertNoHorizontalOverflow(state, page);

        // Below lg the toggle lives in the off-canvas navigation: open it with the real trigger first.
        if (mobile)
        {
            Assert.False(state.GetProperty("toggleShown").GetBoolean() && state.GetProperty("toggleHit").GetBoolean(),
                $"{page}: the toggle is clickable while the navigation is closed");
            await ClickElementAsync(browser, "#tenantNavOpen", ct);
            Assert.True(await browser.WaitForAsync("document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: navigation did not open");
            await Task.Delay(400, ct);
        }

        foreach (var expected in new[] { "dark", "light", "dark" })
        {
            await ClickToggleAsync(browser, ct);
            state = await StateAsync(browser, ct);
            AssertTheme(state, expected, "explicit", page);
            AssertToggleShows(state, expected);
            AssertToggleInsideViewport(state, page);
            AssertNoHorizontalOverflow(state, page);
            Assert.Equal(direction, state.GetProperty("dir").GetString());
        }

        if (mobile)
        {
            // The drawer still closes normally and the theme stays.
            await browser.PressKeyAsync("Escape", shift: false, ct);
            Assert.True(await browser.WaitForAsync("!document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: navigation did not close");
        }

        await ReloadAsync(browser, ct);
        state = await StateAsync(browser, ct);
        AssertTheme(state, "dark", "explicit", page + " after reload");
        AssertNoHorizontalOverflow(state, page + " after reload");
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public async Task TheAdminToggle_WorksOnMobileAndDesktop_InLtrAndRtl(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, ct);
        await browser.SetViewportAsync(width, 860, width < 992, ct);
        await LoadAdminAsync(browser, "/admin", ct);
        var page = $"Admin {width}px {culture}";

        var state = await StateAsync(browser, ct);
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        AssertTheme(state, "light", "default", page);
        foreach (var expected in new[] { "dark", "light" })
        {
            await ClickToggleAsync(browser, ct);
            state = await StateAsync(browser, ct);
            AssertTheme(state, expected, "explicit", page);
            AssertToggleShows(state, expected);
            AssertToggleInsideViewport(state, page);
            AssertNoHorizontalOverflow(state, page);
            Assert.Equal(JsonValueKind.Null, state.GetProperty("tenantKey").ValueKind);
        }
    }

    [Theory]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task TheTenantSignInPage_StaysLight_AndLeavesTheChoiceAlone(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, ct, signedIn: false);
        await browser.SetSystemColorSchemeAsync("dark", ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
        await browser.EvaluateAsync<bool>("(() => { localStorage.setItem('Wasla.tenant.theme', 'dark'); return true; })()", ct);

        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/auth/login"), ct);
        var state = await browser.EvaluateAsync<JsonElement>("""
            ({ html: document.documentElement.getAttribute('data-bs-theme'), body: document.body.getAttribute('data-bs-theme'),
               dir: document.documentElement.getAttribute('dir'), scope: document.documentElement.getAttribute('data-wasla-theme-scope'),
               stored: localStorage.getItem('Wasla.tenant.theme'), hasForm: !!document.querySelector('form[action="/auth/login"]') })
            """, ct);
        Assert.True(state.GetProperty("hasForm").GetBoolean());
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        // The sign-in pages have no dark styles: they are not part of the themed shell.
        Assert.Equal(JsonValueKind.Null, state.GetProperty("html").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("body").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("scope").ValueKind);
        Assert.Equal("dark", state.GetProperty("stored").GetString());
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, CancellationToken ct, bool signedIn = true)
    {
        var browser = await _host.BrowserAsync(culture, signedIn, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser theme checks.");
        return browser;
    }

    private async Task LoadTenantAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await NavigateWithRetryAsync(browser, path, ct);
        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
        await Task.Delay(150, ct);
    }

    private async Task LoadAdminAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await NavigateWithRetryAsync(browser, path, ct);
        if (!await browser.WaitForAsync("!!document.getElementById('live-region')", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("AdminLTE did not load (its CDN is unreachable), so the Admin layout cannot be exercised.");
        await browser.WaitForAsync("document.documentElement.getAttribute('data-wasla-admin-nav') === 'ready'", TimeSpan.FromSeconds(3), ct);
        await Task.Delay(150, ct);
    }

    private async Task NavigateWithRetryAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layouts load Bootstrap (and AdminLTE) from their CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
    }

    private static async Task ReloadAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>("(() => { window.__waslaBeforeReload = true; setTimeout(() => location.reload(), 0); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!window.__waslaBeforeReload && document.readyState === 'complete'", TimeSpan.FromSeconds(30), ct), "The page did not reload.");
        await Task.Delay(300, ct);
    }

    private static async Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>(State, ct);

    private static async Task ClickToggleAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var before = await StateAsync(browser, ct);
        Assert.True(before.GetProperty("toggleShown").GetBoolean(), "No visible theme toggle.");
        Assert.True(before.GetProperty("toggleHit").GetBoolean(), "The theme toggle is covered by another element.");
        var r = before.GetProperty("toggle");
        await browser.ClickAtAsync(
            (r.GetProperty("left").GetDouble() + r.GetProperty("right").GetDouble()) / 2,
            (r.GetProperty("top").GetDouble() + r.GetProperty("bottom").GetDouble()) / 2, ct);
        var next = before.GetProperty("html").GetString() == "dark" ? "light" : "dark";
        Assert.True(await browser.WaitForAsync($"document.documentElement.getAttribute('data-bs-theme') === '{next}'", TimeSpan.FromSeconds(5), ct),
            "The toggle click did not switch the theme.");
    }

    private static async Task ClickElementAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var center = await browser.EvaluateAsync<JsonElement>(
            $"(() => {{ const r = document.querySelector({JsonSerializer.Serialize(selector)}).getBoundingClientRect(); return {{ x: r.left + r.width / 2, y: r.top + r.height / 2 }}; }})()", ct);
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
    }

    private static void AssertTheme(JsonElement state, string theme, string source, string page)
    {
        Assert.True(theme == state.GetProperty("html").GetString(), $"{page}: <html> is {state.GetProperty("html")}, expected {theme}");
        Assert.True(theme == state.GetProperty("body").GetString(), $"{page}: <body> is {state.GetProperty("body")}, expected {theme}");
        Assert.True(source == state.GetProperty("source").GetString(), $"{page}: source is {state.GetProperty("source")}, expected {source}");
    }

    private static void AssertToggleShows(JsonElement state, string theme)
    {
        // Existing toggle contract: pressed in dark mode; the icon shows the theme a click switches to.
        Assert.Equal(theme == "dark" ? "true" : "false", state.GetProperty("pressed").GetString());
        Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("toggleTitle").GetString()));
        if (state.GetProperty("toggleShown").GetBoolean())
        {
            Assert.Equal(theme == "dark", state.GetProperty("sunShown").GetBoolean());
            Assert.Equal(theme != "dark", state.GetProperty("moonShown").GetBoolean());
        }
    }

    private static void AssertToggleInsideViewport(JsonElement state, string page)
    {
        var r = state.GetProperty("toggle");
        var width = state.GetProperty("width").GetDouble();
        Assert.True(r.GetProperty("left").GetDouble() >= -1 && r.GetProperty("right").GetDouble() <= width + 1,
            $"{page}: the toggle is outside the viewport ({r})");
    }

    private static void AssertNoHorizontalOverflow(JsonElement state, string page) =>
        Assert.True(state.GetProperty("scrollWidth").GetDouble() <= state.GetProperty("clientWidth").GetDouble() + 1,
            $"{page}: the page scrolls sideways ({state.GetProperty("scrollWidth")} > {state.GetProperty("clientWidth")})");
}
