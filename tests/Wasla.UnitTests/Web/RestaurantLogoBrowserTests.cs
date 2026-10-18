using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Branding;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Branding;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-127 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/>: the restaurant logo replaces the
/// storefront icon in the Live Screen header in the same box, beside a readable restaurant name, in light and dark mode,
/// on phones and right to left; it is requested once and never touched while the board refreshes and changes; a logo
/// the browser cannot draw falls back to the storefront icon; and Account Settings previews a chosen file and refuses
/// one of the wrong type before it is sent. Skips only when no Chromium browser is installed or Bootstrap cannot be
/// loaded from its CDN.
/// </summary>
public sealed class RestaurantLogoBrowserTests
{
    private const string TenantThemeKey = "Wasla.tenant.theme";

    [Theory]
    [InlineData("tr-TR", "ltr", "light", 1280)]
    [InlineData("en-US", "ltr", "dark", 768)]
    [InlineData("ru-RU", "ltr", "light", 390)]
    [InlineData("ar-SA", "rtl", "dark", 390)]
    public async Task TheLogo_ReplacesTheStorefrontIcon_BesideAReadableName(string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.RestaurantLogos.Add(Logo(TestImages.Png(160, 80))));
        await using var browser = await OpenAsync(host, culture, width, theme, ct);

        var header = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const mark = document.querySelector('.wasla-live-display-identity__mark');
              const img = mark && mark.querySelector('img[data-restaurant-logo]');
              const icon = mark && mark.querySelector('.bi-shop');
              const title = document.querySelector('.wasla-live-display-header__title');
              const m = mark.getBoundingClientRect(), i = img ? img.getBoundingClientRect() : null, t = title.getBoundingClientRect();
              const overlap = !(m.right <= t.left || t.right <= m.left || m.bottom <= t.top || t.bottom <= m.top);
              return {
                drawn: !!img && img.complete && img.naturalWidth > 0,
                markWidth: Math.round(m.width), markHeight: Math.round(m.height), rem3: Math.round(3 * parseFloat(getComputedStyle(document.documentElement).fontSize)),
                imgInside: !!i && i.left >= m.left - 0.5 && i.right <= m.right + 0.5 && i.top >= m.top - 0.5 && i.bottom <= m.bottom + 0.5,
                fit: img ? getComputedStyle(img).objectFit : null,
                alt: img ? img.getAttribute('alt') : null,
                markHidden: mark.getAttribute('aria-hidden'),
                iconShown: !!icon && !icon.hidden && icon.getBoundingClientRect().width > 0,
                plate: getComputedStyle(mark).backgroundColor,
                titleText: title.textContent.trim(), titleVisible: t.width > 0 && t.height > 0, overlap,
                markBeforeTitle: document.documentElement.dir === 'rtl' ? m.left >= t.right - 1 : m.right <= t.left + 1,
                dir: document.documentElement.getAttribute('dir'),
                theme: document.documentElement.getAttribute('data-bs-theme'),
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

        var page = $"{culture} {theme} {width}px";
        Assert.True(header.GetProperty("drawn").GetBoolean(), $"{page}: the logo was not drawn. {host.ServerErrors}");
        // The storefront icon's own 3rem box (smaller on phones, where the root font is smaller).
        Assert.Equal(header.GetProperty("rem3").GetInt32(), header.GetProperty("markWidth").GetInt32());
        Assert.Equal(header.GetProperty("rem3").GetInt32(), header.GetProperty("markHeight").GetInt32());
        Assert.True(header.GetProperty("imgInside").GetBoolean(), $"{page}: the logo spills out of its box");
        Assert.Equal("contain", header.GetProperty("fit").GetString());
        Assert.Equal("", header.GetProperty("alt").GetString());
        Assert.Equal("true", header.GetProperty("markHidden").GetString());
        Assert.False(header.GetProperty("iconShown").GetBoolean(), $"{page}: the storefront icon is shown beside the logo");
        Assert.Equal("rgb(255, 255, 255)", header.GetProperty("plate").GetString());
        Assert.False(string.IsNullOrEmpty(header.GetProperty("titleText").GetString()));
        Assert.True(header.GetProperty("titleVisible").GetBoolean());
        Assert.False(header.GetProperty("overlap").GetBoolean(), $"{page}: the logo overlaps the restaurant name");
        Assert.True(header.GetProperty("markBeforeTitle").GetBoolean(), $"{page}: the logo is not at the start of the name");
        Assert.Equal(direction, header.GetProperty("dir").GetString());
        Assert.Equal(theme, header.GetProperty("theme").GetString());
        Assert.False(header.GetProperty("overflow").GetBoolean(), $"{page}: the page overflows horizontally");
    }

    [Fact]
    public async Task TheLogo_IsRequestedOnce_AndUntouched_WhileTheBoardRefreshesAndChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.RestaurantLogos.Add(Logo(TestImages.Png(64, 64))));
        await using var browser = await OpenAsync(host, "tr-TR", 1280, "light", ct);

        await browser.EvaluateAsync<bool>("""
            (() => {
              // The logo box only: the sync status and "last updated" beside it change with every snapshot by design.
              const mark = document.querySelector('.wasla-live-display-identity__mark');
              window.__logo = { node: mark.querySelector('img[data-restaurant-logo]'), mutations: 0 };
              new MutationObserver(list => { window.__logo.mutations += list.length; })
                .observe(mark, { childList: true, subtree: true, attributes: true, characterData: true });
              return true;
            })()
            """, ct);

        // Unchanged snapshots, then one where an order moved on the board.
        await browser.EvaluateAsync<bool>("(async () => { for (let i = 0; i < 3; i++) await WaslaOrders.liveStore.requestRefresh(); return true; })()", ct);
        await using (var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct))
        {
            var order = await db.Orders.Where(o => o.InternalStatus == OrderStatus.New).OrderBy(o => o.Id).FirstAsync(ct);
            order.InternalStatus = OrderStatus.Preparing;
            order.ChangeVersion++;
            await db.SaveChangesAsync(ct);
        }

        var result = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              const board = document.getElementById('ordersLiveScreenHost');
              let boardChanges = 0;
              const watch = new MutationObserver(list => { boardChanges += list.length; });
              watch.observe(board, { childList: true, subtree: true });
              const changed = await WaslaOrders.liveStore.requestRefresh();
              for (let i = 0; i < 2; i++) await WaslaOrders.liveStore.requestRefresh();
              await new Promise(r => requestAnimationFrame(() => setTimeout(r, 50)));
              watch.disconnect();
              const img = document.querySelector('.wasla-live-display-identity img[data-restaurant-logo]');
              return {
                applied: !!(changed && changed.applied),
                boardChanges,
                sameNode: img === window.__logo.node,
                mutations: window.__logo.mutations,
                logoRequests: performance.getEntriesByType('resource').filter(e => new URL(e.name).pathname === '/restaurant-logo').length,
                logoEntries: JSON.stringify(performance.getEntriesByType('resource').filter(e => new URL(e.name).pathname === '/restaurant-logo').map(e => ({ n: e.name, i: e.initiatorType, s: Math.round(e.startTime), t: e.transferSize, d: e.decodedBodySize }))),
                snapshots: performance.getEntriesByType('resource').filter(e => e.name.indexOf('/orders/live-data') >= 0).length
              };
            })()
            """, ct);

        Assert.True(result.GetProperty("applied").GetBoolean(), $"the changed snapshot was not applied. {host.ServerErrors}");
        Assert.True(result.GetProperty("boardChanges").GetInt32() > 0, "the board did not change, so nothing was proven");
        Assert.True(result.GetProperty("snapshots").GetInt32() >= 6, $"only {result.GetProperty("snapshots").GetInt32()} snapshots were read");
        Assert.True(result.GetProperty("sameNode").GetBoolean(), "the logo element was replaced");
        Assert.Equal(0, result.GetProperty("mutations").GetInt32());
        Assert.True(1 == result.GetProperty("logoRequests").GetInt32(), result.GetProperty("logoEntries").GetString());
    }

    [Fact]
    public async Task ALogoTheBrowserCannotDraw_LeavesTheStorefrontIcon()
    {
        var ct = TestContext.Current.CancellationToken;
        // Stored behind the upload checks' back: the image request answers with something that is not an image (as it
        // would after an expired session or a removed logo).
        var damaged = "not an image"u8.ToArray();
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.RestaurantLogos.Add(Logo(damaged)));
        await using var browser = await OpenAsync(host, "en-US", 1280, "dark", ct);

        Assert.True(await browser.WaitForAsync("!document.querySelector('.wasla-live-display-identity img')", TimeSpan.FromSeconds(10), ct),
            "the broken logo stayed on the page");
        var mark = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const mark = document.querySelector('.wasla-live-display-identity__mark');
              const icon = mark.querySelector('.bi-shop');
              return { iconShown: !!icon && !icon.hidden && icon.getBoundingClientRect().width > 0,
                       plated: mark.classList.contains('wasla-live-display-identity__mark--logo'),
                       width: Math.round(mark.getBoundingClientRect().width), rem3: Math.round(3 * parseFloat(getComputedStyle(document.documentElement).fontSize)) };
            })()
            """, ct);
        Assert.True(mark.GetProperty("iconShown").GetBoolean());
        Assert.False(mark.GetProperty("plated").GetBoolean());
        Assert.Equal(mark.GetProperty("rem3").GetInt32(), mark.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task AccountSettings_PreviewsAChosenLogo_AndRefusesAWrongTypeBeforeSending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenAsync(host, "ar-SA", 390, "light", ct, path: "/settings/account");

        var result = await browser.EvaluateAsync<JsonElement>($$"""
            (async () => {
              const input = document.getElementById('restaurantLogoFile');
              const form = document.getElementById('restaurantLogoUploadForm');
              const error = document.getElementById('restaurantLogoClientError');
              const choose = file => { const dt = new DataTransfer(); dt.items.add(file); input.files = dt.files; input.dispatchEvent(new Event('change', { bubbles: true })); };
              const png = Uint8Array.from(atob('{{Convert.ToBase64String(TestImages.Png(40, 40))}}'), c => c.charCodeAt(0));
              const before = { icon: !document.querySelector('.wasla-restaurant-logo-frame .bi-shop').hidden, img: !!document.querySelector('.wasla-restaurant-logo-frame img') };

              choose(new File([png], 'logo.png', { type: 'image/png' }));
              const preview = document.querySelector('.wasla-restaurant-logo-frame img');
              await new Promise(r => { if (preview.complete) r(); else preview.onload = preview.onerror = r; });
              const previewed = { blob: preview.src.startsWith('blob:'), drawn: preview.naturalWidth > 0, caption: document.querySelector('[data-logo-preview-caption]').textContent.trim(), errorShown: !error.hidden };

              choose(new File(['<svg xmlns="http://www.w3.org/2000/svg"/>'], 'logo.svg', { type: 'image/svg+xml' }));
              let submitted = true;
              form.addEventListener('submit', e => { submitted = !e.defaultPrevented; e.preventDefault(); });
              form.requestSubmit();
              return { before, previewed, refused: { errorShown: !error.hidden, error: error.textContent, invalid: input.getAttribute('aria-invalid'), submitted,
                       previewGone: !document.querySelector('.wasla-restaurant-logo-frame img') },
                       dir: document.documentElement.getAttribute('dir'), overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth };
            })()
            """, ct);

        Assert.True(result.GetProperty("before").GetProperty("icon").GetBoolean());
        Assert.False(result.GetProperty("before").GetProperty("img").GetBoolean());
        var previewed = result.GetProperty("previewed");
        Assert.True(previewed.GetProperty("blob").GetBoolean());
        Assert.True(previewed.GetProperty("drawn").GetBoolean());
        Assert.False(previewed.GetProperty("errorShown").GetBoolean());
        Assert.Equal(Resource("ar-SA", "Settings.RestaurantLogo.Preview"), previewed.GetProperty("caption").GetString());
        var refused = result.GetProperty("refused");
        Assert.True(refused.GetProperty("errorShown").GetBoolean());
        Assert.Equal(Resource("ar-SA", "Settings.RestaurantLogo.Error.UnsupportedFormat"), refused.GetProperty("error").GetString());
        Assert.Equal("true", refused.GetProperty("invalid").GetString());
        Assert.False(refused.GetProperty("submitted").GetBoolean(), "a wrong file type was sent");
        Assert.True(refused.GetProperty("previewGone").GetBoolean());
        Assert.Equal("rtl", result.GetProperty("dir").GetString());
        Assert.False(result.GetProperty("overflow").GetBoolean());
    }

    private static RestaurantLogo Logo(byte[] content) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Content = content,
        ContentType = RestaurantLogoImage.Png,
        Version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content), 0, 16).ToLowerInvariant()
    };

    private static async Task<HeadlessChromium> OpenAsync(
        TenantVisualWebHost host, string culture, int width, string theme, CancellationToken ct, string path = "/orders/live-display")
    {
        var browser = await host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser restaurant logo checks.");
        browser = await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(theme, ct);
            await started.NavigateAsync(new Uri(host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); localStorage.setItem('{TenantThemeKey}', '{theme}'); return true; }})()", ct);
        });

        try
        {
            // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
            try
            {
                await browser.NavigateAsync(new Uri(host.BaseAddress, path), ct);
            }
            catch (TimeoutException)
            {
                await browser.NavigateAsync(new Uri(host.BaseAddress, path), ct);
            }

            if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            if (path == "/orders/live-display")
            {
                Assert.True(await browser.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-detail]').length >= 5", TimeSpan.FromSeconds(15), ct),
                    $"The Live Screen did not render the seeded orders. {host.ServerErrors}");
            }
            else
            {
                Assert.True(await browser.WaitForAsync("document.readyState === 'complete'", TimeSpan.FromSeconds(15), ct));
            }

            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    private static string Resource(string culture, string key) =>
        System.Xml.Linq.XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
