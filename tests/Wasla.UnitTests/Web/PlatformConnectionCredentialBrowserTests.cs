using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-131 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/>: on Platform Connections create and
/// edit, each credential is a masked, empty field with a reveal control next to it. The control is a real button with a
/// localized name and a pressed state, reached by Tab from its field, works with Space and Enter without submitting the
/// form or requesting anything, and shows only what was typed into the field: on the edit page the stored credential is
/// nowhere in the document. Submitting masks a revealed field again. Checked in Turkish, English, Arabic (right to left)
/// and Russian, in light and dark mode, at desktop and phone widths. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class PlatformConnectionCredentialBrowserTests
{
    private const string TenantThemeKey = "Wasla.tenant.theme";
    private const string Typed = "typed-in-browser-sentinel-6d0e";

    public static TheoryData<string, string, string, int> Layouts() => new()
    {
        { "tr-TR", "ltr", "light", 1280 },
        { "en-US", "ltr", "dark", 1280 },
        { "ar-SA", "rtl", "dark", 390 },
        { "ru-RU", "ltr", "light", 390 }
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task CreateAndEdit_MaskEveryCredential_AndRevealOnlyWhatWasTyped(string culture, string direction, string theme, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenAsync(host, culture, width, theme, ct, "/platform-connections");

        var editPath = await browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('a[href$=\"/edit\"]')].map(a => new URL(a.href).pathname)[0]", ct);
        Assert.False(string.IsNullOrEmpty(editPath), "The connections list links to no edit page.");

        foreach (var path in new[] { "/platform-connections/create", editPath })
        {
            await NavigateAsync(browser, host, path, ct);
            var label = $"{path} in {culture} ({theme}, {width}px)";

            var page = await browser.EvaluateAsync<JsonElement>("""
                (() => {
                  const html = document.documentElement.outerHTML;
                  return {
                    dir: document.documentElement.getAttribute('dir') || 'ltr',
                    overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                    fields: ['ApiKey', 'ApiSecret'].map(id => {
                      const input = document.getElementById(id), toggle = document.querySelector(`[data-credential-toggle][aria-controls="${id}"]`);
                      const i = input.getBoundingClientRect(), t = toggle.getBoundingClientRect();
                      return {
                        type: input.type, value: input.value, autocomplete: input.autocomplete, hidden: toggle.hidden,
                        buttonType: toggle.type, name: toggle.getAttribute('aria-label'), pressed: toggle.getAttribute('aria-pressed'),
                        visible: t.width > 0 && t.height > 0, inViewport: t.left >= 0 && t.right <= document.documentElement.clientWidth,
                        sameRow: Math.abs(t.top - i.top) < 2, toggleAfterInput: t.left >= i.right - 1, toggleBeforeInput: t.right <= i.left + 1
                      };
                    }),
                    storedMarker: html.includes('APIKEY-SECRET-0c9d') || html.includes('APISECRET-SECRET-8e7f')
                  };
                })()
                """, ct);

            Assert.Equal(direction, page.GetProperty("dir").GetString());
            Assert.False(page.GetProperty("overflow").GetBoolean(), $"{label}: the page scrolls sideways.");
            Assert.False(page.GetProperty("storedMarker").GetBoolean(), $"{label}: a stored credential is in the document.");
            var names = new[] { Resource(culture, "PlatformConnections.ShowApiKey"), Resource(culture, "PlatformConnections.ShowApiSecret") };
            var index = 0;
            foreach (var field in page.GetProperty("fields").EnumerateArray())
            {
                Assert.Equal("password", field.GetProperty("type").GetString());
                Assert.Equal("", field.GetProperty("value").GetString());
                Assert.Equal("off", field.GetProperty("autocomplete").GetString());
                Assert.False(field.GetProperty("hidden").GetBoolean(), $"{label}: the reveal control is still hidden.");
                Assert.Equal("button", field.GetProperty("buttonType").GetString());
                Assert.Equal(names[index++], field.GetProperty("name").GetString());
                Assert.Equal("false", field.GetProperty("pressed").GetString());
                Assert.True(field.GetProperty("visible").GetBoolean() && field.GetProperty("inViewport").GetBoolean(), $"{label}: the reveal control is not fully on screen.");
                Assert.True(field.GetProperty("sameRow").GetBoolean(), $"{label}: the reveal control is not beside its field.");
                // At the end of the field in the reading direction.
                Assert.True(field.GetProperty(direction == "rtl" ? "toggleBeforeInput" : "toggleAfterInput").GetBoolean(), $"{label}: the reveal control is on the wrong side.");
            }

            // Type a key, reach its control by Tab, and work it with Space and Enter.
            await browser.EvaluateAsync<bool>($$"""
                (() => {
                  window.__requests = performance.getEntriesByType('resource').length;
                  window.__location = location.href;
                  const input = document.getElementById('ApiKey');
                  input.focus();
                  input.value = '{{Typed}}';
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  return true;
                })()
                """, ct);
            await browser.PressKeyAsync("Tab", shift: false, ct);
            Assert.Equal("toggle:ApiKey", await browser.EvaluateAsync<string>(
                "(document.activeElement.hasAttribute('data-credential-toggle') ? 'toggle:' : 'other:') + (document.activeElement.getAttribute('aria-controls') || document.activeElement.id)", ct));

            await browser.PressKeyAsync(" ", shift: false, ct);
            var revealed = await StateAsync(browser, ct);
            Assert.Equal(("text", Typed, "true", "bi bi-eye-slash"), revealed);

            await browser.PressKeyAsync("Enter", shift: false, ct);
            Assert.Equal(("password", Typed, "false", "bi bi-eye"), await StateAsync(browser, ct));

            var afterKeys = await browser.EvaluateAsync<JsonElement>("""
                (() => ({
                  sameLocation: location.href === window.__location,
                  focused: document.activeElement.getAttribute('aria-controls'),
                  requests: performance.getEntriesByType('resource').length - window.__requests,
                  secretValue: document.getElementById('ApiSecret').value
                }))()
                """, ct);
            Assert.True(afterKeys.GetProperty("sameLocation").GetBoolean(), $"{label}: the reveal control submitted or navigated.");
            Assert.Equal("ApiKey", afterKeys.GetProperty("focused").GetString());
            Assert.Equal(0, afterKeys.GetProperty("requests").GetInt32());

            // Revealing the untouched secret shows that it is empty: there is nothing stored in the page to reveal.
            var secret = await browser.EvaluateAsync<JsonElement>("""
                (() => {
                  document.querySelector('[data-credential-toggle][aria-controls="ApiSecret"]').click();
                  const input = document.getElementById('ApiSecret');
                  return { type: input.type, value: input.value, requests: performance.getEntriesByType('resource').length - window.__requests };
                })()
                """, ct);
            Assert.Equal("text", secret.GetProperty("type").GetString());
            Assert.Equal("", secret.GetProperty("value").GetString());
            Assert.Equal(0, secret.GetProperty("requests").GetInt32());

            // Revealed fields are masked again when the form is submitted (the event alone; nothing is sent).
            var masked = await browser.EvaluateAsync<JsonElement>("""
                (() => {
                  document.querySelector('[data-credential-toggle][aria-controls="ApiKey"]').click();
                  const form = document.getElementById('ApiKey').form;
                  form.addEventListener('submit', e => e.preventDefault(), { once: true });
                  form.dispatchEvent(new Event('submit', { cancelable: true }));
                  return ['ApiKey', 'ApiSecret'].map(id => ({
                    type: document.getElementById(id).type,
                    pressed: document.querySelector(`[data-credential-toggle][aria-controls="${id}"]`).getAttribute('aria-pressed')
                  }));
                })()
                """, ct);
            foreach (var field in masked.EnumerateArray())
                Assert.Equal(("password", "false"), (field.GetProperty("type").GetString(), field.GetProperty("pressed").GetString()));

            // The control and the field stay readable in this theme. The focused control is still in its colour transition
            // after the keys above, so the colours are read once it has settled: a value read mid-transition is neither the
            // focused nor the resting colour. Bootstrap's focus colour is a color-mix(), which the browser reports as
            // color(srgb …); the shared library parses it and composites translucent backgrounds.
            var contrast = await browser.EvaluateAsync<JsonElement>($$"""
                (async () => {
                  {{TenantDarkModeBrowserTests.Library}}
                  const input = document.getElementById('ApiKey'), toggle = document.querySelector('[data-credential-toggle][aria-controls="ApiKey"]');
                  await Promise.all([input, toggle].flatMap(el => el.getAnimations({ subtree: true })).map(a => a.finished.catch(() => null)));
                  return {
                    input: ratio(parse(getComputedStyle(input).color), behind(input)),
                    toggle: ratio(parse(getComputedStyle(toggle.querySelector('.bi')).color), behind(toggle))
                  };
                })()
                """, ct);
            Assert.True(contrast.GetProperty("input").GetDouble() >= 4.5, $"{label}: field text contrast {contrast.GetProperty("input").GetDouble():0.00}.");
            Assert.True(contrast.GetProperty("toggle").GetDouble() >= 3, $"{label}: reveal icon contrast {contrast.GetProperty("toggle").GetDouble():0.00}.");

            if (path == editPath)
            {
                var text = await browser.EvaluateAsync<string>("document.getElementById('apiKeyStored').textContent", ct);
                Assert.Contains(Resource(culture, "PlatformConnections.CredentialSaved"), text);
            }
        }
    }

    private static async Task<(string?, string?, string?, string?)> StateAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var state = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const input = document.getElementById('ApiKey'), toggle = document.querySelector('[data-credential-toggle][aria-controls="ApiKey"]');
              return { type: input.type, value: input.value, pressed: toggle.getAttribute('aria-pressed'), icon: toggle.querySelector('.bi').className };
            })()
            """, ct);
        return (state.GetProperty("type").GetString(), state.GetProperty("value").GetString(), state.GetProperty("pressed").GetString(), state.GetProperty("icon").GetString());
    }

    private static async Task<HeadlessChromium> OpenAsync(TenantVisualWebHost host, string culture, int width, string theme, CancellationToken ct, string path)
    {
        var browser = await host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser credential checks.");
        browser = await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(theme, ct);
            await started.NavigateAsync(new Uri(host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>($"(() => {{ localStorage.clear(); localStorage.setItem('{TenantThemeKey}', '{theme}'); return true; }})()", ct);
        });

        try
        {
            await NavigateAsync(browser, host, path, ct);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    private static async Task NavigateAsync(HeadlessChromium browser, TenantVisualWebHost host, string path, CancellationToken ct)
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
        Assert.True(await browser.WaitForAsync("document.readyState === 'complete'", TimeSpan.FromSeconds(15), ct), $"{path} did not finish loading. {host.ServerErrors}");
    }

    private static string Resource(string culture, string key) =>
        System.Xml.Linq.XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
