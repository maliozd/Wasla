using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Web;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-24 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers, views,
/// scripts, the vendored SignalR client and the hub over a WebSocket). While connected the Live Screen polls only every
/// 30 seconds, so an order that shows within a few seconds came through an invalidation and the ordinary snapshot read.
/// Covered: a new order and a lifecycle action shown without waiting for polling, one browser notification per new
/// order however many invalidations arrive, a burst read together, the 10-second fallback while disconnected, the
/// immediate read on reconnect and after a back/forward-cache restore with one connection throughout, training
/// isolation (no sound or card for a real order, only its count), and a Kitchen screen. Skips only when no Chromium
/// browser is installed or Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class LiveScreenRealtimeBrowserTests
{
    private static readonly TimeSpan Push = TimeSpan.FromSeconds(5);
    private static Guid Tenant => TenantVisualWebHost.TenantId;

    // Counts snapshot reads and SignalR connections built, before any page script runs.
    private const string Instrumentation = """
        window.__liveDataReads = 0;
        window.__connectionsBuilt = 0;
        (function () {
          const fetch = window.fetch;
          window.fetch = function (input) {
            if (String(input && input.url || input).indexOf('/orders/live-data') >= 0) window.__liveDataReads++;
            return fetch.apply(this, arguments);
          };
          let client;
          Object.defineProperty(window, 'signalR', {
            configurable: true,
            get: function () { return client; },
            set: function (value) {
              client = value;
              const build = value.HubConnectionBuilder.prototype.build;
              value.HubConnectionBuilder.prototype.build = function () { window.__connectionsBuilt++; return build.apply(this, arguments); };
            }
          });
        })();
        """;

    // Counts the Live Screen's new-order notifications (sound and browser notification run together in onNotify).
    private const string CountNotifications = """
        (() => {
          const audio = window.WaslaOrders.audio;
          window.__notifications = 0;
          const show = audio.showBrowserNotificationIfAllowed;
          audio.showBrowserNotificationIfAllowed = function () { window.__notifications++; return show && show.apply(this, arguments); };
          return true;
        })()
        """;

    [Theory]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task NewOrdersAndLifecycleChanges_ShowWithoutPolling_AndANewOrderIsAnnouncedOnce(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, culture, signInAs: null, ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));
        Assert.Equal(30000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));

        // A new order, stored and then announced as ingestion does after its commit.
        var orderId = await InsertNewOrderAsync(host, "RT-NEW-1", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync(CardShown(orderId), Push, ct), $"The new order did not appear without polling. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("window.__notifications === 1", TimeSpan.FromSeconds(3), ct), "The new order was not announced once.");

        // A burst of invalidations for the same order: read together, never announced again.
        var readsBefore = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
        for (var i = 0; i < 10; i++)
            await AnnounceAsync(host, orderId, ct);
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        var burstReads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct) - readsBefore;
        Assert.InRange(burstReads, 1, 3);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__notifications", ct));

        // A lifecycle action through the real action service (it announces after its save).
        using (var scope = host.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IOrderActionService>().TryApproveAsync(Tenant, TenantVisualWebHost.DetailOrderId, ct)).Succeeded);
        Assert.True(await browser.WaitForAsync(CardStatus(TenantVisualWebHost.DetailOrderId, "Accepted"), Push, ct),
            $"The approved order did not change on screen. {host.ServerErrors}");
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__notifications", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
        Assert.Null(host.ServerErrors);
    }

    [Fact]
    public async Task WhileDisconnected_ItPollsEveryTenSeconds_AndAReconnectReadsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct);

        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().stop(), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaOrders.liveStore.pollingIntervalMs() === 10000", Push, ct));
        var whileDown = await InsertNewOrderAsync(host, "RT-DOWN-1", ct);
        await AnnounceAsync(host, whileDown, ct); // not delivered: no connection
        Assert.True(await browser.WaitForAsync(CardShown(whileDown), TimeSpan.FromSeconds(15), ct),
            "While disconnected, the order did not appear within the 10-second fallback.");

        // Changed while still down; only the read on reconnect can show it this fast.
        var beforeReconnect = await InsertNewOrderAsync(host, "RT-DOWN-2", ct);
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().start(), true)", ct);
        Assert.True(await browser.WaitForAsync(CardShown(beforeReconnect), Push, ct), "A reconnect did not read the snapshot at once.");
        Assert.Equal(30000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
    }

    [Fact]
    public async Task ARestoreFromTheBackForwardCache_ReconnectsTheSameConnection_ReadsAtOnce_AndKeepsListening()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct);

        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'disconnected'", Push, ct));
        var whileAway = await InsertNewOrderAsync(host, "RT-BFCACHE-1", ct);
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync(CardShown(whileAway), Push, ct), "The restored page did not read the snapshot.");

        // Still subscribed after the restore: a new invalidation is read without polling.
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'connected'", Push, ct));
        var afterRestore = await InsertNewOrderAsync(host, "RT-BFCACHE-2", ct);
        await AnnounceAsync(host, afterRestore, ct);
        Assert.True(await browser.WaitForAsync(CardShown(afterRestore), Push, ct), "After the restore, invalidations were no longer handled.");
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
    }

    [Fact]
    public async Task DuringOrderTraining_ARealOrderIsCountedQuickly_ButNotShownOrAnnounced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(tenantInSetup: true);
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct, waitForCards: false);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('[data-training-real-orders]')", TimeSpan.FromSeconds(10), ct),
            $"The order training panel is not shown. {host.ServerErrors}");
        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));

        var orderId = await InsertNewOrderAsync(host, "RT-TRAINING-1", ct);
        await AnnounceAsync(host, orderId, ct);

        Assert.True(await browser.WaitForAsync("(() => { const box = document.querySelector('[data-training-real-orders-box]'); return !!box && !box.hidden; })()", Push, ct),
            "The real order was not counted in the training panel without polling.");
        Assert.False(await browser.EvaluateAsync<bool>(CardShown(orderId), ct));
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__notifications", ct));
    }

    [Fact]
    public async Task AKitchenScreen_FollowsOrders_WithoutLoadingPrintStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: "kitchen", ct);
        Assert.False(await browser.EvaluateAsync<bool>("!!window.WaslaOrders.printStatus", ct));

        var orderId = await InsertNewOrderAsync(host, "RT-KITCHEN-1", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync(CardShown(orderId), Push, ct), $"The Kitchen screen did not show the new order. {host.ServerErrors}");
    }

    private static async Task<HeadlessChromium> OpenLiveScreenAsync(TenantVisualWebHost host, string culture, string? signInAs, CancellationToken ct, bool waitForCards = true)
    {
        var browser = await host.BrowserAsync(culture, 1280, 900, mobile: false, ct, signInAs);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        await browser.AddScriptBeforePageScriptsAsync(Instrumentation, ct);
        try
        {
            await browser.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
        if (waitForCards)
        {
            Assert.True(await browser.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-id]').length >= 3", TimeSpan.FromSeconds(15), ct),
                $"The Live Screen did not render the seeded orders. {host.ServerErrors}");
        }

        Assert.True(await browser.WaitForAsync("!!window.WaslaRealtime && !!window.WaslaRealtime.get() && window.WaslaRealtime.get().state() === 'connected'", TimeSpan.FromSeconds(15), ct),
            $"The Live Screen did not connect to the live hub. {host.ServerErrors}");
        return browser;
    }

    private static string CardShown(Guid orderId) => $"!!document.querySelector('#ordersLiveScreenHost [data-order-id=\"{orderId}\"]')";

    private static string CardStatus(Guid orderId, string status) =>
        $"(() => {{ const c = document.querySelector('#ordersLiveScreenHost [data-order-id=\"{orderId}\"]'); return !!c && c.getAttribute('data-order-status') === '{status}'; }})()";

    private static async Task<Guid> InsertNewOrderAsync(TenantVisualWebHost host, string externalId, CancellationToken ct)
    {
        var order = PrintServices.Order(Guid.NewGuid(), Tenant, externalId);
        order.InternalStatus = OrderStatus.New;
        order.PlatformStatus = "Created";
        order.ReceivedAt = DateTime.UtcNow;
        order.CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-1);
        await using var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.Id;
    }

    private static Task AnnounceAsync(TenantVisualWebHost host, Guid orderId, CancellationToken ct) =>
        host.Services.GetRequiredService<ITenantChangeNotifier>()
            .NotifyAsync(Tenant, OrderChanged.For(orderId, DateTime.UtcNow), ct);
}
