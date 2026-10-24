using System.Text.Json;
using System.Xml.Linq;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-12 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/>, whose tenant has a Yemeksepeti
/// connection that synced 30 seconds ago and a Trendyol GO connection paused by the circuit breaker after repeated
/// failures. The Live Screen header used to say "Live" whatever the connections did; it must show the connection issue
/// the backend reports, name the affected platform in the page's language, keep it readable right to left, and show the
/// last successful synchronization from the connection records. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenSyncStatusBrowserTests : IAsyncLifetime
{
    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("tr-TR", "ltr", 1280)]
    [InlineData("ru-RU", "ltr", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    public async Task WithAPausedConnection_TheHeaderShowsTheConnectionIssue_NotLive(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        await LoadAsync(browser, "/orders/live-display", ct);
        Assert.True(await browser.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-detail]').length >= 5", TimeSpan.FromSeconds(15), ct),
            $"The Live Screen did not render the seeded orders. {_host.ServerErrors}");

        var header = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const status = document.querySelector('.wasla-live-display-status');
              const time = document.getElementById('ordersLiveDisplayLastSync');
              const item = document.getElementById('ordersLiveDisplayLastSyncItem');
              return {
                text: status ? status.textContent.replace(/\s+/g, ' ').trim() : null,
                role: status ? status.getAttribute('role') : null,
                state: status ? status.getAttribute('data-sync-state') : null,
                iconHidden: status ? [...status.querySelectorAll('i, .wasla-live-display-status__dot')].every(e => e.getAttribute('aria-hidden') === 'true') : null,
                lastSync: time ? time.getAttribute('datetime') : null,
                lastSyncShown: !!item && !item.hidden && item.getBoundingClientRect().width > 0,
                dir: document.documentElement.getAttribute('dir'),
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

        var live = Resource(culture, "Orders.LiveDisplay.LiveStatus");
        var expected = Resource(culture, "Orders.LiveDisplay.SyncStatus.IssueWithPlatforms")
            .Replace("{0}", Resource(culture, "Orders.PlatformTrendyolYemek"), StringComparison.Ordinal);
        var text = header.GetProperty("text").GetString();

        // The defect: an open page and a working poll were shown as "Live" although Trendyol GO is paused.
        Assert.NotEqual(live, text);
        Assert.Equal(expected, text);
        Assert.Equal("issue", header.GetProperty("state").GetString());
        Assert.Equal("status", header.GetProperty("role").GetString());
        Assert.True(header.GetProperty("iconHidden").GetBoolean(), "the icon is decorative; the words carry the state");

        // The oldest last successful sync of the active connections (Trendyol GO, three hours ago), from the database.
        Assert.True(header.GetProperty("lastSyncShown").GetBoolean(), "the last successful sync is shown");
        var lastSync = DateTimeOffset.Parse(header.GetProperty("lastSync").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.UtcNow - lastSync, TimeSpan.FromHours(3) - TimeSpan.FromMinutes(5), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5));

        Assert.Equal(direction, header.GetProperty("dir").GetString());
        Assert.False(header.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: the header overflows horizontally");
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
    }

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
