using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Accessible names of the tenant and Central Admin sign-in forms and of the tenant sidebar (WAS-132), read from the
/// accessibility tree of a real headless Chromium browser (Edge or Chrome) against the real pages of
/// <see cref="ThemeWebHost"/>. Every sign-in field is named by its visible label, whose <c>for</c> matches the rendered
/// input id, and a click on the label focuses the field. Every sidebar link and control keeps the same meaningful name
/// expanded and collapsed (its text is visually hidden, never <c>display: none</c>), without icon glyphs or repeated
/// text; the settings menu with Logout is named by its toggle; and the collapsed sidebar is fully keyboard operable,
/// in Turkish, English, Arabic (right to left) and Russian, light and dark, at desktop and 390 px widths. Skips only when
/// no Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be loaded
/// from its CDN.
/// </summary>
public sealed class TenantAccessibleNamesBrowserTests : IAsyncLifetime
{
    private const string TenantThemeKey = "Wasla.tenant.theme";
    private const string SidebarKey = "Wasla.sidebarCollapsed";

    // Private-use characters are icon-font glyphs (Bootstrap Icons); a screen reader would read them as noise.
    private static readonly System.Text.RegularExpressions.Regex IconGlyph = new("[-]");

    private ThemeWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await ThemeWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string, int> SignInCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var culture in new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" })
            foreach (var width in new[] { 390, 1280 })
                data.Add(culture, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(SignInCases))]
    public async Task TenantSignInFields_AreNamedByTheirVisibleLabels(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, signedIn: false, theme: null, ct);
        await LoadAsync(browser, _host.BaseAddress, "/auth/login", ct);
        await AssertSignInFieldsAsync(browser, "/auth/login", "login-password", culture, $"tenant sign-in {culture} {width}px", ct);
    }

    [Theory]
    [MemberData(nameof(SignInCases))]
    public async Task CentralAdminSignInFields_AreNamedByTheirVisibleLabels(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        using var central = new CentralTestDatabase();
        using var tenants = new RecordingTenantDbFactory();
        await using var admin = await AdminWebHost.StartAsync(central, tenants);
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser accessibility checks.");
        await using var browser = await (await HeadlessChromium.StartAsync(executable, ct)).SetUpAsync(async started =>
        {
            await started.SetCookieAsync(admin.BaseAddress, CookieRequestCultureProvider.DefaultCookieName,
                Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
            await started.SetViewportAsync(width, 860, mobile: width < 992, ct);
        });
        await LoadAsync(browser, admin.BaseAddress, "/admin/login", ct);
        await AssertSignInFieldsAsync(browser, "/admin/login", "admin-login-password", culture, $"admin sign-in {culture} {width}px", ct);
    }

    private static async Task AssertSignInFieldsAsync(HeadlessChromium browser, string action, string passwordId, string culture, string page, CancellationToken ct)
    {
        var form = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const form = document.querySelector('form[action="{{action}}"]');
              const fields = [...form.querySelectorAll('input:not([type=hidden])')];
              return {
                dir: document.documentElement.getAttribute('dir'),
                scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
                orphanLabels: [...form.querySelectorAll('label[for]')].filter(l => !document.getElementById(l.htmlFor)).map(l => l.htmlFor),
                fields: fields.map(f => ({ id: f.id, labels: f.labels.length, label: f.labels.length ? f.labels[0].textContent.trim() : '' }))
              };
            })()
            """, ct);
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", form.GetProperty("dir").GetString());
        Assert.True(form.GetProperty("orphanLabels").GetArrayLength() == 0, $"{page}: labels point at no element: {form.GetProperty("orphanLabels")}");

        var fields = form.GetProperty("fields").EnumerateArray().ToList();
        var names = await browser.AccessibleNodesAsync($"form[action=\"{action}\"] input:not([type=hidden])", ct);
        Assert.Equal(fields.Count, names.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var id = fields[i].GetProperty("id").GetString();
            var label = fields[i].GetProperty("label").GetString();
            Assert.True(fields[i].GetProperty("labels").GetInt32() == 1, $"{page}: #{id} has {fields[i].GetProperty("labels")} labels");
            Assert.False(string.IsNullOrWhiteSpace(label), $"{page}: #{id} has an empty label");
            Assert.True(label == names[i].Name, $"{page}: #{id} is named \"{names[i].Name}\", its visible label says \"{label}\"");
        }

        var password = fields.Single(f => f.GetProperty("id").GetString() == passwordId);
        Assert.Equal(Resource(culture, "Auth.Password"), password.GetProperty("label").GetString());

        // A real click on the visible label focuses the password field; Shift+Tab and Tab move between the fields.
        await ClickAsync(browser, $"label[for=\"{passwordId}\"]", ct);
        Assert.Equal(passwordId, await FocusedIdAsync(browser, ct));
        await browser.PressKeyAsync("Tab", shift: true, ct);
        Assert.Equal("Email", await FocusedIdAsync(browser, ct));
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.Equal(passwordId, await FocusedIdAsync(browser, ct));
        Assert.True(form.GetProperty("scrollWidth").GetDouble() <= form.GetProperty("clientWidth").GetDouble() + 1, $"{page}: the page scrolls sideways");
    }

    public static TheoryData<string, string> SidebarCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var culture in new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" })
            foreach (var theme in new[] { "light", "dark" })
                data.Add(culture, theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(SidebarCases))]
    public async Task SidebarControls_KeepTheirNames_ExpandedAndCollapsed(string culture, string theme)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"sidebar {culture} {theme} 1366px";
        await using var browser = await BrowserAsync(culture, 1366, signedIn: true, theme, ct);
        await LoadAsync(browser, _host.BaseAddress, "/help", ct);
        Assert.Equal(theme, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('data-bs-theme')", ct));

        var expanded = await SidebarControlsAsync(browser, ct);
        Assert.True(expanded.Count >= 8, $"{page}: only {expanded.Count} sidebar controls found");
        foreach (var control in expanded)
        {
            AssertMeaningfulName(control, page + " expanded");
            if (control.Label is { Length: > 0 } label && !control.HasAriaLabel)
                Assert.True(label == control.Name, $"{page} expanded: {control.Key} is named \"{control.Name}\", its visible text says \"{label}\"");
        }

        var settings = expanded.Single(c => c.Key == "#tenantSettingsMenuToggle");
        Assert.Equal(Resource(culture, "Navigation.Settings"), settings.Name);

        // Collapse with the real control; every control that stays reachable keeps exactly the same name.
        await ClickAsync(browser, "#tenantSidebarToggle", ct);
        Assert.True(await browser.WaitForAsync(
            "document.body.classList.contains('sidebar-collapsed') && document.getElementById('app-sidebar-tenant').getBoundingClientRect().width <= 69",
            TimeSpan.FromSeconds(5), ct), $"{page}: the sidebar did not collapse");
        var collapsed = await SidebarControlsAsync(browser, ct);
        foreach (var control in collapsed)
        {
            AssertMeaningfulName(control, page + " collapsed");
            var before = expanded.SingleOrDefault(c => c.Key == control.Key);
            Assert.True(before is not null, $"{page}: {control.Key} appears only when collapsed");
            Assert.True(before!.Name == control.Name, $"{page}: {control.Key} is \"{before.Name}\" expanded but \"{control.Name}\" collapsed");
        }

        Assert.Equal(expanded.Count(c => !c.InSubmenu), collapsed.Count);

        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const sidebar = document.getElementById('app-sidebar-tenant'), box = sidebar.getBoundingClientRect();
              const texts = [...sidebar.querySelectorAll('.nav-link .nav-text, .wasla-sidebar-action-btn .wasla-sidebar-action-label')]
                .filter(t => t.closest('a, button').getBoundingClientRect().width > 0);
              return {
                hidden: texts.map(t => ({ display: getComputedStyle(t).display, r: t.getBoundingClientRect() }))
                  .filter(x => x.display === 'none' || x.r.width > 1 || x.r.height > 1).length,
                outside: texts.filter(t => { const r = t.getBoundingClientRect(); return r.left < box.left - 1 || r.right > box.right + 1; }).length,
                count: texts.length,
                scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
                sidebarScroll: sidebar.scrollWidth - sidebar.clientWidth
              };
            })()
            """, ct);
        Assert.True(layout.GetProperty("count").GetInt32() > 0, $"{page}: no collapsed labels found");
        Assert.True(layout.GetProperty("hidden").GetInt32() == 0, $"{page}: a collapsed label is display:none or visible: {layout}");
        Assert.True(layout.GetProperty("outside").GetInt32() == 0, $"{page}: a hidden label sits outside the sidebar: {layout}");
        Assert.True(layout.GetProperty("scrollWidth").GetDouble() <= layout.GetProperty("clientWidth").GetDouble() + 1, $"{page}: the page scrolls sideways");
        Assert.True(layout.GetProperty("sidebarScroll").GetDouble() <= 1, $"{page}: the collapsed sidebar scrolls sideways");

        // The settings menu, keyboard only: Enter opens it, its menu is named by the toggle, Logout is named, Escape closes
        // it and returns focus to the toggle.
        await FocusAsync(browser, "#tenantSettingsMenuToggle", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync("document.getElementById('tenantSettingsMenu').classList.contains('show')", TimeSpan.FromSeconds(5), ct),
            $"{page}: Enter did not open the settings menu");
        var menu = (await browser.AccessibleNodesAsync("#tenantSettingsMenu", ct)).Single();
        Assert.Equal(Resource(culture, "Navigation.Settings"), menu.Name);
        var logout = (await browser.AccessibleNodesAsync("#tenantSettingsMenu button[type=submit]", ct)).Single();
        Assert.Equal(Resource(culture, "Account.Logout"), logout.Name);
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.getElementById('tenantSettingsMenu').classList.contains('show')", TimeSpan.FromSeconds(5), ct),
            $"{page}: Escape did not close the settings menu");
        Assert.Equal("tenantSettingsMenuToggle", await FocusedIdAsync(browser, ct));

        // Tab walks the collapsed sidebar from its first control and leaves it: every stop is a named sidebar control.
        await FocusAsync(browser, "#tenantSidebarToggle", ct);
        var stops = new List<string>();
        for (var i = 0; i < collapsed.Count + 2; i++)
        {
            await browser.PressKeyAsync("Tab", shift: false, ct);
            var stop = await browser.EvaluateAsync<string?>(
                "document.getElementById('app-sidebar-tenant').contains(document.activeElement) ? document.activeElement.getAttribute('data-wasla-name-key') : null", ct);
            if (stop is null)
                break;
            stops.Add(stop);
        }

        Assert.True(stops.Count < collapsed.Count + 2, $"{page}: Tab never left the collapsed sidebar ({string.Join(", ", stops)})");
        Assert.All(stops, stop => Assert.Contains(collapsed, c => c.Key == stop));
    }

    [Theory]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task OnAPhone_TheDrawerShowsAndNamesEveryLabel_EvenWithTheDesktopSidebarCollapsed(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = $"drawer {culture} 390px";
        await using var browser = await BrowserAsync(culture, 390, signedIn: true, theme: "light", ct, collapsedPreference: true);
        await LoadAsync(browser, _host.BaseAddress, "/help", ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));

        await FocusAsync(browser, "#tenantNavOpen", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync("document.body.classList.contains('wasla-nav-open') && document.activeElement.id === 'tenantNavClose'", TimeSpan.FromSeconds(5), ct),
            $"{page}: the drawer did not open");
        Assert.True(await browser.EvaluateAsync<bool>("document.body.classList.contains('sidebar-collapsed')", ct), $"{page}: the collapsed preference was not read");

        var controls = await SidebarControlsAsync(browser, ct);
        foreach (var control in controls)
        {
            AssertMeaningfulName(control, page);
            if (control.Label is { Length: > 0 } label && !control.HasAriaLabel)
                Assert.True(label == control.Name, $"{page}: {control.Key} is named \"{control.Name}\", its visible text says \"{label}\"");
        }

        var visible = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const texts = [...document.querySelectorAll('#app-sidebar-tenant .nav-link .nav-text, #app-sidebar-tenant .wasla-sidebar-action-label')]
                .filter(t => t.closest('a, button').getBoundingClientRect().width > 0);
              return { count: texts.length, narrow: texts.filter(t => t.getBoundingClientRect().width < 8).length,
                       scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth };
            })()
            """, ct);
        Assert.True(visible.GetProperty("count").GetInt32() > 0, $"{page}: no drawer labels found");
        Assert.True(visible.GetProperty("narrow").GetInt32() == 0, $"{page}: a drawer label is hidden: {visible}");
        Assert.True(visible.GetProperty("scrollWidth").GetDouble() <= visible.GetProperty("clientWidth").GetDouble() + 1, $"{page}: the page scrolls sideways");

        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.body.classList.contains('wasla-nav-open') && document.activeElement.id === 'tenantNavOpen'", TimeSpan.FromSeconds(5), ct),
            $"{page}: Escape did not close the drawer and return focus");
    }

    private sealed record SidebarControl(string Key, string Name, string? Role, string Label, bool HasAriaLabel, bool InSubmenu);

    /// <summary>
    /// The rendered links and buttons of the sidebar with their browser-computed names. Each gets a stable key (its id, or
    /// its link target and text class) so expanded and collapsed names can be compared control by control.
    /// </summary>
    private static async Task<IReadOnlyList<SidebarControl>> SidebarControlsAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var marked = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              document.querySelectorAll('[data-wasla-name-key]').forEach(e => e.removeAttribute('data-wasla-name-key'));
              const all = [...document.querySelectorAll('#app-sidebar-tenant a[href], #app-sidebar-tenant button')]
                .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden'; });
              return all.map(e => {
                // The brand and the Dashboard link share a target, so a link's key also names its first class.
                const key = e.id ? '#' + e.id
                  : e.hasAttribute('href') ? 'a[href="' + e.getAttribute('href') + '"].' + e.classList[0] + (e.classList.contains('wasla-sidebar-nav-sub__link') ? '.sub' : '')
                  : e.tagName.toLowerCase() + '.' + [...e.classList].filter(c => c !== 'show').join('.');
                e.setAttribute('data-wasla-name-key', key);
                const text = e.querySelector('.nav-text, .wasla-sidebar-action-label');
                return { key, label: text ? text.textContent.trim() : '', aria: e.hasAttribute('aria-label') || e.hasAttribute('aria-labelledby'),
                         sub: !!e.closest('.wasla-sidebar-nav-sub') };
              });
            })()
            """, ct);
        var names = await browser.AccessibleNodesAsync("[data-wasla-name-key]", ct);
        var controls = marked.EnumerateArray().ToList();
        Assert.Equal(controls.Count, names.Count);
        return controls.Select((c, i) => new SidebarControl(
            c.GetProperty("key").GetString()!, names[i].Name, names[i].Role, c.GetProperty("label").GetString()!,
            c.GetProperty("aria").GetBoolean(), c.GetProperty("sub").GetBoolean())).ToList();
    }

    private static void AssertMeaningfulName(SidebarControl control, string page)
    {
        var name = control.Name.Trim();
        Assert.True(name.Length > 0, $"{page}: {control.Key} has no accessible name");
        Assert.False(IconGlyph.IsMatch(name), $"{page}: {control.Key} is named \"{name}\", which includes an icon glyph");
        var half = name.Length / 2;
        Assert.False(name.Length > 1 && name.Length % 2 == 1 && name[half] == ' ' && name[..half] == name[(half + 1)..],
            $"{page}: {control.Key} repeats its name: \"{name}\"");
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, bool signedIn, string? theme, CancellationToken ct, bool collapsedPreference = false)
    {
        var browser = await _host.BrowserAsync(culture, signedIn, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser accessibility checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.SetViewportAsync(width, 860, mobile: width < 992, ct);
            await started.SetSystemColorSchemeAsync(theme ?? "light", ct);
            await started.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
            var store = theme is null ? "" : $"localStorage.setItem('{TenantThemeKey}', '{theme}');";
            if (collapsedPreference)
                store += $"localStorage.setItem('{SidebarKey}', 'true');";
            await started.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); {store} return true; }})()", ct);
        });
    }

    private static async Task LoadAsync(HeadlessChromium browser, Uri baseAddress, string path, CancellationToken ct)
    {
        // The layouts load Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try { await browser.NavigateAsync(new Uri(baseAddress, path), ct); }
        catch (TimeoutException) { await browser.NavigateAsync(new Uri(baseAddress, path), ct); }
        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== '' && !!window.bootstrap", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the page cannot be exercised.");
    }

    private static Task<string?> FocusedIdAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<string?>("document.activeElement ? document.activeElement.id : null", ct);

    private static async Task FocusAsync(HeadlessChromium browser, string selector, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); e.focus(); return document.activeElement === e; }})()", ct),
            $"{selector} could not be focused.");

    // A real mouse click at the element's center, after checking that nothing covers it.
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct)
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

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
