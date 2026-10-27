using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The WAS-66 host chain in a real headless Chromium browser: the marketing site on <c>wasla.local</c>, its sign-in link
/// to the application entry <c>app.wasla.local</c>, the tenant address page there, and sign-in on the tenant host
/// <c>alpha.app.wasla.local</c>. The application is <see cref="TenantSessionWebHost"/> (real tenant resolution, cookie
/// scheme, controllers and views, Production environment). The browser maps every host name, port included, to the
/// in-process server, so the links the pages generate (no port outside Development) are followed unchanged. Skips only
/// when no Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be
/// loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class WaslaHostRoutingBrowserTests : IAsyncLifetime
{
    private static readonly string[] MappedHosts =
    [
        "wasla.local", "app.wasla.local", TenantSessionWebHost.AlphaHost, "admin.app.wasla.local", "alpha.app.wasla.local.evil.example"
    ];

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantPostLoginRedirectTests.StartHostAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("tr-TR", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task SignInOnTheMarketingSite_LeadsThroughTheApplicationEntry_ToTheTenantsOwnHost(string culture, string dir)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = _host.SeedUser(_host.AlphaId, $"owner.{culture}@alpha.test", UserRole.Owner);
        await using var browser = await BrowserAsync(culture, ct);

        await browser.NavigateAsync(new Uri("http://wasla.local/"), ct);
        await WaitForAsync(browser, "location.host === 'wasla.local' && location.pathname === '/' && !!document.querySelector('.wm-header')", ct);
        var header = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const links = [...document.querySelectorAll('a')].filter(a => a.getAttribute('href') && a.getAttribute('href').includes('/auth/login'));
              return { dir: document.documentElement.getAttribute('dir'), hrefs: links.map(a => a.getAttribute('href')) };
            })()
            """, ct);
        Assert.Equal(dir, header.GetProperty("dir").GetString());
        // The header's navigation, the header's narrow-screen link next to the menu button, and the footer.
        Assert.Equal(
            new[] { "http://app.wasla.local/auth/login", "http://app.wasla.local/auth/login", "http://app.wasla.local/auth/login" },
            header.GetProperty("hrefs").EnumerateArray().Select(h => h.GetString()).ToArray());

        // A real click on the header's sign-in link.
        var center = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const r = document.querySelector('.wm-nav__end a[href$="/auth/login"]').getBoundingClientRect();
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """, ct);
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
        await WaitForAsync(browser, "location.host === 'app.wasla.local' && location.pathname === '/tenant-address-required' && document.readyState === 'complete'", ct);
        await RequireBootstrapAsync(browser, ct);
        var entry = await browser.EvaluateAsync<JsonElement>("""
            ({ text: document.body.innerText, dir: document.documentElement.getAttribute('dir'),
               signup: document.querySelector('a.btn-primary[href$="/signup"]')?.getAttribute('href') })
            """, ct);
        Assert.Contains("bahce-tadi.app.wasla.local", entry.GetProperty("text").GetString());
        Assert.Equal(dir, entry.GetProperty("dir").GetString());
        Assert.Equal("http://wasla.local/signup", entry.GetProperty("signup").GetString());

        // The visitor goes to their tenant address and signs in there.
        await browser.NavigateAsync(new Uri($"http://{TenantSessionWebHost.AlphaHost}/auth/login"), ct);
        await WaitForAsync(browser, $"location.host === '{TenantSessionWebHost.AlphaHost}' && !!document.querySelector('form[action=\"/auth/login\"]')", ct);
        await browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[action="/auth/login"]');
              form.querySelector('[name="Email"]').value = {{JsonSerializer.Serialize(owner.Email)}};
              form.querySelector('[name="Password"]').value = {{JsonSerializer.Serialize(TenantSessionWebHost.Password)}};
              form.querySelector('button[type="submit"]').click();
              return true;
            })()
            """, ct);
        await WaitForAsync(browser, $"location.host === '{TenantSessionWebHost.AlphaHost}' && location.pathname === '/dashboard' && document.readyState === 'complete'", ct);

        // The tenant session stays on the tenant host: the application entry still asks for the tenant address.
        await GoAsync(browser, "http://app.wasla.local/dashboard", ct);
        await WaitForAsync(browser, "location.host === 'app.wasla.local' && location.pathname === '/tenant-address-required' && document.readyState === 'complete'", ct);
    }

    [Theory]
    [InlineData("admin.app.wasla.local")]
    [InlineData("alpha.app.wasla.local.evil.example")]
    public async Task ReservedAndLookAlikeHosts_ShowTheTenantNotFoundPage(string host)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", ct);

        await GoAsync(browser, $"http://{host}/auth/login", ct);
        await WaitForAsync(browser, $"location.host === {JsonSerializer.Serialize(host)} && location.pathname === '/tenant-not-found' && document.readyState === 'complete'", ct);

        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('form[action=\"/auth/login\"]')", ct));
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser host checks.");

        // Every Wasla host name, on any port, reaches the in-process server; everything else (the CDN) resolves normally.
        var port = _host.BaseAddress.Port;
        var rules = string.Join(", ", MappedHosts.Select(h => $"MAP {h} 127.0.0.1:{port}"));
        var browser = await HeadlessChromium.StartAsync(executable, [$"--host-resolver-rules={rules}"], ct);
        return await browser.SetUpAsync(async started =>
        {
            var cookie = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            foreach (var host in MappedHosts)
                await started.SetCookieAsync(new Uri($"http://{host}/"), CookieRequestCultureProvider.DefaultCookieName, cookie, ct);
            await started.SetViewportAsync(1280, 860, mobile: false, ct);
        });
    }

    /// <summary>Opens a URL that answers with a redirect; the caller then waits for where the browser ends up.</summary>
    private static Task GoAsync(HeadlessChromium browser, string url, CancellationToken ct) =>
        browser.EvaluateAsync<bool>($"(() => {{ location.href = {JsonSerializer.Serialize(url)}; return true; }})()", ct);

    private static async Task WaitForAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        if (!await browser.WaitForAsync(expression, TimeSpan.FromSeconds(30), ct))
            Assert.Fail($"Waited for {expression}; the browser is at {await browser.EvaluateAsync<string>("location.href", ct)}.");
    }

    private static async Task RequireBootstrapAsync(HeadlessChromium browser, CancellationToken ct)
    {
        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the application pages cannot be exercised.");
    }
}
