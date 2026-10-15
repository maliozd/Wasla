using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The post-login redirect chain and the Access denied page in a real headless Chromium browser (WAS-113). The tenant
/// application is <see cref="TenantSessionWebHost"/>: real tenant resolution from the host name, the real cookie scheme
/// with session revalidation, the Program.cs policies and the real controllers and views. The browser maps the tenant
/// host names to the in-process server, signs in through the rendered form and follows every redirect itself. Skips
/// only when no Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be
/// loaded from its CDN.
/// </summary>
public sealed class TenantLoginRedirectBrowserTests : IAsyncLifetime
{
    private const string LiveScreen = "/orders/live-display";

    private const string State = """
        (() => {
          const html = document.documentElement;
          const home = document.querySelector('[data-access-denied-home]');
          const r = home ? home.getBoundingClientRect() : null;
          const hit = r ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) : null;
          const card = document.querySelector('.wasla-card');
          const rgb = c => (c.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
          const lum = c => { const [r, g, b] = rgb(c).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
          return {
            path: location.pathname, search: location.search, dir: html.getAttribute('dir'), lang: html.getAttribute('lang'),
            theme: html.getAttribute('data-bs-theme'),
            liveScreen: !!document.getElementById('ordersLiveDisplayRoot'), dashboard: !!document.querySelector('.wasla-dashboard'),
            homeText: home ? home.textContent.trim() : null, homeHref: home ? home.getAttribute('href') : null,
            homeVisible: !!r && r.width > 0 && r.height > 0 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight,
            homeHit: !!(home && hit && home.contains(hit)),
            homeCenter: r ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null,
            loginLinks: document.querySelectorAll('a[href^="/auth/login"], form[action^="/auth/login"]').length,
            logoutForms: document.querySelectorAll('form[action="/auth/logout"]').length,
            cardLuminance: card ? lum(getComputedStyle(card).backgroundColor) : null,
            overflow: html.scrollWidth - html.clientWidth
          };
        })()
        """;

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantPostLoginRedirectTests.StartHostAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Uri Origin => new($"http://{TenantSessionWebHost.AlphaHost}:{_host.BaseAddress.Port}");

    [Theory]
    [InlineData(UserRole.Kitchen, "ar-SA", LiveScreen)]
    [InlineData(UserRole.Cashier, "en-US", LiveScreen)]
    [InlineData(UserRole.Owner, "tr-TR", "/dashboard")]
    [InlineData(UserRole.Manager, "ru-RU", "/dashboard")]
    [InlineData(UserRole.Viewer, "en-US", "/dashboard")]
    public async Task SigningInThroughTheForm_LandsOnTheRolesPage(UserRole role, string culture, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = _host.SeedUser(_host.AlphaId, $"{role.ToString().ToLowerInvariant()}@alpha.test", role);
        await using var browser = await BrowserAsync(culture, 1280, 860, mobile: false, "light", ct);

        await GoAsync(browser, "/auth/login", "/auth/login", ct);
        await SubmitLoginAsync(browser, user.Email, expected, ct);
        var state = await StateAsync(browser, ct);

        Assert.Equal(expected, state.GetProperty("path").GetString());
        Assert.Equal(expected == LiveScreen, state.GetProperty("liveScreen").GetBoolean());
        Assert.Equal(expected != LiveScreen, state.GetProperty("dashboard").GetBoolean());
        Assert.Null(state.GetProperty("homeText").GetString());
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", state.GetProperty("dir").GetString());
    }

    /// <summary>
    /// A Kitchen deep link to the Dashboard: the browser is sent to sign in with that ReturnUrl, and after signing in
    /// lands on the Live Screen instead of being sent on to Access denied.
    /// </summary>
    [Fact]
    public async Task DeepLinkToAPageTheRoleMayNotOpen_ThenSignIn_LandsOnTheRolesPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var kitchen = _host.SeedUser(_host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        await using var browser = await BrowserAsync("en-US", 1280, 860, mobile: false, "light", ct);

        await GoAsync(browser, "/dashboard", "/auth/login", ct);
        var challenged = await StateAsync(browser, ct);
        await SubmitLoginAsync(browser, kitchen.Email, LiveScreen, ct);

        Assert.Equal("?ReturnUrl=%2Fdashboard", challenged.GetProperty("search").GetString());
        Assert.Equal(LiveScreen, (await StateAsync(browser, ct)).GetProperty("path").GetString());
    }

    /// <summary>
    /// A signed-in Kitchen user on Access denied: the localized recovery button is visible and clickable in both
    /// reading directions, both themes and at phone and desktop widths; there is no login link and the logout control
    /// stays; a real click leads to the Live Screen.
    /// </summary>
    [Theory]
    [InlineData("ar-SA", 390, true, "dark", "الانتقال إلى صفحة البداية")]
    [InlineData("ar-SA", 1280, false, "light", "الانتقال إلى صفحة البداية")]
    [InlineData("en-US", 1280, false, "dark", "Go to my start page")]
    [InlineData("tr-TR", 390, true, "light", "Başlangıç sayfama git")]
    [InlineData("ru-RU", 1280, false, "light", "Перейти на начальную страницу")]
    public async Task AccessDenied_SignedIn_OffersAWorkingStartPageButton(string culture, int width, bool mobile, string theme, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var kitchen = _host.SeedUser(_host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        await using var browser = await BrowserAsync(culture, width, mobile ? 844 : 860, mobile, theme, ct);
        await GoAsync(browser, "/auth/login", "/auth/login", ct);
        await SubmitLoginAsync(browser, kitchen.Email, LiveScreen, ct);

        await GoAsync(browser, "/settings/users", "/auth/access-denied", ct);
        var state = await StateAsync(browser, ct);

        Assert.Equal(label, state.GetProperty("homeText").GetString());
        Assert.Equal(LiveScreen, state.GetProperty("homeHref").GetString());
        Assert.True(state.GetProperty("homeVisible").GetBoolean(), state.ToString());
        Assert.True(state.GetProperty("homeHit").GetBoolean(), state.ToString());
        Assert.Equal(0, state.GetProperty("loginLinks").GetInt32());
        Assert.True(state.GetProperty("logoutForms").GetInt32() > 0);
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", state.GetProperty("dir").GetString());
        Assert.Equal(theme, state.GetProperty("theme").GetString());
        var luminance = state.GetProperty("cardLuminance").GetDouble();
        Assert.True(theme == "dark" ? luminance < 0.2 : luminance > 0.6, $"Card luminance {luminance} in {theme}.");
        Assert.True(state.GetProperty("overflow").GetDouble() <= 1, state.ToString());

        var center = state.GetProperty("homeCenter");
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
        Assert.True(await browser.WaitForAsync($"location.pathname === '{LiveScreen}' && document.readyState === 'complete'", TimeSpan.FromSeconds(20), ct),
            "The start page button did not open the Live Screen.");
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, bool mobile, string theme, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser login checks.");

        // The tenant host names resolve to the in-process server; everything else (the CDN) resolves normally.
        var browser = await HeadlessChromium.StartAsync(
            executable,
            [$"--host-resolver-rules=MAP {TenantSessionWebHost.AlphaHost} 127.0.0.1, MAP {TenantSessionWebHost.BetaHost} 127.0.0.1"],
            ct);
        return await browser.SetUpAsync(async started =>
        {
            await started.SetCookieAsync(Origin, CookieRequestCultureProvider.DefaultCookieName,
                Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
            await started.SetViewportAsync(width, height, mobile, ct);
            await started.SetSystemColorSchemeAsync(theme, ct);
        });
    }

    /// <summary>Opens a path on the tenant host and waits until the browser has followed every redirect to <paramref name="expectedPath"/>.</summary>
    private async Task GoAsync(HeadlessChromium browser, string path, string expectedPath, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($"(() => {{ location.href = {JsonSerializer.Serialize(new Uri(Origin, path).ToString())}; return true; }})()", ct);
        await WaitForPageAsync(browser, expectedPath, ct);
    }

    private static async Task SubmitLoginAsync(HeadlessChromium browser, string email, string expectedPath, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[action="/auth/login"]');
              form.querySelector('[name="Email"]').value = {{JsonSerializer.Serialize(email)}};
              form.querySelector('[name="Password"]').value = {{JsonSerializer.Serialize(TenantSessionWebHost.Password)}};
              form.querySelector('button[type="submit"]').click();
              return true;
            })()
            """, ct);
        await WaitForPageAsync(browser, expectedPath, ct);
    }

    private static async Task WaitForPageAsync(HeadlessChromium browser, string expectedPath, CancellationToken ct)
    {
        if (!await browser.WaitForAsync(
                $"location.pathname === {JsonSerializer.Serialize(expectedPath)} && document.readyState === 'complete'",
                TimeSpan.FromSeconds(30), ct))
        {
            Assert.Fail($"Expected {expectedPath}, the browser is at {await browser.EvaluateAsync<string>("location.href", ct)}.");
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant pages cannot be exercised.");
        await Task.Delay(150, ct);
    }

    private static Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>(State, ct);
}
