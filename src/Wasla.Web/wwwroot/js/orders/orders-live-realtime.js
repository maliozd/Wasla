// Live Screen invalidation (WAS-24). The page's single live connection (wasla-realtime.js) says when an order was
// stored or changed; this module then asks the Live Screen store for its ordinary snapshot read
// (O.liveStore.requestRefresh, GET /orders/live-data) unless the snapshot on screen already shows the order at that
// version (Order.ChangeVersion only grows). A notification is never rendered: ordering, role filters,
// training isolation, new-order detection, sound and browser notifications all stay with the snapshot coordinator.
// - Connected: notifications trigger a read, bursts coalesced; polling continues at a lower rate as a safety net.
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

    const live = api.createLiveInvalidation({
      requestRefresh: function () { return store.requestRefresh(); },
      setPollingIntervalMs: function (ms) { store.setPollingIntervalMs(ms); },
      shownVersion: function (orderId) { return store.shownChangeVersion(orderId); },
      disconnectedPollingMs: opts.pollingIntervalMs || api.DISCONNECTED_POLLING_MS,
      connectedPollingMs: opts.connectedPollingIntervalMs || api.CONNECTED_POLLING_MS,
      setTimeout: root.setTimeout.bind(root),
      clearTimeout: root.clearTimeout.bind(root),
      now: function () { return Date.now(); }
    });

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
  /** The lower safety rate while connected: notifications carry the changes, polling catches anything missed. */
  const CONNECTED_POLLING_MS = 30000;
  /** Notifications within this window are read together. */
  const SETTLE_MS = 150;
  /** At most one notification-triggered read per second; the coordinator still runs one request at a time. */
  const MIN_GAP_MS = 1000;

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

    function read() {
      lastReadAt = options.now();
      return options.requestRefresh();
    }

    function clearScheduled() {
      if (timer) options.clearTimeout(timer);
      timer = null;
    }

    return {
      onNotification: function (message) {
        if (disposed || !message || message.type !== "OrderChanged") return;
        // Already shown at this version or a later one (a duplicate, or a change an earlier read already included).
        const version = Number(message.version);
        if (typeof options.shownVersion === "function" && isFinite(version) && options.shownVersion(String(message.orderId || "")) >= version) return;
        // Already scheduled: this notification is read with it (duplicates and bursts collapse here).
        if (timer) return;
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
          options.setPollingIntervalMs(options.connectedPollingMs || CONNECTED_POLLING_MS);
          // Anything sent before or during the connection was missed: read the full snapshot now.
          clearScheduled();
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
      hasPendingRead: function () { return timer !== null; }
    };
  }

  return {
    DISCONNECTED_POLLING_MS: DISCONNECTED_POLLING_MS,
    CONNECTED_POLLING_MS: CONNECTED_POLLING_MS,
    SETTLE_MS: SETTLE_MS,
    MIN_GAP_MS: MIN_GAP_MS,
    createLiveInvalidation: createLiveInvalidation
  };
});
