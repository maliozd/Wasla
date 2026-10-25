// Live Screen invalidation (WAS-24). The page's single live connection (wasla-realtime.js) says when an order was
// stored or changed; this module then asks the Live Screen store for its ordinary snapshot read
// (O.liveStore.requestRefresh, GET /orders/live-data) unless the snapshot on screen already shows the order at that
// version (Order.ChangeVersion only grows). A notification is never rendered: ordering, role filters,
// training isolation, new-order detection, sound and browser notifications all stay with the snapshot coordinator.
// - Connected: notifications trigger a read, bursts coalesced; polling continues as a safety net, at a lower rate only
//   when the page says Wasla.Web receives Worker and Api changes (connectedPollingIntervalMs, WAS-103).
// - Connecting or disconnected: the normal polling rate; every (re)connect, including after the back/forward cache,
//   reads the snapshot at once because notifications sent meanwhile were missed.
(function (root, factory) {
  "use strict";

  const api = factory();
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }

  const O = root && root.WaslaOrders;
  if (!O || typeof root.document === "undefined") return;

  function start() {
    const opts = O.opts || {};
    const store = O.liveStore;
    if (opts.pageMode !== "liveDisplay" || !opts.realtimeHubUrl || !root.WaslaRealtime || !store) return;
    const realtime = root.WaslaRealtime.get(opts.realtimeHubUrl);
    if (!realtime) return;

    const rates = api.pollingRates(opts);
    const live = api.createLiveInvalidation({
      requestRefresh: function () { return store.requestRefresh(); },
      setPollingIntervalMs: function (ms) { store.setPollingIntervalMs(ms); },
      shownVersion: function (orderId) { return store.shownChangeVersion(orderId); },
      disconnectedPollingMs: rates.disconnected,
      connectedPollingMs: rates.connected,
      setTimeout: root.setTimeout.bind(root),
      clearTimeout: root.clearTimeout.bind(root),
      now: function () { return Date.now(); }
    });

    // Read-only for tests and support (counters, pending read); the page itself only uses the subscriptions below.
    O.liveInvalidation = { stats: live.stats, isConnected: live.isConnected };
    realtime.on("OrderChanged", live.onNotification);
    realtime.onState(live.onConnectionState);
    live.onConnectionState({ state: realtime.state(), reconnected: false });
    // A page kept in the back/forward cache keeps this module; the live client reconnects on return and that reconnect
    // reads the snapshot. Only a page that is really going away is disposed.
    root.addEventListener("pagehide", function (ev) { if (!ev || !ev.persisted) live.dispose(); });
    realtime.start();
  }

  root.document.addEventListener("DOMContentLoaded", start);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  /** The Live Screen's existing polling rate, kept while not connected. */
  const DISCONNECTED_POLLING_MS = 10000;
  /** Notifications within this window are read together. */
  const SETTLE_MS = 150;
  /** At most one notification-triggered read per second; the coordinator still runs one request at a time. */
  const MIN_GAP_MS = 1000;

  function usableInterval(value) {
    return typeof value === "number" && isFinite(value) && value > 0 ? value : null;
  }

  /**
   * The page's polling rates. Wasla.Web renders connectedPollingIntervalMs from its internal listener (WAS-103): the
   * lower safety rate when Worker and Api changes are pushed, the normal rate when they cannot be, however healthy this
   * page's own connection is. Without a usable value a connected page keeps the normal rate; the browser never lowers
   * it on its own.
   */
  function pollingRates(opts) {
    const disconnected = usableInterval(opts && opts.pollingIntervalMs) || DISCONNECTED_POLLING_MS;
    return { disconnected: disconnected, connected: usableInterval(opts && opts.connectedPollingIntervalMs) || disconnected };
  }

  /**
   * options: requestRefresh(), setPollingIntervalMs(ms), shownVersion(orderId) (-1 when not shown),
   * disconnectedPollingMs, connectedPollingMs, setTimeout, clearTimeout, now().
   */
  function createLiveInvalidation(options) {
    const settleMs = options.settleMs != null ? options.settleMs : SETTLE_MS;
    const minGapMs = options.minGapMs != null ? options.minGapMs : MIN_GAP_MS;
    let timer = null;
    let lastReadAt = -Infinity;
    let connected = false;
    let disposed = false;
    // notifications: OrderChanged received; skipped: already shown at that version; coalesced: folded into a read
    // already scheduled; reads: snapshot reads asked for (notification-triggered and on every (re)connect).
    const counters = { notifications: 0, skipped: 0, coalesced: 0, reads: 0, connectReads: 0, ignoredAfterDispose: 0 };

    function read() {
      lastReadAt = options.now();
      counters.reads += 1;
      return options.requestRefresh();
    }

    function clearScheduled() {
      if (timer) options.clearTimeout(timer);
      timer = null;
    }

    return {
      onNotification: function (message) {
        if (disposed) {
          counters.ignoredAfterDispose += 1;
          return;
        }
        if (!message || message.type !== "OrderChanged") return;
        counters.notifications += 1;
        // Already shown at this version or a later one (a duplicate, or a change an earlier read already included).
        const version = Number(message.version);
        if (typeof options.shownVersion === "function" && isFinite(version) && options.shownVersion(String(message.orderId || "")) >= version) {
          counters.skipped += 1;
          return;
        }
        // Already scheduled: this notification is read with it (duplicates and bursts collapse here).
        if (timer) {
          counters.coalesced += 1;
          return;
        }
        const delay = Math.max(settleMs, lastReadAt + minGapMs - options.now());
        timer = options.setTimeout(function () {
          timer = null;
          if (!disposed) read();
        }, delay);
      },
      onConnectionState: function (info) {
        if (disposed || !info) return;
        if (info.state === "connected") {
          connected = true;
          options.setPollingIntervalMs(options.connectedPollingMs || options.disconnectedPollingMs || DISCONNECTED_POLLING_MS);
          // Anything sent before or during the connection was missed: read the full snapshot now.
          if (timer) counters.coalesced += 1;
          clearScheduled();
          counters.connectReads += 1;
          read();
        } else {
          connected = false;
          clearScheduled();
          options.setPollingIntervalMs(options.disconnectedPollingMs || DISCONNECTED_POLLING_MS);
        }
      },
      dispose: function () {
        disposed = true;
        clearScheduled();
      },
      isConnected: function () { return connected; },
      hasPendingRead: function () { return timer !== null; },
      stats: function () { return Object.assign({ pendingRead: timer !== null, disposed: disposed }, counters); }
    };
  }

  return {
    DISCONNECTED_POLLING_MS: DISCONNECTED_POLLING_MS,
    pollingRates: pollingRates,
    SETTLE_MS: SETTLE_MS,
    MIN_GAP_MS: MIN_GAP_MS,
    createLiveInvalidation: createLiveInvalidation
  };
});
