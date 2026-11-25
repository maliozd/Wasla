using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;
using Wasla.UnitTests.Contact;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The marketing pages in a real headless Chromium browser that reaches only the test host: every other host name,
/// jsDelivr's included, resolves to nothing (WAS-248). On the coming-soon page, the full site and the pilot pages the
/// browser requests nothing from another origin, Bootstrap's Reboot applies, and the Bootstrap Icons font loads from the
/// site's pinned copy and draws the icons, flipped right to left where the page asks for it. Served by
/// <see cref="TenantSessionWebHost"/> (Production, the real controllers, views, styles and scripts). Skips only when no
/// Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>).
/// </summary>
[Trait("Category", "Browser")]
public sealed class MarketingVendorFilesBrowserTests
{
    private const string MarketingHost = "wasla.local";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string IconFontLoaded =
        "[...document.fonts].some(f => f.family.replace(/[\"']/g, '') === 'bootstrap-icons' && f.status === 'loaded')";

    private static readonly string State = $$"""
        (async () => {
          await document.fonts.ready;
          const origin = location.origin;
          const resources = performance.getEntriesByType('resource').map(e => e.name);
          const sheets = [...document.styleSheets].map(s => s.href).filter(Boolean);
          const visible = e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
          const icon = [...document.querySelectorAll('.bi')].find(visible);
          const flip = [...document.querySelectorAll('.wm-flip-rtl')].find(visible);
          return JSON.stringify({
            dir: document.documentElement.dir,
            foreign: [...resources, ...sheets].filter(u => new URL(u).origin !== origin),
            iconFontFile: resources.some(u => new URL(u).pathname === '{{MarketingVendorFilesTests.IconsFont}}'),
            iconFontLoaded: {{IconFontLoaded}},
            iconFamily: icon ? getComputedStyle(icon, '::before').fontFamily : '',
            iconWidth: icon ? icon.getBoundingClientRect().width : 0,
            flip: flip ? getComputedStyle(flip).transform : '',
            bodyMargin: getComputedStyle(document.body).margin,
            boxSizing: getComputedStyle(document.querySelector('main')).boxSizing
          });
        })()
        """;

    [Theory]
    [InlineData(false, "tr-TR", "ltr", 1440, 900, false)]
    [InlineData(true, "tr-TR", "ltr", 1440, 900, false)]
    [InlineData(true, "ar-SA", "rtl", 390, 844, true)]
    public async Task MarketingPages_UseTheSitesOwnBootstrapAndIconFont_AndNoOtherOrigin(
        bool fullSite, string culture, string dir, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new Dictionary<string, string?>
        {
            ["Wasla:PilotApplication:Enabled"] = "true",
            [MarketingSiteOptions.SectionName + ":" + nameof(MarketingSiteOptions.FullSiteEnabled)] = fullSite ? "true" : "false"
        };
        var notice = PilotApplicationModelTests.CompleteNotice();
        foreach (var property in typeof(PilotPrivacyNoticeOptions).GetProperties())
            settings[$"Wasla:PilotApplication:PrivacyNotice:{property.Name}"] = property.GetValue(notice) as string;
        await using var host = await TenantSessionWebHost.StartAsync(configuration: settings);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            // The pilot pages open only with the complete catalog of 81 provinces.
            db.Cities.AddRange(PilotProvinceSeed.All(idOffset: 1000));
            await db.SaveChangesAsync(ct);
        }

        await using var browser = await BrowserAsync(host, culture, width, height, mobile, ct);
        string[] paths = fullSite
            ? ["/", PublicSiteUrls.PosPath, PublicSiteUrls.ContactPath, PublicSiteUrls.PrivacyNoticePath, PublicSiteUrls.PilotPath, PublicSiteUrls.PilotPrivacyNoticePath]
            : ["/", PublicSiteUrls.PilotPath, PublicSiteUrls.PilotPrivacyNoticePath];

        foreach (var path in paths)
        {
            await browser.NavigateAsync(new Uri($"http://{MarketingHost}{path}"), ct);
            var ready = await browser.WaitForAsync($"document.readyState === 'complete' && {IconFontLoaded}", Wait, ct);
            var raw = await browser.EvaluateAsync<string>(State, ct);
            var state = JsonDocument.Parse(raw).RootElement;
            var at = $"{path} ({culture}, {width} px): {raw}";

            Assert.True(ready, $"Timed out waiting for the page and the icon font on {at}");
            Assert.True(state.GetProperty("dir").GetString() == dir, at);
            Assert.True(state.GetProperty("foreign").GetArrayLength() == 0, $"Requests to another origin on {at}");
            // Bootstrap's Reboot, which the wm- styles build on.
            Assert.True(state.GetProperty("bodyMargin").GetString() == "0px", at);
            Assert.True(state.GetProperty("boxSizing").GetString() == "border-box", at);
            // The icon font comes from the site's copy and draws a visible icon.
            Assert.True(state.GetProperty("iconFontFile").GetBoolean(), at);
            Assert.True(state.GetProperty("iconFontLoaded").GetBoolean(), at);
            Assert.Contains("bootstrap-icons", state.GetProperty("iconFamily").GetString(), StringComparison.Ordinal);
            Assert.True(state.GetProperty("iconWidth").GetDouble() > 0, at);
            var flip = state.GetProperty("flip").GetString();
            if (flip!.Length > 0)
                Assert.True(flip == (dir == "rtl" ? "matrix(-1, 0, 0, 1, 0, 0)" : "none"), at);
        }
    }

    private static async Task<HeadlessChromium> BrowserAsync(TenantSessionWebHost host, string culture, int width, int height, bool mobile, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the marketing vendor file checks.");

        // The marketing host reaches the in-process server; every other host name (the CDN's included) resolves to nothing.
        var browser = await HeadlessChromium.StartAsync(
            executable, [$"--host-resolver-rules=MAP {MarketingHost} 127.0.0.1:{host.BaseAddress.Port}, MAP * ~NOTFOUND"], ct);
        return await browser.SetUpAsync(async started =>
        {
            var cookie = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            await started.SetCookieAsync(new Uri($"http://{MarketingHost}/"), CookieRequestCultureProvider.DefaultCookieName, cookie, ct);
            await started.SetViewportAsync(width, height, mobile, ct);
        });
    }
}
