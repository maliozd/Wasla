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
/// isolation (no sound or card for a real order, only its count), and a Kitchen screen. The host runs with Web's
/// internal listener on (<c>Realtime:Internal</c>), as a deployment with Worker changes pushed does; without it a
/// connected screen keeps the 10-second rate (WAS-103). Skips only when no Chromium browser is installed or Bootstrap
/// cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class LiveScreenRealtimeBrowserTests
{
    private static readonly TimeSpan Push = TimeSpan.FromSeconds(5);
    private static Guid Tenant => TenantVisualWebHost.TenantId;

    // Counts snapshot reads, the snapshots shown and SignalR connections built, before any page script runs.
    private const string Instrumentation = """
        window.__liveDataReads = 0;
        window.__liveRendered = 0;
        window.__connectionsBuilt = 0;
        document.addEventListener('wasla:live-rendered', function () { window.__liveRendered++; });
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

    // Web's internal listener on, so the page lowers its connected rate to 30 seconds. Nothing posts to it here; the
    // tests announce through the host's own notifier.
    private static Task<TenantVisualWebHost> StartHostAsync(bool tenantInSetup = false) =>
        TenantVisualWebHost.StartAsync(tenantInSetup, new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = InternalTenantChangeChannelTests.FreeLoopbackPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = "FAKE-TEST-SECRET-browser-tests-0123456789abcd"
        });

    [Theory]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    public async Task NewOrdersAndLifecycleChanges_ShowWithoutPolling_AndANewOrderIsAnnouncedOnce(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync();
        await using var browser = await OpenLiveScreenAsync(host, culture, signInAs: null, ct);
        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));
        Assert.Equal(30000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));

        // A new order, stored and then announced as ingestion does after its commit.
        var orderId = await InsertNewOrderAsync(host, "RT-NEW-1", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync(CardShown(orderId), Push, ct), $"The new order did not appear without polling. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("window.__notifications === 1", TimeSpan.FromSeconds(3), ct), "The new order was not announced once.");

        // Duplicates of a version the snapshot already shows: no read at all, and never announced again. Reads already
        // under way (a coalesced follow-up) settle first, so only reads caused by the duplicates are counted.
        var readsBefore = await WaitForNoReadsAsync(browser, ct);
        for (var i = 0; i < 10; i++)
            await AnnounceAsync(host, orderId, ct);
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__liveDataReads", ct) - readsBefore);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__notifications", ct));

        // A burst of newer versions (several orders arriving together): read together. The Live Screen announces the new
        // orders of one snapshot with one sound and notification, so a burst costs one announcement per read, not per
        // event.
        readsBefore = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
        var burst = new List<Guid>();
        for (var i = 0; i < 5; i++)
            burst.Add(await InsertNewOrderAsync(host, $"RT-BURST-{i}", ct));
        foreach (var id in burst)
            await AnnounceAsync(host, id, ct);
        Assert.True(await browser.WaitForAsync(string.Join(" && ", burst.Select(CardShown)), Push, ct), "The burst of new orders did not appear.");
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        var burstReads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct) - readsBefore;
        Assert.InRange(burstReads, 1, 3);
        var notifications = await browser.EvaluateAsync<int>("window.__notifications", ct);
        Assert.InRange(notifications, 2, 1 + burstReads);

        // A lifecycle action through the real action service (it announces after its save).
        using (var scope = host.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IOrderActionService>().TryApproveAsync(Tenant, TenantVisualWebHost.DetailOrderId, ct)).Succeeded);
        Assert.True(await browser.WaitForAsync(CardStatus(TenantVisualWebHost.DetailOrderId, "Accepted"), Push, ct),
            $"The approved order did not change on screen. {host.ServerErrors}");
        Assert.Equal(notifications, await browser.EvaluateAsync<int>("window.__notifications", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
        Assert.Null(host.ServerErrors);
    }

    [Fact]
    public async Task WithoutWebsInternalListener_AConnectedScreenKeepsPollingEveryTenSeconds()
    {
        // WAS-103: no Worker or Api change can be pushed, so the page keeps the normal rate while connected, through a
        // reconnect as well.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct);

        Assert.True(await browser.EvaluateAsync<bool>("window.WaslaOrders.liveInvalidation.isConnected()", ct));
        Assert.Equal(10000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().stop(), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'disconnected'", Push, ct));
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().start(), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaOrders.liveInvalidation.isConnected()", Push, ct));
        Assert.Equal(10000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
    }

    [Fact]
    public async Task WhileDisconnected_ItPollsEveryTenSeconds_AndAReconnectReadsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct);

        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));
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
        // Each new order announced exactly once across the fallback and the reconnect.
        Assert.Equal(2, await browser.EvaluateAsync<int>("window.__notifications", ct));
        Assert.Equal(30000, await browser.EvaluateAsync<int>("window.WaslaOrders.liveStore.pollingIntervalMs()", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
    }

    [Fact]
    public async Task ARestoreFromTheBackForwardCache_ReconnectsTheSameConnection_ReadsAtOnce_AndKeepsListening()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct);

        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));
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
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Assert.Equal(2, await browser.EvaluateAsync<int>("window.__notifications", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__connectionsBuilt", ct));
    }

    [Fact]
    public async Task DuringOrderTraining_ARealOrderIsCountedAndAnnouncedOnce_WithoutShowingItsCardOrPausingTraining()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(tenantInSetup: true);
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct, waitForCards: false);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('[data-training-real-orders]')", TimeSpan.FromSeconds(10), ct),
            $"The order training panel is not shown. {host.ServerErrors}");
        Assert.True(await browser.EvaluateAsync<bool>(CountNotifications, ct));

        // WAS-117: the seeded real backlog stays off the practice board but is counted in the header, under its own label.
        Assert.True(await browser.WaitForAsync($"{WaitingCount} >= 3", TimeSpan.FromSeconds(10), ct),
            $"The header does not count the real orders waiting during training. {host.ServerErrors}");
        var waiting = await browser.EvaluateAsync<int>(WaitingCount, ct);
        Assert.Equal("Waiting Real Orders", await browser.EvaluateAsync<string>("document.getElementById('ordersLiveDisplayActiveLabel').textContent.trim()", ct));
        // Only real orders link to their details page: none is on the practice board.
        Assert.Equal(0, await browser.EvaluateAsync<int>("document.querySelectorAll('#ordersLiveScreenHost a[href^=\"/orders/details/\"]').length", ct));

        var orderId = await InsertNewOrderAsync(host, "RT-TRAINING-1", ct);
        await AnnounceAsync(host, orderId, ct);

        Assert.True(await browser.WaitForAsync("window.__notifications === 1", Push, ct),
            "A real order that arrived during training was not announced without polling.");
        Assert.True(await browser.WaitForAsync($"{WaitingCount} === {waiting + 1}", Push, ct), "The waiting count did not include the new real order.");
        Assert.True(await browser.WaitForAsync("(() => { const box = document.querySelector('[data-training-real-orders-box]'); return !!box && !box.hidden; })()", Push, ct),
            "The training panel does not report the waiting real orders.");
        Assert.False(await browser.EvaluateAsync<bool>(CardShown(orderId), ct));
        var trainingBefore = await browser.EvaluateAsync<string>(TrainingState, ct);

        // Duplicates of the event, a dropped connection, and a back/forward-cache restore: no second announcement, no
        // pause or step change, and the real order is still only counted.
        for (var i = 0; i < 5; i++)
            await AnnounceAsync(host, orderId, ct);
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().stop(), true)", ct);
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().start(), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'connected'", Push, ct));
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })), true)", ct);
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'connected'", Push, ct));
        var second = await InsertNewOrderAsync(host, "RT-TRAINING-2", ct);
        await AnnounceAsync(host, second, ct);
        Assert.True(await browser.WaitForAsync("window.__notifications === 2", Push, ct), "The second real order was not announced.");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        Assert.Equal(trainingBefore, await browser.EvaluateAsync<string>(TrainingState, ct));
        Assert.Equal(2, await browser.EvaluateAsync<int>("window.__notifications", ct));
        Assert.Equal(waiting + 2, await browser.EvaluateAsync<int>(WaitingCount, ct));
        Assert.False(await browser.EvaluateAsync<bool>(CardShown(second), ct));
        // The pause flow removed in 7bd1bca does not come back in any form.
        Assert.True(await browser.EvaluateAsync<bool>(
            "!document.querySelector('[data-training-pause], [data-training-resume], [data-training-show-active], .is-paused')", ct));
    }

    // The header's active count as a number (en-US digits).
    private const string WaitingCount = "Number(document.getElementById('ordersLiveDisplayActiveCount').textContent.replace(/[^0-9]/g, ''))";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndingTrainingOnTheLiveScreen_AnnouncesARealOrderThatArrivedJustBefore_WithoutReplayingTheBacklog(bool slowSettings)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(tenantInSetup: true);
        // The store's own first read waits for the notification settings. When they are slow it comes after the read the
        // live connection makes on connecting, so the first snapshot shown is not yet the page's last read (as on CI).
        var settings = slowSettings ? host.HoldNextRequest("GET", "/notification-settings/current") : null;
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct, waitForCards: false, AnnouncementLog);
        Assert.True(await browser.WaitForAsync($"{WaitingCount} >= 3", TimeSpan.FromSeconds(10), ct),
            $"The training Live Screen did not render its first snapshot. {host.ServerErrors}");
        if (settings is not null)
        {
            await settings.Reached.WaitAsync(TimeSpan.FromSeconds(10), ct);
            settings.Release();
        }

        Assert.True(await browser.WaitForAsync(OpeningReadsShown, TimeSpan.FromSeconds(10), ct),
            $"The training Live Screen's opening reads did not finish. {host.ServerErrors}");
        var reads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);

        // A real order is stored (and not announced live) after this page's last read: only the next page can see it.
        var orderId = await InsertNewOrderAsync(host, "RT-TRAINING-END", ct);
        Assert.Equal(reads, await browser.EvaluateAsync<int>("window.__liveDataReads", ct));

        // The Owner ends training from the Live Screen (the End dialog's confirmed form); the server opens the normal
        // Live Screen, whose first snapshot holds the whole backlog and the new order.
        await browser.EvaluateAsync<bool>("(window.__leftTraining = true)", ct);
        await browser.EvaluateAsync<bool>($"({EndForm}.requestSubmit(), true)", ct);
        await browser.WaitForNavigationAsync("__leftTraining", ct);
        // Nothing was read on the training page between storing the order and handing its known orders over.
        Assert.Equal(reads, await browser.EvaluateAsync<int>("Number(sessionStorage.getItem('__readsAtSubmit'))", ct));
        Assert.True(await browser.WaitForAsync(CardShown(orderId), TimeSpan.FromSeconds(10), ct),
            $"The normal Live Screen did not show the order after training ended. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("JSON.parse(sessionStorage.getItem('__announced') || '[]').length >= 1", Push, ct),
            "The real order that arrived just before training ended was never announced.");
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // Exactly that order, once: the backlog the trainee already knew is not announced as new.
        Assert.Equal($"[\"{orderId}\"]", await browser.EvaluateAsync<string>("sessionStorage.getItem('__announced')", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("Number(sessionStorage.getItem('__notified') || 0)", ct));
        Assert.Equal("Active Orders", await browser.EvaluateAsync<string>("document.getElementById('ordersLiveDisplayActiveLabel').textContent.trim()", ct));
    }

    [Fact]
    public async Task EndingTraining_ARealOrderThatArrivesWhileThePageIsBeingReplaced_IsAnnouncedOnceByTheNormalLiveScreen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(tenantInSetup: true);
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct, waitForCards: false, AnnouncementLog);
        Assert.True(await browser.WaitForAsync(OpeningReadsShown, TimeSpan.FromSeconds(10), ct),
            $"The training Live Screen's opening reads did not finish. {host.ServerErrors}");

        // The Owner confirms End. The training page hands its known orders over on submit and runs on until the normal
        // Live Screen replaces it, while the server ends training and a real order arrives. DevTools does not reach a page
        // whose own navigation is under way, so the browser's navigation is held back once every page listener has seen
        // the submit, training is ended with the End form's own POST, and the page then leaves through the form.
        await browser.EvaluateAsync<bool>(HoldBackNextNavigation, ct);
        await browser.EvaluateAsync<bool>("(window.__leftTraining = true)", ct);
        await browser.EvaluateAsync<bool>($"({EndForm}.requestSubmit(), true)", ct);
        Assert.Equal("opaqueredirect", await browser.EvaluateAsync<string>(
            $"fetch({EndForm}.action, {{ method: 'POST', body: new FormData({EndForm}), redirect: 'manual' }}).then(r => r.type)", ct));

        // The new order is announced as ingestion does after its commit. The training page still receives it and asks its
        // store for a read, which would now get the normal snapshot with the order in it.
        var reads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
        var asked = await browser.EvaluateAsync<int>("window.WaslaOrders.liveInvalidation.stats().reads", ct);
        var orderId = await InsertNewOrderAsync(host, "RT-TRAINING-GAP", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync(
                $"window.WaslaOrders.liveInvalidation.stats().reads > {asked} && window.__liveRendered === window.__liveDataReads", Push, ct),
            $"The training page did not handle the new order's notification. {host.ServerErrors}");
        var readsWhileLeaving = await browser.EvaluateAsync<int>("window.__liveDataReads", ct) - reads;

        await browser.EvaluateAsync<bool>($"({EndForm}.submit(), true)", ct);
        await browser.WaitForNavigationAsync("__leftTraining", ct);
        Assert.True(await browser.WaitForAsync(OpeningReadsShown, TimeSpan.FromSeconds(10), ct),
            $"The normal Live Screen's opening reads did not finish. {host.ServerErrors}");
        Assert.True(await browser.EvaluateAsync<bool>(CardShown(orderId), ct), $"The normal Live Screen does not show the order. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("Number(sessionStorage.getItem('__notified') || 0) >= 1", Push, ct),
            "The real order that arrived while training ended was never announced.");

        // Once, by the normal Live Screen, and the backlog is not announced as new: the page being replaced read and
        // announced nothing after it handed its known orders over.
        Assert.Equal($"[\"{orderId}\"]", await browser.EvaluateAsync<string>("sessionStorage.getItem('__announced')", ct));
        Assert.Equal(1, await browser.EvaluateAsync<int>("Number(sessionStorage.getItem('__notified') || 0)", ct));
        Assert.Equal(0, readsWhileLeaving);
        Assert.Equal("Active Orders", await browser.EvaluateAsync<string>("document.getElementById('ordersLiveDisplayActiveLabel').textContent.trim()", ct));
    }

    [Fact]
    public async Task ATrainingPageThatHandedOver_AndComesBackFromTheBackForwardCache_FollowsRealOrdersAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(tenantInSetup: true);
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: null, ct, waitForCards: false, AnnouncementLog);
        Assert.True(await browser.WaitForAsync(OpeningReadsShown, TimeSpan.FromSeconds(10), ct),
            $"The training Live Screen's opening reads did not finish. {host.ServerErrors}");
        var waiting = await browser.EvaluateAsync<int>(WaitingCount, ct);

        // End is submitted and the page hands its known orders over (its navigation held back as above). The Owner then
        // returns to it: the page is hidden and shown again from the back/forward cache, as in the tests above.
        await browser.EvaluateAsync<bool>(HoldBackNextNavigation, ct);
        await browser.EvaluateAsync<bool>($"({EndForm}.requestSubmit(), true)", ct);
        Assert.True(await browser.EvaluateAsync<bool>("!!sessionStorage.getItem('Wasla.liveScreen.knownOrderHandoff')", ct),
            "The training page did not hand its known orders over.");
        var reads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'disconnected'", Push, ct));
        await browser.EvaluateAsync<bool>("(window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })), true)", ct);
        Assert.True(await browser.WaitForAsync($"window.__liveDataReads > {reads} && window.__liveRendered === window.__liveDataReads", Push, ct),
            $"The restored page did not read its snapshot. {host.ServerErrors}");
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'connected'", Push, ct));

        // It is this tab's Live Screen again: a real order that arrives now is counted and announced once.
        var orderId = await InsertNewOrderAsync(host, "RT-TRAINING-RESTORED", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync($"{WaitingCount} === {waiting + 1}", Push, ct), "The restored page did not count the new real order.");
        Assert.True(await browser.WaitForAsync("Number(sessionStorage.getItem('__notified') || 0) === 1", Push, ct),
            "The restored page did not announce the new real order.");
    }

    // The End dialog's confirmed form on the Live Screen.
    private const string EndForm = "document.querySelector('#waslaGuidedSetupEndDialog form[action=\"/guided-setup/end\"]')";

    // Cancels the next form submission once every page listener has seen it (on window, last), so the page stays: the
    // Live Screen store, listening on the document, still takes it for the page leaving.
    private const string HoldBackNextNavigation = "(window.addEventListener('submit', function (event) { event.preventDefault(); }, { once: true }), true)";

    // Both opening reads of a Live Screen page have been shown and none is under way: the read its live connection makes
    // on connecting and the store's own first read, which waits for the notification settings, in either order. Needs
    // AnnouncementLog.
    private const string OpeningReadsShown = "window.__storeStarted === true && window.__liveRendered === window.__liveDataReads";

    // Records, across page loads in this tab, each order the Live Screen announced, each notification attempt and the
    // reads a page had started when a form was submitted; marks when the store's own first read was asked for.
    private const string AnnouncementLog = """
        document.addEventListener('wasla:real-order-arrived', function (event) {
          const log = JSON.parse(sessionStorage.getItem('__announced') || '[]');
          log.push(event.detail.orderId);
          sessionStorage.setItem('__announced', JSON.stringify(log));
        });
        window.addEventListener('submit', function () {
          sessionStorage.setItem('__readsAtSubmit', String(window.__liveDataReads));
        }, true);
        (function () {
          let orders;
          Object.defineProperty(window, 'WaslaOrders', {
            configurable: true,
            get: function () { return orders; },
            set: function (value) {
              orders = value;
              let audio;
              Object.defineProperty(value, 'audio', {
                configurable: true,
                get: function () { return audio; },
                set: function (next) {
                  audio = next;
                  const show = next.showBrowserNotificationIfAllowed;
                  next.showBrowserNotificationIfAllowed = function () {
                    sessionStorage.setItem('__notified', String(Number(sessionStorage.getItem('__notified') || 0) + 1));
                    return show && show.apply(this, arguments);
                  };
                }
              });
              let store;
              Object.defineProperty(value, 'liveStore', {
                configurable: true,
                get: function () { return store; },
                set: function (next) {
                  store = next;
                  const start = next.start;
                  next.start = function () {
                    window.__storeStarted = true;
                    return start.apply(this, arguments);
                  };
                }
              });
            }
          });
        })();
        """;

    // The training panel's step and visibility, which a pause would change.
    private const string TrainingState =
        "(() => { const s = document.querySelector('[data-guided-training]'); return s ? `${s.getAttribute('data-step')}|${s.hidden}|${document.querySelector('[data-training-panel]')?.hidden}` : 'none'; })()";

    [Fact]
    public async Task AKitchenScreen_FollowsOrders_WithoutLoadingPrintStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync();
        await using var browser = await OpenLiveScreenAsync(host, "en-US", signInAs: "kitchen", ct);
        Assert.False(await browser.EvaluateAsync<bool>("!!window.WaslaOrders.printStatus", ct));

        var orderId = await InsertNewOrderAsync(host, "RT-KITCHEN-1", ct);
        await AnnounceAsync(host, orderId, ct);
        Assert.True(await browser.WaitForAsync(CardShown(orderId), Push, ct), $"The Kitchen screen did not show the new order. {host.ServerErrors}");
    }

    private static async Task<HeadlessChromium> OpenLiveScreenAsync(
        TenantVisualWebHost host, string culture, string? signInAs, CancellationToken ct, bool waitForCards = true, string? moreInstrumentation = null)
    {
        var browser = await host.BrowserAsync(culture, 1280, 900, mobile: false, ct, signInAs);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");

        // A stalled page (a slow CDN, a hub that does not connect) fails or skips here, before the test's await using.
        return await browser.SetUpAsync(async started =>
        {
            await started.AddScriptBeforePageScriptsAsync(Instrumentation, ct);
            if (moreInstrumentation is not null)
                await started.AddScriptBeforePageScriptsAsync(moreInstrumentation, ct);
            try
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
            }
            catch (TimeoutException)
            {
                await started.NavigateAsync(new Uri(host.BaseAddress, "/orders/live-display"), ct);
            }

            if (!await started.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
                Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
            if (waitForCards)
            {
                Assert.True(await started.WaitForAsync("document.querySelectorAll('#ordersLiveScreenHost [data-order-id]').length >= 3", TimeSpan.FromSeconds(15), ct),
                    $"The Live Screen did not render the seeded orders. {host.ServerErrors}");
            }

            Assert.True(await started.WaitForAsync("!!window.WaslaRealtime && !!window.WaslaRealtime.get() && window.WaslaRealtime.get().state() === 'connected'", TimeSpan.FromSeconds(15), ct),
                $"The Live Screen did not connect to the live hub. {host.ServerErrors}");
        });
    }

    /// <summary>
    /// Waits until no snapshot read has started for 1.5 seconds (well under the 30-second connected poll) and returns the
    /// read count; a slow machine may still finish a coalesced follow-up read after the card it waited for appeared.
    /// </summary>
    private static async Task<int> WaitForNoReadsAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var reads = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            var now = await browser.EvaluateAsync<int>("window.__liveDataReads", ct);
            if (now == reads)
                return now;
            reads = now;
        }

        throw new TimeoutException("Live Screen snapshot reads did not settle.");
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
        order.ChangeVersion = 1; // as ingestion stores a new order
        await using var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.Id;
    }

    /// <summary>Announces the order at its stored version, as the writer that committed it does.</summary>
    private static async Task AnnounceAsync(TenantVisualWebHost host, Guid orderId, CancellationToken ct)
    {
        long version;
        await using (var db = await host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct))
            version = db.Orders.Where(o => o.Id == orderId).Select(o => o.ChangeVersion).Single();
        await host.Services.GetRequiredService<ITenantChangeNotifier>().NotifyAsync(Tenant, new OrderChanged(orderId, version), ct);
    }
}
