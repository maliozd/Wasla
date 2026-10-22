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
/// storefront icon in the Live Screen header in the same box, drawn straight on the header (no plate), beside a readable
/// restaurant name, in light and dark mode,
/// on phones and right to left; it is requested once and never touched while the board refreshes and changes; a logo
/// the browser cannot draw falls back to the storefront icon; and Account Settings previews a chosen file and refuses
/// one of the wrong type before it is sent. Skips only when no Chromium browser is installed or Bootstrap cannot be
/// loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class RestaurantLogoBrowserTests
{
    private const string TenantThemeKey = "Wasla.tenant.theme";

    // The computed colours of the logo background surfaces (wasla-theme.css).
    private const string Transparent = "rgba(0, 0, 0, 0)";
    private const string LightThemeLightSurface = "rgb(255, 255, 255)";
    private const string DarkThemeLightSurface = "rgb(236, 233, 228)";
    private const string DarkSurface = "rgb(35, 38, 44)";

    /// <summary>Each style's surface in the light and the dark theme.</summary>
    private static readonly (string Token, string Light, string Dark)[] Styles =
    [
        ("theme-aware", Transparent, DarkThemeLightSurface),
        ("transparent", Transparent, Transparent),
        ("light", LightThemeLightSurface, DarkThemeLightSurface),
        ("dark", DarkSurface, DarkSurface)
    ];

    [Fact]
    public async Task EveryStyle_InBothThemes_IsOnlyCss_AndNeverRequestsTheLogoAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(
            seed: (db, _) => db.RestaurantLogos.Add(Logo(TestImages.Png(64, 64), RestaurantLogoBackground.Dark)));
        await using var browser = await OpenAsync(host, "en-US", 1280, "light", ct);

        // The server renders the stored style.
        Assert.Equal("wasla-logo-bg--dark", await browser.EvaluateAsync<string>(
            "[...document.querySelector('.wasla-live-display-identity__mark').classList].find(c => c.startsWith('wasla-logo-bg--'))", ct));

        foreach (var (token, light, dark) in Styles)
        {
            foreach (var (theme, expected) in new[] { ("light", light), ("dark", dark) })
            {
                var style = await browser.EvaluateAsync<JsonElement>($$"""
                    (async () => {
                      for (const el of [document.documentElement, document.body]) el.setAttribute('data-bs-theme', '{{theme}}');
                      const mark = document.querySelector('.wasla-live-display-identity__mark');
                      mark.classList.remove(...[...mark.classList].filter(c => c.startsWith('wasla-logo-bg--')));
                      mark.classList.add('wasla-logo-bg--{{token}}');
                      await new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
                      const cs = getComputedStyle(mark), m = mark.getBoundingClientRect(), i = mark.querySelector('img').getBoundingClientRect();
                      return { bg: cs.backgroundColor, shadow: cs.boxShadow, border: cs.borderTopWidth, width: Math.round(m.width), inside: i.width <= m.width && i.height <= m.height };
                    })()
                    """, ct);
                var label = $"{token} in the {theme} theme";
                Assert.True(expected == style.GetProperty("bg").GetString(), $"{label}: background {style.GetProperty("bg").GetString()}, expected {expected}");
                Assert.Equal("0px", style.GetProperty("border").GetString());
                Assert.Equal(48, style.GetProperty("width").GetInt32());
                Assert.True(style.GetProperty("inside").GetBoolean(), $"{label}: the logo spills out of its box");
                if (expected == Transparent)
                    Assert.True(style.GetProperty("shadow").GetString() == "none", $"{label}: a transparent style has a ring");
                else
                    Assert.StartsWith("rgba(", style.GetProperty("shadow").GetString());
            }
        }

        var logoRequests = await browser.EvaluateAsync<int>(
            "performance.getEntriesByType('resource').filter(e => new URL(e.name).pathname === '/restaurant-logo').length", ct);
        Assert.Equal(1, logoRequests);
    }

    [Theory]
    [InlineData("tr-TR", "ltr", "light", 1280)]
    [InlineData("ar-SA", "rtl", "dark", 390)]
    public async Task AccountSettings_PreviewsBothThemesSideBySide_AndRestylesThemAtOnce(string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.RestaurantLogos.Add(Logo(TestImages.Png(64, 64))));
        await using var browser = await OpenAsync(host, culture, width, theme, ct, path: "/settings/account");

        var result = await browser.EvaluateAsync<JsonElement>("""
            (async () => {
              const settle = () => new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
              const tile = t => document.querySelector(`.wasla-logo-theme-preview__header[data-bs-theme="${t}"]`);
              const read = () => ['light', 'dark'].map(t => {
                const mark = tile(t).querySelector('[data-logo-background-target]');
                return { bg: getComputedStyle(mark).backgroundColor, cls: [...mark.classList].find(c => c.startsWith('wasla-logo-bg--')), rect: tile(t).getBoundingClientRect() };
              });
              const requests = () => performance.getEntriesByType('resource').filter(e => new URL(e.name).pathname === '/restaurant-logo').length;
              const choose = async token => { document.getElementById('restaurantLogoBackground-' + token).click(); await settle(); return read(); };
              const label = document.querySelector('label[for="restaurantLogoBackground-theme-aware"]');
              const checkedAtStart = document.querySelector('input[name="background"]:checked').value;
              const before = read(), requestsBefore = requests();
              const dark = await choose('dark');
              const transparent = await choose('transparent');
              const light = await choose('light');
              const themeAware = await choose('theme-aware');
              const [l, d] = themeAware;
              return {
                checkedAtStart, recommended: !!label.querySelector('.badge') && label.querySelector('.badge').textContent.trim(),
                before: before.map(x => x.bg), dark: dark.map(x => x.bg), transparent: transparent.map(x => x.bg), light: light.map(x => x.bg), themeAware: themeAware.map(x => x.bg),
                classes: themeAware.map(x => x.cls),
                sideBySide: Math.abs(l.rect.top - d.rect.top) < 1, stacked: d.rect.top >= l.rect.bottom,
                requestsBefore, requestsAfter: requests(),
                dir: document.documentElement.getAttribute('dir'), overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

        Assert.Equal("theme-aware", result.GetProperty("checkedAtStart").GetString());
        Assert.Equal(Resource(culture, "Settings.RestaurantLogo.Background.Recommended"), result.GetProperty("recommended").GetString());
        // Each preview shows its own theme, whatever theme the page is in, and follows the option at once.
        Assert.Equal(new[] { Transparent, DarkThemeLightSurface }, Colours(result, "before"));
        Assert.Equal(new[] { DarkSurface, DarkSurface }, Colours(result, "dark"));
        Assert.Equal(new[] { Transparent, Transparent }, Colours(result, "transparent"));
        Assert.Equal(new[] { LightThemeLightSurface, DarkThemeLightSurface }, Colours(result, "light"));
        Assert.Equal(new[] { Transparent, DarkThemeLightSurface }, Colours(result, "themeAware"));
        Assert.Equal(new[] { "wasla-logo-bg--theme-aware", "wasla-logo-bg--theme-aware" }, Colours(result, "classes"));
        // Side by side on a desktop, one above the other on a phone.
        Assert.True(width >= 992 ? result.GetProperty("sideBySide").GetBoolean() : result.GetProperty("stacked").GetBoolean(),
            $"{culture} {width}px: the previews are not laid out as expected");
        Assert.Equal(result.GetProperty("requestsBefore").GetInt32(), result.GetProperty("requestsAfter").GetInt32());
        Assert.Equal(direction, result.GetProperty("dir").GetString());
        Assert.False(result.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: the page overflows horizontally");
    }

    private static string[] Colours(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray().Select(x => x.GetString() ?? "").ToArray();

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
                border: getComputedStyle(mark).borderTopWidth,
                shadow: getComputedStyle(mark).boxShadow,
                imgWidth: i ? Math.round(i.width) : 0, imgHeight: i ? Math.round(i.height) : 0,
                filter: img ? getComputedStyle(img).filter : null,
                background: [...mark.classList].find(c => c.startsWith('wasla-logo-bg--')) || null,
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
        // The default style, Theme-aware: no border ever (a plate looked like a button). In the light theme the logo is
        // drawn straight on the header and uses the whole box; in the dark theme it sits on a subtle light surface with a
        // hairline inset ring, so dark logos stay readable. No filter is applied to the image.
        Assert.Equal("wasla-logo-bg--theme-aware", header.GetProperty("background").GetString());
        Assert.Equal("0px", header.GetProperty("border").GetString());
        Assert.Equal("none", header.GetProperty("filter").GetString());
        if (theme == "dark")
        {
            Assert.Equal(DarkThemeLightSurface, header.GetProperty("plate").GetString());
            Assert.Contains("inset", header.GetProperty("shadow").GetString());
            Assert.InRange(header.GetProperty("imgWidth").GetInt32(), header.GetProperty("rem3").GetInt32() - 6, header.GetProperty("rem3").GetInt32() - 3);
        }
        else
        {
            Assert.Equal(Transparent, header.GetProperty("plate").GetString());
            Assert.Equal("none", header.GetProperty("shadow").GetString());
            Assert.Equal(header.GetProperty("rem3").GetInt32(), header.GetProperty("imgWidth").GetInt32());
            Assert.Equal(header.GetProperty("rem3").GetInt32(), header.GetProperty("imgHeight").GetInt32());
        }
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
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.RestaurantLogos.Add(Logo(damaged, RestaurantLogoBackground.Light)));
        await using var browser = await OpenAsync(host, "en-US", 1280, "dark", ct);

        Assert.True(await browser.WaitForAsync("!document.querySelector('.wasla-live-display-identity img')", TimeSpan.FromSeconds(10), ct),
            "the broken logo stayed on the page");
        var mark = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const mark = document.querySelector('.wasla-live-display-identity__mark');
              const icon = mark.querySelector('.bi-shop');
              return { iconShown: !!icon && !icon.hidden && icon.getBoundingClientRect().width > 0,
                       plated: mark.classList.contains('wasla-live-display-identity__mark--logo'),
                       bg: getComputedStyle(mark).backgroundColor,
                       width: Math.round(mark.getBoundingClientRect().width), rem3: Math.round(3 * parseFloat(getComputedStyle(document.documentElement).fontSize)) };
            })()
            """, ct);
        Assert.True(mark.GetProperty("iconShown").GetBoolean());
        Assert.False(mark.GetProperty("plated").GetBoolean());
        // The storefront tile of the dark theme, not the Light logo surface the Owner chose.
        Assert.Equal("rgba(224, 122, 69, 0.14)", mark.GetProperty("bg").GetString());
        Assert.Equal(mark.GetProperty("rem3").GetInt32(), mark.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task AccountSettings_PreviewsAChosenLogo_AndRefusesNoFileOrAWrongTypeBeforeSending()
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
              const before = { icon: !document.querySelector('.wasla-restaurant-logo-frame .bi-shop').hidden, img: !!document.querySelector('.wasla-restaurant-logo-frame img'),
                               invalid: input.matches(':invalid') || input.getAttribute('aria-invalid') === 'true' };
              let submitted = true;
              form.addEventListener('submit', e => { submitted = !e.defaultPrevented; e.preventDefault(); });

              // Saving with no file: the page's own message, not the browser's bubble in the browser's language.
              form.requestSubmit();
              const empty = { submitted, errorShown: !error.hidden, error: error.textContent, invalid: input.getAttribute('aria-invalid') };

              choose(new File([png], 'logo.png', { type: 'image/png' }));
              const preview = document.querySelector('.wasla-restaurant-logo-frame img');
              await new Promise(r => { if (preview.complete) r(); else preview.onload = preview.onerror = r; });
              const previewed = { blob: preview.src.startsWith('blob:'), drawn: preview.naturalWidth > 0, caption: document.querySelector('[data-logo-preview-caption]').textContent.trim(), errorShown: !error.hidden };

              choose(new File(['<svg xmlns="http://www.w3.org/2000/svg"/>'], 'logo.svg', { type: 'image/svg+xml' }));
              submitted = true;
              form.requestSubmit();
              return { before, empty, previewed, refused: { errorShown: !error.hidden, error: error.textContent, invalid: input.getAttribute('aria-invalid'), submitted,
                       previewGone: !document.querySelector('.wasla-restaurant-logo-frame img') },
                       dir: document.documentElement.getAttribute('dir'), overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth };
            })()
            """, ct);

        Assert.True(result.GetProperty("before").GetProperty("icon").GetBoolean());
        Assert.False(result.GetProperty("before").GetProperty("img").GetBoolean());
        Assert.False(result.GetProperty("before").GetProperty("invalid").GetBoolean(), "the untouched file input is already invalid");
        var empty = result.GetProperty("empty");
        Assert.False(empty.GetProperty("submitted").GetBoolean(), "the form was sent without a file");
        Assert.True(empty.GetProperty("errorShown").GetBoolean());
        Assert.Equal(Resource("ar-SA", "Settings.RestaurantLogo.Error.Required"), empty.GetProperty("error").GetString());
        Assert.Equal("true", empty.GetProperty("invalid").GetString());
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

    private static RestaurantLogo Logo(byte[] content, RestaurantLogoBackground background = RestaurantLogoBackground.ThemeAware) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Content = content,
        Background = background,
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
