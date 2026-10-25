// Keeps the shared print action (_OrderPrintAction) current while it is on screen: in an open Live Screen detail modal,
// the Focus detail panel, or the full order details page. The server state is read from GET /orders/{id}/print-state:
// - when a live PrintJobChanged notification for a shown order is newer than what is shown;
// - right after the live connection (re)connects, because notifications may have been missed;
// - every ~10 seconds while the live connection is down, and only while such a panel is shown.
// A notification is never applied as the state itself. Versions are order-scoped (PrintJob.StatusVersion), so a
// duplicate, stale or out-of-order notification or response cannot move the shown state backwards.
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
    if (!opts.realtimeHubUrl || !opts.orderPrintStateUrl || !root.WaslaRealtime) return;
    const realtime = root.WaslaRealtime.get(opts.realtimeHubUrl);
    if (!realtime) return;

    const sync = api.createPrintStatusSync({
      view: api.createDomView(root.document, {
        isBusy: function (orderId) {
          return O.print && typeof O.print.isBusy === "function" && O.print.isBusy(orderId);
        },
        submitGeneration: function (orderId) {
          return O.print && typeof O.print.submitGeneration === "function" ? O.print.submitGeneration(orderId) : 0;
        }
      }),
      fetchState: function (orderId, signal) {
        return api.fetchPrintState(root.fetch.bind(root), opts.orderPrintStateUrl, orderId, signal);
      },
      createAbortController: function () { return new AbortController(); },
      setTimeout: root.setTimeout.bind(root),
      clearTimeout: root.clearTimeout.bind(root),
      isHidden: function () { return root.document.visibilityState === "hidden"; }
    });

    realtime.on("PrintJobChanged", sync.onNotification);
    realtime.onState(sync.onConnectionState);
    sync.onConnectionState({ state: realtime.state(), reconnected: false });
    root.document.addEventListener("visibilitychange", sync.onVisible);
    // A page kept in the back/forward cache comes back: the live client reconnects and that reconnect reads every shown
    // order again, so only a page that is really going away is disposed.
    root.addEventListener("pagehide", function (ev) { if (!ev || !ev.persisted) sync.dispose(); });
    O.printStatus = { refresh: sync.refresh };
    realtime.start();
  }

  root.document.addEventListener("DOMContentLoaded", start);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  const FALLBACK_INTERVAL_MS = 10000;
  // Same limit as the Live Screen snapshot request.
  const REQUEST_TIMEOUT_MS = 15000;
  const MODES = ["print", "reprint", "queued", "printing"];
  const OUTCOMES = ["none", "printed", "failed"];

  function isValidState(state) {
    return !!state
      && typeof state.orderId === "string"
      && typeof state.version === "number" && isFinite(state.version) && state.version >= 0
      && MODES.indexOf(state.mode) >= 0
      && OUTCOMES.indexOf(state.outcome) >= 0;
  }

  /** Reads the server state; null for anything that is not a valid answer (session expired, error, timeout). */
  function fetchPrintState(fetchFn, urlTemplate, orderId, signal) {
    const url = urlTemplate.replace("{id}", encodeURIComponent(orderId));
    return fetchFn(url, {
      method: "GET",
      credentials: "same-origin",
      cache: "no-store",
      redirect: "manual",
      headers: { "Accept": "application/json", "X-Requested-With": "XMLHttpRequest" },
      signal: signal
    }).then(function (response) {
      if (!response || !response.ok || response.type === "opaqueredirect") return null;
      return response.json().then(function (body) {
        return isValidState(body) && body.orderId.toLowerCase() === String(orderId).toLowerCase() ? body : null;
      }, function () { return null; });
    }, function () { return null; });
  }

  /**
   * options.view: displayedOrderIds(), shownVersion(orderId) (-1 when not shown), apply(orderId, state), isBusy(orderId),
   * submitGeneration(orderId) (counts finished manual print submits of the order).
   * options.fetchState(orderId, signal) -> Promise<state|null>; createAbortController, setTimeout, clearTimeout, isHidden.
   */
  function createPrintStatusSync(options) {
    const view = options.view;
    const inFlight = {};
    const followUp = {};
    let connected = false;
    let fallbackTimer = null;
    let disposed = false;

    function refresh(orderId) {
      if (disposed || view.shownVersion(orderId) < 0) return Promise.resolve(false);
      if (inFlight[orderId]) {
        // One request per order at a time; whatever arrives meanwhile gets exactly one more read afterwards.
        followUp[orderId] = true;
        return inFlight[orderId];
      }

      const controller = options.createAbortController();
      const generation = submitGeneration(orderId);
      const timer = options.setTimeout(function () { controller.abort(); }, REQUEST_TIMEOUT_MS);
      const request = Promise.resolve()
        .then(function () { return options.fetchState(orderId, controller.signal); })
        .catch(function () { return null; })
        .then(function (state) {
          options.clearTimeout(timer);
          delete inFlight[orderId];
          let applied = false;
          // A manual print of this order is being submitted, or finished after this read began: its own result updates
          // the button and it asks again, so this possibly older answer is not shown.
          if (state && !disposed && !view.isBusy(orderId) && generation === submitGeneration(orderId)
            && state.version >= view.shownVersion(orderId)) {
            view.apply(orderId, state);
            applied = true;
          }
          if (followUp[orderId]) {
            delete followUp[orderId];
            refresh(orderId);
          }
          return applied;
        });
      inFlight[orderId] = request;
      return request;
    }

    function submitGeneration(orderId) {
      return typeof view.submitGeneration === "function" ? view.submitGeneration(orderId) : 0;
    }

    function refreshAllShown() {
      return Promise.all(view.displayedOrderIds().map(refresh));
    }

    function stopFallback() {
      if (fallbackTimer) options.clearTimeout(fallbackTimer);
      fallbackTimer = null;
    }

    function scheduleFallback() {
      stopFallback();
      if (disposed || connected) return;
      fallbackTimer = options.setTimeout(function () {
        fallbackTimer = null;
        // Nothing shown or the tab is hidden: no request; check again later.
        if (!options.isHidden() && view.displayedOrderIds().length > 0) refreshAllShown();
        scheduleFallback();
      }, FALLBACK_INTERVAL_MS);
    }

    return {
      refresh: refresh,
      onNotification: function (message) {
        if (disposed || !message || message.type !== "PrintJobChanged") return;
        const orderId = String(message.orderId || "");
        const version = Number(message.version);
        const shown = view.shownVersion(orderId);
        // Not on screen: nothing to update and nothing to fetch. Not newer: a duplicate or stale notification.
        if (shown < 0 || !isFinite(version) || version <= shown) return;
        refresh(orderId);
      },
      onConnectionState: function (info) {
        if (disposed || !info) return;
        if (info.state === "connected") {
          connected = true;
          stopFallback();
          // Notifications sent while connecting or disconnected were missed: read what is shown now.
          refreshAllShown();
        } else {
          connected = false;
          if (!fallbackTimer) scheduleFallback();
        }
      },
      onVisible: function () {
        if (!disposed && !connected && !options.isHidden()) refreshAllShown();
      },
      dispose: function () {
        disposed = true;
        stopFallback();
      },
      isFallbackActive: function () { return fallbackTimer !== null; }
    };
  }

  /** The page's print actions: [data-print-action-for] roots rendered by _OrderPrintAction. */
  function createDomView(doc, hooks) {
    const page = hooks || {};
    function rootsFor(orderId) {
      const all = doc.querySelectorAll("[data-print-action-for]");
      const matches = [];
      const wanted = String(orderId).toLowerCase();
      for (let i = 0; i < all.length; i++) {
        if (String(all[i].getAttribute("data-print-action-for")).toLowerCase() === wanted) matches.push(all[i]);
      }
      return matches;
    }

    return {
      displayedOrderIds: function () {
        const seen = {};
        const ids = [];
        const all = doc.querySelectorAll("[data-print-action-for]");
        for (let i = 0; i < all.length; i++) {
          const id = String(all[i].getAttribute("data-print-action-for")).toLowerCase();
          if (id && !seen[id]) {
            seen[id] = true;
            ids.push(id);
          }
        }
        return ids;
      },
      shownVersion: function (orderId) {
        const roots = rootsFor(orderId);
        if (roots.length === 0) return -1;
        let highest = 0;
        roots.forEach(function (rootEl) {
          const value = Number(rootEl.getAttribute("data-print-version"));
          if (isFinite(value) && value > highest) highest = value;
        });
        return highest;
      },
      apply: function (orderId, state) {
        rootsFor(orderId).forEach(function (rootEl) { applyToRoot(rootEl, state); });
      },
      isBusy: function (orderId) { return !!(page.isBusy && page.isBusy(orderId)); },
      submitGeneration: function (orderId) { return page.submitGeneration ? page.submitGeneration(orderId) : 0; }
    };
  }

  /** Writes a server state into one print action, with the labels the server rendered into it. */
  function applyToRoot(rootEl, state) {
    rootEl.setAttribute("data-print-version", String(state.version));

    const btn = rootEl.querySelector("[data-order-print]");
    if (btn) {
      btn.setAttribute("data-print-mode", state.mode);
      btn.disabled = state.mode === "queued" || state.mode === "printing";
      const label = btn.getAttribute("data-label-" + state.mode);
      const span = btn.querySelector("[data-print-label]");
      if (label && span && span.textContent !== label) span.textContent = label;
    }

    const outcome = rootEl.querySelector("[data-print-outcome]");
    if (outcome) {
      const text = state.outcome === "none" ? "" : (outcome.getAttribute("data-label-" + state.outcome) || "");
      outcome.setAttribute("data-print-outcome", state.outcome);
      outcome.classList.toggle("text-danger-emphasis", state.outcome === "failed");
      outcome.classList.toggle("text-success-emphasis", state.outcome !== "failed");
      // Written only when it changes, so the status region announces real changes once.
      if (outcome.textContent !== text) outcome.textContent = text;
    }
  }

  return {
    FALLBACK_INTERVAL_MS: FALLBACK_INTERVAL_MS,
    isValidState: isValidState,
    fetchPrintState: fetchPrintState,
    createPrintStatusSync: createPrintStatusSync,
    createDomView: createDomView,
    applyToRoot: applyToRoot
  };
});
