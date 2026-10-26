using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Orders;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-117 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/>, whose tenant is still in Setup
/// with its Owner in order training and real orders waiting. Training kept the real orders off the practice board but
/// the header said "Active orders 0", so an owner could read it as no work waiting. The header must count the real
/// orders waiting, under its own label, the training status line must report the same number in the page's language,
/// and both must stay readable right to left and on a phone. Skips only when no Chromium browser is installed or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenTrainingBacklogBrowserTests : IAsyncLifetime
{
    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync(tenantInSetup: true);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("tr-TR", "ltr", 1280)]
    [InlineData("tr-TR", "ltr", 390)]
    [InlineData("en-US", "ltr", 1280)]
    [InlineData("en-US", "ltr", 390)]
    [InlineData("ar-SA", "rtl", 1280)]
    [InlineData("ar-SA", "rtl", 390)]
    [InlineData("ru-RU", "ltr", 1280)]
    [InlineData("ru-RU", "ltr", 390)]
    public async Task DuringTraining_TheHeaderAndTheTrainingLineCountTheWaitingRealOrders_NotThePracticeBoard(
        string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        await LoadAsync(browser, "/orders/live-display", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('[data-training-real-orders]')", TimeSpan.FromSeconds(10), ct),
            $"The order training panel is not shown. {_host.ServerErrors}");

        var waiting = await WaitingRealOrdersAsync(ct);
        Assert.True(waiting > 0, "the host seeds active real orders");
        var expectedCount = await browser.EvaluateAsync<string>($"new Intl.NumberFormat(document.documentElement.lang).format({waiting})", ct);
        Assert.True(await browser.WaitForAsync(
                $"document.getElementById('ordersLiveDisplayActiveCount').textContent === {JsonSerializer.Serialize(expectedCount)}",
                TimeSpan.FromSeconds(15), ct),
            $"{culture}: the header does not count the {waiting} real orders waiting during training. {_host.ServerErrors}");
        Assert.True(await browser.WaitForAsync(
                "(() => { const box = document.querySelector('[data-training-real-orders-box]'); return !!box && !box.hidden; })()",
                TimeSpan.FromSeconds(10), ct),
            $"{culture}: the training status line does not report the waiting real orders.");

        var page = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const label = document.getElementById('ordersLiveDisplayActiveLabel');
              const metric = label.closest('.wasla-live-summary__metric');
              const title = document.querySelector('[data-training-real-orders-title]');
              const body = document.querySelector('[data-training-real-orders-body]');
              const box = document.querySelector('[data-training-real-orders-box]').getBoundingClientRect();
              const labelBox = label.getBoundingClientRect();
              return {
                label: label.textContent.trim(),
                title: title.textContent.trim(),
                body: body.textContent.trim(),
                realCards: document.querySelectorAll('#ordersLiveScreenHost a[href^="/orders/details/"]').length,
                labelVisible: labelBox.width > 0 && labelBox.left >= 0 && labelBox.right <= document.documentElement.clientWidth + 1,
                labelClipped: metric.scrollWidth > metric.clientWidth + 1,
                boxInside: box.left >= 0 && box.right <= document.documentElement.clientWidth + 1,
                dir: document.documentElement.getAttribute('dir'),
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

        // The header's metric is labelled as the real orders waiting, never as the practice board's active orders.
        Assert.Equal(Resource(culture, "Orders.LiveScreen.WaitingRealOrders"), page.GetProperty("label").GetString());
        Assert.NotEqual(Resource(culture, "Orders.ActiveOrders"), page.GetProperty("label").GetString());
        // The training line reports the same number, written in the same digits, in the page language's singular or plural.
        var title = waiting == 1
            ? Resource(culture, "GuidedSetup.Training.RealOrders.TitleOne")
            : Resource(culture, "GuidedSetup.Training.RealOrders.TitleMany").Replace("{0}", expectedCount, StringComparison.Ordinal);
        Assert.Equal(title, page.GetProperty("title").GetString());
        Assert.Equal(Resource(culture, waiting == 1 ? "GuidedSetup.Training.RealOrders.BodyOne" : "GuidedSetup.Training.RealOrders.BodyMany"), page.GetProperty("body").GetString());
        // Training isolation itself is unchanged: no real order card on the practice board.
        Assert.Equal(0, page.GetProperty("realCards").GetInt32());

        Assert.Equal(direction, page.GetProperty("dir").GetString());
        Assert.True(page.GetProperty("labelVisible").GetBoolean(), $"{culture} {width}px: the waiting label is off screen");
        Assert.False(page.GetProperty("labelClipped").GetBoolean(), $"{culture} {width}px: the waiting label is clipped");
        Assert.True(page.GetProperty("boxInside").GetBoolean(), $"{culture} {width}px: the training status line leaves the screen");
        Assert.False(page.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: the page overflows horizontally");
    }

    /// <summary>The real orders the header must count: the seeded tenant's active orders, from its database.</summary>
    private async Task<int> WaitingRealOrdersAsync(CancellationToken ct)
    {
        await using var db = await _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        return db.Orders.Count(o => LiveScreenVisibility.ActiveStatuses.Contains(o.InternalStatus));
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
