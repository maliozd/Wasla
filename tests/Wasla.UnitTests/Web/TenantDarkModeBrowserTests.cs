using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Dark mode as it is actually painted, in a real headless Chromium browser (Edge or Chrome) against the populated tenant
/// application of <see cref="TenantVisualWebHost"/>: on Dashboard, Orders, an order's details, Platform Connections,
/// Users, the settings pages, Print Bridge, the Live Screen and guided setup for a tenant still in Setup, at desktop and
/// phone widths in English LTR and Arabic RTL,
/// no visible surface is light (apart from the receipt paper preview, the Trendyol GO logo plate and the highlight colour
/// swatches, which are light on purpose) and visible text keeps WCAG AA contrast against what is painted behind it. Known
/// exceptions are exempted, not passed: white on the Wasla orange of primary buttons (about 3:1, the brand pairing in both
/// themes), recently completed Live Screen orders (deliberately dimmed) and disabled controls. Menus, the mobile drawer,
/// a modal, validation errors, keyboard focus and the Live Screen detail and settings are checked open. Light mode keeps
/// its surfaces, a theme choice restyles open pages, and dark mode still applies when storage is unavailable. Skips only
/// when no Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be
/// loaded from its CDN.
/// </summary>
public sealed class TenantDarkModeBrowserTests : IAsyncLifetime
{
    private const string TenantKey = "Wasla.tenant.theme";

    // Shared helpers for the page checks: colour parsing, alpha compositing against what is painted behind an element,
    // relative luminance and the WCAG contrast ratio.
    private const string Library = """
        const parse = s => {
          if (s.startsWith('color(srgb')) { const m = s.slice(10).match(/-?[\d.]+(e-?\d+)?/g).map(Number); return [m[0] * 255, m[1] * 255, m[2] * 255, m[3] ?? 1]; }
          const m = s.match(/[\d.]+/g); if (!m) return [0, 0, 0, 0];
          const n = m.map(Number); return [n[0], n[1], n[2], n[3] ?? 1];
        };
        const lum = ([r, g, b]) => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
        const over = (top, bottom) => { const a = top[3]; return [top[0] * a + bottom[0] * (1 - a), top[1] * a + bottom[1] * (1 - a), top[2] * a + bottom[2] * (1 - a), 1]; };
        const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
        const canvas = (() => { const p = document.createElement('div'); p.style.cssText = 'position:absolute;width:0;height:0;background-color:Canvas'; document.body.appendChild(p); const c = parse(getComputedStyle(p).backgroundColor); p.remove(); c[3] = 1; return c; })();
        const behind = el => {
          const layers = [];
          for (let p = el; p; p = p.parentElement) {
            const cs = getComputedStyle(p);
            if (cs.backgroundImage !== 'none' && !cs.backgroundImage.startsWith('url')) return null;
            const c = parse(cs.backgroundColor); if (c[3] > 0) layers.push(c); if (c[3] >= 1) break;
          }
          let col = canvas; for (let i = layers.length - 1; i >= 0; i--) col = over(layers[i], col); return col;
        };
        const shown = el => {
          const r = el.getBoundingClientRect(); if (r.width < 1 || r.height < 1) return false;
          for (let p = el; p; p = p.parentElement) { const cs = getComputedStyle(p); if (cs.display === 'none' || cs.visibility === 'hidden' || +cs.opacity < 0.1) return false; }
          return true;
        };
        const describe = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + [...e.classList].slice(0, 3).map(c => '.' + c).join('');
        // Light on purpose in dark mode: the printed-receipt preview, the logo plate of the black Trendyol GO wordmark and
        // the highlight colour choices (they show the colours themselves).
        const allowedLight = '.wasla-receipt-preview-paper, .platform-badge--trendyol, .wasla-color-swatch, input[type="color"]';
        // White on the Wasla orange is the brand's primary-button pairing in both themes; recently completed Live Screen
        // orders are deliberately dimmed in both themes; disabled controls are exempt.
        const exemptText = '.btn-primary, .wasla-btn--primary, .orders-card--muted, .wasla-orders-card--muted, :disabled, .disabled, [aria-disabled="true"], .modal:not(.show), .visually-hidden';
        const audit = scope => {
          const light = [], low = []; let surfaces = 0, texts = 0;
          for (const root of [...document.querySelectorAll(scope)].filter(shown))
            for (const el of [root, ...root.querySelectorAll('*')]) {
              if (!shown(el) || el.closest(allowedLight)) continue;
              const cs = getComputedStyle(el), box = el.getBoundingClientRect();
              // A surface is anything at least 12px each way with a mostly opaque background (not a status dot).
              if (parse(cs.backgroundColor)[3] >= 0.5 && box.width >= 12 && box.height >= 12) {
                surfaces++;
                const b = behind(el); if (b && lum(b) > 0.4) light.push(describe(el) + ' ' + cs.backgroundColor);
              }
              if (![...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim())) continue;
              if (el.closest(exemptText)) continue;
              const option = el.closest('label, .receipt-timing-option'); if (option && option.querySelector('input:disabled')) continue;
              const c = parse(cs.color); if (c[3] === 0) continue;
              let opacity = 1; for (let p = el; p; p = p.parentElement) opacity *= +getComputedStyle(p).opacity;
              const b = behind(el); if (!b) continue;
              texts++;
              const fg = over([c[0], c[1], c[2], c[3] * opacity], b);
              const size = parseFloat(cs.fontSize), need = size >= 24 || (+cs.fontWeight >= 700 && size >= 18.66) ? 3 : 4.5;
              const r = ratio(fg, b);
              if (r < need) low.push(describe(el) + ' "' + el.textContent.trim().slice(0, 30) + '" ' + r.toFixed(2) + ':1 < ' + need);
            }
          // The black Trendyol GO wordmark must sit on its light plate wherever it is shown.
          const unplated = [...document.querySelectorAll(scope)].flatMap(root => [...root.querySelectorAll('.platform-badge--trendyol img')])
            .filter(shown).filter(img => { const b = behind(img); return !b || lum(b) < 0.6; }).map(img => describe(img.closest('[class*="wasla-live"], tr, .card, body')));
          return { unplated, theme: document.documentElement.getAttribute('data-bs-theme'), bodyTheme: document.body.getAttribute('data-bs-theme'),
                   dir: document.documentElement.getAttribute('dir'), bodyLum: lum(behind(document.body)), surfaces, texts,
                   light: light.slice(0, 12), lowContrast: low.slice(0, 12),
                   overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth };
        };
        """;

    // Runs in every document before the page's own scripts: records uncaught page errors.
    private const string ErrorProbe = """
        (() => {
          const errors = [];
          window.__waslaPageErrors = errors;
          addEventListener('error', e => errors.push(String(e.message)));
          addEventListener('unhandledrejection', e => errors.push('unhandled rejection: ' + String(e.reason)));
        })();
        """;

    // Blocks storage as a browser with storage disabled does (as the WAS-68 sidebar tests do).
    private const string BlockStorage = """
        Object.defineProperty(window, 'localStorage', {
          configurable: true,
          get() { throw new DOMException('The operation is insecure.', 'SecurityError'); }
        });
        """;

    private static readonly string[] ShellPages =
    [
        "/dashboard",
        "/orders",
        $"/orders/details/{TenantVisualWebHost.DetailOrderId}",
        "/platform-connections",
        "/platform-connections/create",
        "/settings/users",
        "/settings/users/create",
        "/settings/orders",
        "/settings/receipt-printer",
        "/settings/account",
        "/branches",
        "/print-bridge/devices",
        "/print-bridge/setup",
        "/help"
    ];

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string, string, int> Layouts()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (culture, direction) in new[] { ("en-US", "ltr"), ("ar-SA", "rtl") })
            foreach (var width in new[] { 1280, 390 })
                data.Add(culture, direction, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task EveryTenantPage_IsDarkAndReadable_InDarkMode(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, "dark", ct);

        foreach (var path in ShellPages)
        {
            await LoadAsync(browser, path, ct);
            var page = $"{path} {culture} {width}px dark";
            var result = await AuditAsync(browser, "body", ct);
            AssertDark(result, direction, page);
            Assert.True(result.GetProperty("surfaces").GetInt32() > 3, $"{page}: no surfaces were checked");
            Assert.True(result.GetProperty("texts").GetInt32() > 5, $"{page}: no text was checked");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task TheLiveScreen_BoardDetailAndSettings_AreDarkAndReadable(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, "dark", ct);
        await browser.AddScriptBeforePageScriptsAsync("try { localStorage.setItem('Wasla.liveScreen.viewMode', 'board'); } catch (e) { }", ct);
        await LoadAsync(browser, "/orders/live-display", ct);
        Assert.True(await browser.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-detail]').length >= 5", TimeSpan.FromSeconds(15), ct),
            "The Live Screen did not render the seeded orders.");
        var page = $"Live Screen {culture} {width}px dark";

        AssertDark(await AuditAsync(browser, "body", ct), direction, page + " board");

        // The detail modal and the settings menu, opened from the keyboard: the header and cards are re-rendered by the
        // snapshot poll, so a pointer position measured before the click can move under it.
        await PressEnterOnAsync(browser, "#ordersLiveScreenHost [data-order-detail]", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('.modal.show .wasla-live-detail__actions')", TimeSpan.FromSeconds(10), ct), $"{page}: the detail modal did not open");
        await Task.Delay(400, ct);
        AssertDark(await AuditAsync(browser, ".modal.show .modal-content", ct), direction, page + " detail modal");
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('.modal.show, .modal-backdrop')", TimeSpan.FromSeconds(5), ct), $"{page}: the detail modal did not close");

        await PressEnterOnAsync(browser, "#ordersLiveDisplaySettingsToggle", ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('ordersLiveDisplaySettingsMenu').classList.contains('show')", TimeSpan.FromSeconds(5), ct), $"{page}: the settings menu did not open");
        await Task.Delay(300, ct);
        AssertDark(await AuditAsync(browser, "#ordersLiveDisplaySettingsMenu", ct), direction, page + " settings menu");
        await browser.PressKeyAsync("Escape", shift: false, ct);

        // List and Focus, chosen with the view switch.
        foreach (var view in new[] { "list", "focus" })
        {
            await PressEnterOnAsync(browser, $"[data-live-screen-view=\"{view}\"]", ct);
            Assert.True(await browser.WaitForAsync($"document.querySelector('[data-live-screen-view=\"{view}\"]').getAttribute('aria-pressed') === 'true'", TimeSpan.FromSeconds(5), ct),
                $"{page}: the {view} view was not selected");
            await Task.Delay(800, ct);
            AssertDark(await AuditAsync(browser, "body", ct), direction, $"{page} {view}");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task SharedNavigationMenusAndFocus_AreDarkAndVisible(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var mobile = width < 992;
        await using var browser = await BrowserAsync(culture, width, "dark", ct);
        await LoadAsync(browser, "/orders", ct);
        var page = $"/orders {culture} {width}px dark";

        if (mobile)
        {
            // The off-canvas navigation, opened with the real trigger.
            await ClickAsync(browser, "#tenantNavOpen", ct);
            Assert.True(await browser.WaitForAsync("document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: the navigation did not open");
            await Task.Delay(500, ct);
            AssertDark(await AuditAsync(browser, "#app-sidebar-tenant", ct), direction, page + " drawer");
        }

        // The settings menu at the foot of the sidebar.
        await ClickAsync(browser, "#tenantSettingsMenuToggle", ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('tenantSettingsMenu').classList.contains('show')", TimeSpan.FromSeconds(5), ct), $"{page}: the settings menu did not open");
        await Task.Delay(300, ct);
        AssertDark(await AuditAsync(browser, "#tenantSettingsMenu", ct), direction, page + " settings menu");
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await Task.Delay(300, ct);
        if (mobile)
        {
            await browser.PressKeyAsync("Escape", shift: false, ct);
            Assert.True(await browser.WaitForAsync("!document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: the navigation did not close");
        }

        // Keyboard focus from the top of the page: the first focused control shows a ring with at least 3:1 against
        // what is painted behind it.
        await browser.EvaluateAsync<bool>("(() => { document.activeElement && document.activeElement.blur(); window.scrollTo(0, 0); return true; })()", ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        await Task.Delay(200, ct);
        var focus = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              {{Library}}
              const a = document.activeElement, cs = getComputedStyle(a);
              const ring = parse(cs.outlineColor), b = behind(a.parentElement || document.body);
              return { target: describe(a), style: cs.outlineStyle, width: parseFloat(cs.outlineWidth),
                       contrast: b ? ratio(over(ring, b), b) : 0, matches: a.matches(':focus-visible') };
            })()
            """, ct);
        var target = focus.GetProperty("target").GetString();
        Assert.True(focus.GetProperty("matches").GetBoolean(), $"{page}: {target} is not :focus-visible after Tab");
        Assert.NotEqual("none", focus.GetProperty("style").GetString());
        Assert.True(focus.GetProperty("width").GetDouble() >= 2, $"{page}: {target} focus ring is thinner than 2px");
        Assert.True(focus.GetProperty("contrast").GetDouble() >= 3, $"{page}: {target} focus ring contrast {focus.GetProperty("contrast")} < 3");
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task GuidedSetupAndTheSetupChecklist_AreDarkAndReadable(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var setupHost = await TenantVisualWebHost.StartAsync(tenantInSetup: true);
        var cases = new (string? SignInAs, string Path, string Shown)[]
        {
            (null, "/dashboard", ".wasla-setup"),                              // the setup checklist, with "order training next"
            (null, "/orders/live-display", ".wasla-guided-training"),          // the Live Screen order-training panel
            ("new", "/dashboard", ".wasla-guided-setup-card"),                 // the first-use card
            ("resume", "/platform-connections", ".wasla-guided-setup-panel")   // a section panel
        };

        foreach (var (signInAs, path, shown) in cases)
        {
            await using var browser = await BrowserAsync(culture, width, "dark", ct, setupHost, signInAs);
            await LoadAsync(browser, path, ct, setupHost);
            var page = $"{path} as {signInAs ?? "training"} {culture} {width}px dark";
            Assert.True(await browser.WaitForAsync($"!!document.querySelector('{shown}')", TimeSpan.FromSeconds(10), ct), $"{page}: {shown} is not shown");
            await Task.Delay(400, ct);
            AssertDark(await AuditAsync(browser, "body", ct), direction, page);
        }
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task FormValidation_AndTheNotificationModal_AreDarkAndReadable(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, "dark", ct);
        var page = $"{culture} {width}px dark";

        // Server-side validation errors after submitting the empty form with a real click.
        await LoadAsync(browser, "/settings/users/create", ct);
        await ClickAsync(browser, "#tenant-main form [type=submit]", ct);
        Assert.True(await browser.WaitForAsync("document.readyState === 'complete' && !!document.querySelector('.field-validation-error')", TimeSpan.FromSeconds(15), ct),
            $"{page}: no validation error was shown");
        await Task.Delay(300, ct);
        AssertDark(await AuditAsync(browser, "#tenant-main", ct), direction, page + " user validation");

        // The notification settings modal.
        await LoadAsync(browser, "/settings/orders", ct);
        await ClickAsync(browser, "#notificationSettingsBtn", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#notificationSettingsModal.show .wasla-color-swatch')", TimeSpan.FromSeconds(10), ct),
            $"{page}: the notification settings modal did not open");
        await Task.Delay(400, ct);
        AssertDark(await AuditAsync(browser, "#notificationSettingsModal .modal-content", ct), direction, page + " notification modal");
    }

    [Theory]
    [InlineData("en-US", 1280)]
    [InlineData("ar-SA", 390)]
    public async Task LightMode_KeepsTheLightFoundationSurfaces(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, "light", ct);

        foreach (var path in new[] { "/dashboard", "/orders", "/settings/users", "/platform-connections" })
        {
            await LoadAsync(browser, path, ct);
            var state = await SurfacesAsync(browser, ct);
            Assert.Equal("light", state.GetProperty("theme").GetString());
            Assert.Equal("rgb(247, 244, 238)", state.GetProperty("body").GetString());
            Assert.Equal("rgb(255, 254, 250)", state.GetProperty("card").GetString());
            Assert.Equal("rgb(40, 35, 31)", state.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task AThemeChoice_RestylesOpenPages_AndAChoiceInAnotherDocumentIsFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, theme: null, ct);
        await browser.SetSystemColorSchemeAsync("light", ct);
        await LoadAsync(browser, "/orders", ct);
        var state = await SurfacesAsync(browser, ct);
        Assert.Equal("light", state.GetProperty("theme").GetString());
        Assert.Equal("rgb(255, 254, 250)", state.GetProperty("card").GetString());

        // A real click on the sidebar toggle: the open page is restyled without a reload, and the choice is stored.
        await ClickAsync(browser, ".wasla-sidebar-bottom .wasla-theme-toggle", ct);
        Assert.True(await browser.WaitForAsync("document.body.getAttribute('data-bs-theme') === 'dark'", TimeSpan.FromSeconds(5), ct));
        await Task.Delay(600, ct); // let the 140-150ms colour transitions of links and buttons finish
        state = await SurfacesAsync(browser, ct);
        Assert.Equal("dark", state.GetProperty("stored").GetString());
        Assert.Equal("rgb(15, 17, 21)", state.GetProperty("body").GetString());
        Assert.Equal("rgb(23, 26, 33)", state.GetProperty("card").GetString());
        AssertDark(await AuditAsync(browser, "body", ct), "ltr", "/orders after the toggle");

        // Another page of the app, and a reload.
        await LoadAsync(browser, "/dashboard", ct);
        AssertDark(await AuditAsync(browser, "body", ct), "ltr", "/dashboard after the choice");

        // A choice made in another document of the app (as in another tab) restyles this page.
        await browser.EvaluateAsync<bool>("""
            new Promise(resolve => {
              const frame = document.createElement('iframe');
              frame.src = '/help?frame=1';
              frame.style.cssText = 'position:absolute;inline-size:1px;block-size:1px;opacity:0';
              frame.onload = () => { frame.contentWindow.WaslaTheme.choose('light'); resolve(true); };
              document.body.appendChild(frame);
            })
            """, ct);
        Assert.True(await browser.WaitForAsync("document.body.getAttribute('data-bs-theme') === 'light'", TimeSpan.FromSeconds(5), ct),
            "The open page did not follow the choice made in another document.");
        state = await SurfacesAsync(browser, ct);
        Assert.Equal("rgb(247, 244, 238)", state.GetProperty("body").GetString());
        Assert.Equal("light", state.GetProperty("stored").GetString());
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task WithStorageUnavailable_TheSystemDarkThemeIsPaintedAndTheToggleRestylesThePage(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, theme: null, ct);
        await browser.AddScriptBeforePageScriptsAsync(ErrorProbe + BlockStorage, ct);
        await browser.SetSystemColorSchemeAsync("dark", ct);
        var page = $"blocked storage {culture} {width}px";

        await LoadAsync(browser, "/dashboard", ct);
        AssertDark(await AuditAsync(browser, "body", ct), direction, page);
        Assert.Equal(0, (await browser.EvaluateAsync<JsonElement>("window.__waslaPageErrors", ct)).GetArrayLength());

        // The toggle still restyles the open page (the choice cannot be stored).
        if (width < 992)
        {
            await ClickAsync(browser, "#tenantNavOpen", ct);
            Assert.True(await browser.WaitForAsync("document.body.classList.contains('wasla-nav-open')", TimeSpan.FromSeconds(5), ct), $"{page}: the navigation did not open");
            await Task.Delay(500, ct);
        }

        await ClickAsync(browser, ".wasla-sidebar-bottom .wasla-theme-toggle", ct);
        Assert.True(await browser.WaitForAsync("document.body.getAttribute('data-bs-theme') === 'light'", TimeSpan.FromSeconds(5), ct), $"{page}: the toggle did not switch to light");
        await Task.Delay(600, ct);
        var state = await browser.EvaluateAsync<JsonElement>("""
            ({ body: getComputedStyle(document.body).backgroundColor,
               card: getComputedStyle(document.querySelector('#tenant-main .card, #tenant-main .wasla-card, #tenant-main .wasla-surface')).backgroundColor,
               errors: window.__waslaPageErrors })
            """, ct);
        Assert.Equal("rgb(247, 244, 238)", state.GetProperty("body").GetString());
        Assert.Equal("rgb(255, 254, 250)", state.GetProperty("card").GetString());
        Assert.Equal(0, state.GetProperty("errors").GetArrayLength());
    }

    private static void AssertDark(JsonElement result, string direction, string page)
    {
        Assert.Equal("dark", result.GetProperty("theme").GetString());
        Assert.Equal("dark", result.GetProperty("bodyTheme").GetString());
        Assert.Equal(direction, result.GetProperty("dir").GetString());
        Assert.True(result.GetProperty("bodyLum").GetDouble() < 0.02, $"{page}: the page background is not dark");
        Assert.False(result.GetProperty("overflow").GetBoolean(), $"{page}: the page scrolls sideways");
        Assert.True(result.GetProperty("light").GetArrayLength() == 0, $"{page}: light surfaces in dark mode: {result.GetProperty("light")}");
        Assert.True(result.GetProperty("lowContrast").GetArrayLength() == 0, $"{page}: text below WCAG AA contrast: {result.GetProperty("lowContrast")}");
        Assert.True(result.GetProperty("unplated").GetArrayLength() == 0, $"{page}: Trendyol GO wordmark without its light plate: {result.GetProperty("unplated")}");
    }

    private static Task<JsonElement> AuditAsync(HeadlessChromium browser, string scope, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>($"(() => {{ {Library} return audit({JsonSerializer.Serialize(scope)}); }})()", ct);

    private static Task<JsonElement> SurfacesAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const card = document.querySelector('#tenant-main .card, #tenant-main .wasla-card, #tenant-main .wasla-surface');
              let stored = null; try { stored = localStorage.getItem('{{TenantKey}}'); } catch (e) { }
              return { theme: document.documentElement.getAttribute('data-bs-theme'), stored,
                       body: getComputedStyle(document.body).backgroundColor,
                       card: card ? getComputedStyle(card).backgroundColor : null,
                       text: getComputedStyle(document.body).color };
            })()
            """, ct);

    /// <summary>A signed-in browser with the explicit tenant theme stored (or no choice when <paramref name="theme"/> is null).</summary>
    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, string? theme, CancellationToken ct, TenantVisualWebHost? host = null, string? signInAs = null)
    {
        host ??= _host;
        var browser = await host.BrowserAsync(culture, width, 860, mobile: width < 992, ct, signInAs);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser dark-mode checks.");
        await browser.SetSystemColorSchemeAsync(theme ?? "light", ct);

        // Store the choice from a page of this origin before any tenant page loads.
        await browser.NavigateAsync(new Uri(host.BaseAddress, "/css/site.css"), ct);
        var store = theme is null ? $"localStorage.removeItem('{TenantKey}')" : $"localStorage.setItem('{TenantKey}', '{theme}')";
        await browser.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); {store}; return true; }})()", ct);
        return browser;
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, CancellationToken ct, TenantVisualWebHost? host = null)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri((host ?? _host).BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri((host ?? _host).BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
        await Task.Delay(400, ct);
    }

    // Real keyboard input: focus the first visible match, then press Enter.
    private static async Task PressEnterOnAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var focused = await browser.EvaluateAsync<bool>($$"""
            (() => {
              const e = [...document.querySelectorAll({{JsonSerializer.Serialize(selector)}})]
                .find(x => { const r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(x).visibility !== 'hidden'; });
              if (!e) return false;
              e.focus();
              return document.activeElement === e;
            })()
            """, ct);
        Assert.True(focused, $"{selector} could not be focused.");
        await browser.PressKeyAsync("Enter", shift: false, ct);
    }

    // A real mouse click at the element's center, after scrolling it into view and checking that nothing covers it.
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var target = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = [...document.querySelectorAll({{JsonSerializer.Serialize(selector)}})]
                .find(x => { const r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(x).visibility !== 'hidden'; });
              if (!e) return { found: false };
              e.scrollIntoView({ block: 'center', inline: 'center' });
              const r = e.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              return { found: true, x, y, hit: !!hit && e.contains(hit) };
            })()
            """, ct);
        Assert.True(target.GetProperty("found").GetBoolean(), $"{selector} is not on the page.");
        Assert.True(target.GetProperty("hit").GetBoolean(), $"{selector} is covered or off screen.");
        await browser.ClickAtAsync(target.GetProperty("x").GetDouble(), target.GetProperty("y").GetDouble(), ct);
    }
}
