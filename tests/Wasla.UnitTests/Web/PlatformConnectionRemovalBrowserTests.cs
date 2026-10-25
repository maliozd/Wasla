using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// GitHub #72 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views,
/// stylesheets and scripts over seeded SQLite databases with fake data). Every Platform Connections row (active, circuit
/// open and inactive) has Remove beside Edit with the same height and centre, readable in both themes. Remove opens a
/// confirmation naming the provider and Store ID; Cancel closes it, gives focus back to Remove and changes nothing; the
/// dialog's Remove removes only that connection and the list says so. Checked in Turkish, English, Arabic (right to left)
/// and Russian, in light and dark mode, at desktop and phone widths. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class PlatformConnectionRemovalBrowserTests
{
    private const string TenantThemeKey = "Wasla.tenant.theme";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string Instrumentation = """
        window.__shown = 0;
        window.__hidden = 0;
        document.addEventListener('shown.bs.modal', function () { window.__shown++; });
        document.addEventListener('hidden.bs.modal', function () { window.__hidden++; });
        """;

    public static TheoryData<string, string, string, int> Layouts() => new()
    {
        { "tr-TR", "ltr", "light", 1280 },
        { "en-US", "ltr", "dark", 1280 },
        { "ar-SA", "rtl", "dark", 390 },
        { "ru-RU", "ltr", "light", 390 }
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task RemoveSitsBesideEdit_AsksFirst_CancelChangesNothing_AndConfirmRemovesOnlyThatConnection(
        string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        var label = $"{culture} {theme} {width}px";
        // The standard seed has an active Yemeksepeti and a circuit-open Trendyol connection; add an inactive one.
        await using var host = await TenantVisualWebHost.StartAsync(seed: (db, _) => db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.GetirYemek, StoreId = "GY-3003", EncryptedApiKey = SecretMarkers.ApiKey,
            EncryptedApiSecret = SecretMarkers.ApiSecret, IsActive = false
        }));
        await using var browser = await OpenAsync(host, culture, width, theme, ct);
        var trendyol = (await ConnectionsAsync(host, ct)).Single(c => c.Platform == FoodPlatform.TrendyolYemek).Id;

        var rows = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              {{TenantDarkModeBrowserTests.Library}}
              const width = document.documentElement.clientWidth;
              return {
                dir: document.documentElement.getAttribute('dir') || 'ltr',
                overflow: document.documentElement.scrollWidth > width,
                rows: [...document.querySelectorAll('.wasla-connections tbody tr')].map(tr => {
                  const edit = tr.querySelector('a[href$="/edit"]'), remove = tr.querySelector('[data-connection-remove]');
                  const e = edit.getBoundingClientRect(), r = remove.getBoundingClientRect();
                  return {
                    heights: Math.abs(e.height - r.height), centres: Math.abs((e.top + e.height / 2) - (r.top + r.height / 2)),
                    removeAfterEdit: r.left >= e.right - 1, removeBeforeEdit: r.right <= e.left + 1,
                    onScreen: e.left >= 0 && r.left >= 0 && e.right <= width && r.right <= width,
                    contrast: ratio(parse(getComputedStyle(remove).color), behind(remove)),
                    text: remove.textContent.trim()
                  };
                }),
                stored: document.documentElement.outerHTML.includes('APIKEY-SECRET-0c9d') || document.documentElement.outerHTML.includes('APISECRET-SECRET-8e7f')
              };
            })()
            """, ct);

        Assert.Equal(direction, rows.GetProperty("dir").GetString());
        Assert.False(rows.GetProperty("overflow").GetBoolean(), $"{label}: the page scrolls sideways.");
        Assert.False(rows.GetProperty("stored").GetBoolean(), $"{label}: a stored credential is in the document.");
        Assert.Equal(3, rows.GetProperty("rows").GetArrayLength());
        foreach (var row in rows.GetProperty("rows").EnumerateArray())
        {
            Assert.True(row.GetProperty("heights").GetDouble() <= 0.5 && row.GetProperty("centres").GetDouble() <= 1, $"{label}: Edit and Remove are not aligned.");
            // Remove follows Edit in the reading direction.
            Assert.True(row.GetProperty(direction == "rtl" ? "removeBeforeEdit" : "removeAfterEdit").GetBoolean(), $"{label}: Remove is not after Edit.");
            Assert.True(row.GetProperty("onScreen").GetBoolean(), $"{label}: a row action is off screen.");
            Assert.True(row.GetProperty("contrast").GetDouble() >= 4.5, $"{label}: Remove text contrast {row.GetProperty("contrast").GetDouble():0.00}.");
            Assert.Equal(Resource(culture, "PlatformConnections.Remove"), row.GetProperty("text").GetString());
        }

        // Cancel: the dialog names the connection, closes, gives focus back to Remove and changes nothing.
        var remove = $"[data-connection-remove=\"{trendyol}\"]";
        var dialog = $"#pc-remove-{trendyol}";
        await ClickAsync(browser, remove, ct);
        Assert.True(await browser.WaitForAsync("window.__shown === 1", Wait, ct), $"{label}: Remove did not open the confirmation.");
        var text = await browser.EvaluateAsync<string>($"document.querySelector('{dialog} .modal-body').innerText", ct);
        Assert.Contains("TrendyolYemek", text);
        Assert.Contains("TY-2002", text);
        Assert.Contains(Resource(culture, "PlatformConnections.RemoveConfirmStopsSync"), text);
        Assert.True(await browser.EvaluateAsync<bool>($"document.querySelector('{dialog}').contains(document.activeElement)", ct), $"{label}: focus is not in the dialog.");
        await ClickAsync(browser, $"{dialog} .modal-footer button[data-bs-dismiss]", ct);
        Assert.True(await browser.WaitForAsync("window.__hidden === 1 && !document.body.classList.contains('modal-open')", Wait, ct), $"{label}: Cancel did not close the dialog.");
        Assert.True(await browser.WaitForAsync($"document.activeElement === document.querySelector('{remove}')", Wait, ct), $"{label}: focus did not return to Remove.");
        Assert.Equal(3, (await ConnectionsAsync(host, ct)).Count);

        // Escape closes it the same way.
        await ClickAsync(browser, remove, ct);
        Assert.True(await browser.WaitForAsync("window.__shown === 2", Wait, ct), $"{label}: Remove did not open the confirmation again.");
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("window.__hidden === 2 && !document.body.classList.contains('modal-open')", Wait, ct), $"{label}: Escape did not close the dialog.");
        Assert.True(await browser.WaitForAsync($"document.activeElement === document.querySelector('{remove}')", Wait, ct), $"{label}: focus did not return to Remove after Escape.");
        Assert.Equal(3, (await ConnectionsAsync(host, ct)).Count);

        // Confirm: only that connection goes, and the list says it was removed.
        await ClickAsync(browser, remove, ct);
        Assert.True(await browser.WaitForAsync("window.__shown === 3", Wait, ct), $"{label}: Remove did not open the confirmation again.");
        await browser.EvaluateAsync<bool>("(window.__leaving = true)", ct);
        await ClickAsync(browser, $"{dialog} [data-connection-remove-confirm]", ct);
        var removed = Resource(culture, "PlatformConnections.Removed");
        Assert.True(await WaitAcrossNavigationAsync(browser,
                $"!window.__leaving && document.readyState === 'complete' && document.body.innerText.includes({JsonSerializer.Serialize(removed)})", ct),
            $"{label}: the list did not confirm the removal. {host.ServerErrors}");
        Assert.Equal(2, await browser.EvaluateAsync<int>("document.querySelectorAll('.wasla-connections tbody tr').length", ct));
        Assert.False(await browser.EvaluateAsync<bool>("document.querySelector('.wasla-connections tbody').innerText.includes('TY-2002')", ct));
        Assert.Equal([FoodPlatform.Yemeksepeti, FoodPlatform.GetirYemek], (await ConnectionsAsync(host, ct)).Select(c => c.Platform).Order());
        Assert.Null(host.ServerErrors);
    }

    private static async Task<IReadOnlyList<PlatformConnectionResult>> ConnectionsAsync(TenantVisualWebHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPlatformConnectionService>().GetListAsync(TenantVisualWebHost.TenantId, ct);
    }

    /// <summary>A real mouse click at the centre of the first visible match, after checking nothing covers it.</summary>
    private static async Task ClickAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var target = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const e = [...document.querySelectorAll({{JsonSerializer.Serialize(selector)}})]
                .find(x => { const r = x.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(x).visibility !== 'hidden'; });
              if (!e) return { found: false };
              // Instant, so the position read below is where the element stays (Bootstrap scrolls smoothly by default).
              e.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
              const r = e.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              window.__clickTarget = e;
              window.__clicked = null;
              document.addEventListener('click', ev => { window.__clicked = ev.target; }, { capture: true, once: true });
              return { found: true, x, y, hit: !!hit && e.contains(hit) };
            })()
            """, ct);
        Assert.True(target.GetProperty("found").GetBoolean(), $"{selector} is not on the page.");
        Assert.True(target.GetProperty("hit").GetBoolean(), $"{selector} is covered.");
        await browser.ClickAtAsync(target.GetProperty("x").GetDouble(), target.GetProperty("y").GetDouble(), ct);
        // The submit button's click leaves the page, so only a control that stays is checked.
        if (!selector.EndsWith("[data-connection-remove-confirm]", StringComparison.Ordinal))
            Assert.True(await browser.EvaluateAsync<bool>("!!window.__clicked && window.__clickTarget.contains(window.__clicked)", ct),
                $"The click aimed at {selector} landed elsewhere.");
    }

    private static async Task<bool> WaitAcrossNavigationAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await browser.EvaluateAsync<bool>(expression, ct))
                    return true;
            }
            catch (InvalidOperationException)
            {
                // The document is being replaced.
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    private static async Task<HeadlessChromium> OpenAsync(TenantVisualWebHost host, string culture, int width, string theme, CancellationToken ct)
    {
        var browser = await host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser checks.");

        return await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(theme, ct);
            await started.NavigateAsync(new Uri(host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); localStorage.setItem('{TenantThemeKey}', '{theme}'); return true; }})()", ct);
            await started.AddScriptBeforePageScriptsAsync(Instrumentation, ct);
            try
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/platform-connections"), ct);
            }
            catch (TimeoutException)
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/platform-connections"), ct);
            }

            if (!await started.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", Wait, ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            Assert.True(await started.WaitForAsync("document.readyState === 'complete' && !!window.bootstrap", Wait, ct),
                $"Platform Connections did not finish loading. {host.ServerErrors}");
        });
    }

    private static string Resource(string culture, string key) =>
        System.Xml.Linq.XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
