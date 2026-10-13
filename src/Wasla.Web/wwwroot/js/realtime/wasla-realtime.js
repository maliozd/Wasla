// One live connection per page to the tenant notification hub (/hubs/tenant-events). Notifications only say that
// something changed; screens read the current state through their normal requests. The connection is optional: a
// screen must keep working (with its fallback refresh) when it never connects or drops.
(function (root, factory) {
  "use strict";

  const api = factory();
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
  if (!root || typeof root.document === "undefined") return;

  let shared = null;

  /** The page's single client; created on first use and stopped when the page goes away. */
  root.WaslaRealtime = {
    get: function (hubUrl) {
      if (shared) return shared;
      if (!hubUrl || !root.signalR) return null;
      shared = api.createRealtimeClient({
        signalR: root.signalR,
        hubUrl: hubUrl,
        setTimeout: root.setTimeout.bind(root),
        clearTimeout: root.clearTimeout.bind(root)
      });
      root.addEventListener("pagehide", function (ev) {
        // A page kept in the back/forward cache comes back through "pageshow" and starts again.
        if (shared) shared.stop();
        if (ev && ev.persisted) {
          root.addEventListener("pageshow", function () { if (shared) shared.start(); }, { once: true });
        }
      });
      return shared;
    }
  };
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  const CLIENT_METHOD = "tenantChange";
  // Sent by the hub once this connection's groups are joined; only then is the connection "connected" (see below).
  const READY_METHOD = "tenantEventsReady";
  // Automatic reconnect after a drop: quickly at first, then every 30 seconds for as long as the page is open.
  const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000];
  const MAX_DELAY_MS = 30000;

  function delayFor(attempt) {
    return attempt < RECONNECT_DELAYS_MS.length ? RECONNECT_DELAYS_MS[attempt] : MAX_DELAY_MS;
  }

  /**
   * options: signalR (the browser client), hubUrl, setTimeout, clearTimeout.
   * States: "connecting" (never connected yet), "connected", "disconnected". Handlers registered with onState are told
   * every change; { reconnected: true } marks a connection that came back after a drop or a failed first attempt.
   * "connected" is reported when the hub says the connection's groups are joined, not when the transport opens: the
   * client's start completes at the handshake, before the server joins the groups, and a screen that read its state in
   * between could miss a change made just before the join.
   */
  function createRealtimeClient(options) {
    const signalR = options.signalR;
    const typeHandlers = {};
    const stateHandlers = [];
    let state = "connecting";
    let readyCount = 0;
    let failedBefore = false;
    let stopped = true;
    let startAttempt = 0;
    let startTimer = null;
    let starting = false;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(options.hubUrl)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: function (context) { return delayFor(context.previousRetryCount); }
      })
      .configureLogging(signalR.LogLevel.None)
      .build();

    // Handlers are attached before the first start, so nothing that arrives right after connecting is missed.
    connection.on(CLIENT_METHOD, function (message) {
      if (!message || typeof message.type !== "string") return;
      const handlers = typeHandlers[message.type];
      if (!handlers) return;
      handlers.slice().forEach(function (handler) {
        try { handler(message); } catch (_) { /* one screen's failure does not stop the others */ }
      });
    });
    connection.on(READY_METHOD, function () {
      if (stopped) return;
      const reconnected = readyCount > 0 || failedBefore;
      readyCount += 1;
      setState("connected", { reconnected: reconnected });
    });
    connection.onreconnecting(function () { setState("disconnected"); });
    // A reconnect is a new server connection: it is reported as connected when its ready message arrives.
    connection.onreconnected(function () { /* wait for READY_METHOD */ });
    connection.onclose(function () {
      // Automatic reconnect gave up or the server refused the session: keep trying, slowly, while the page is open.
      setState("disconnected");
      if (!stopped) scheduleStart(MAX_DELAY_MS);
    });

    function setState(next, detail) {
      if (state === next && !(detail && detail.reconnected)) return;
      state = next;
      const info = Object.assign({ state: next, reconnected: false }, detail || {});
      stateHandlers.slice().forEach(function (handler) {
        try { handler(info); } catch (_) { /* ignore */ }
      });
    }

    function clearStartTimer() {
      if (startTimer) options.clearTimeout(startTimer);
      startTimer = null;
    }

    function scheduleStart(delay) {
      clearStartTimer();
      startTimer = options.setTimeout(function () {
        startTimer = null;
        attemptStart();
      }, delay);
    }

    // The first start is not covered by automatic reconnect, so it retries here with the same delays.
    function attemptStart() {
      if (stopped || starting) return Promise.resolve();
      if (connection.state && connection.state !== signalR.HubConnectionState.Disconnected) return Promise.resolve();
      starting = true;
      return connection.start().then(function () {
        starting = false;
        startAttempt = 0;
        if (stopped) connection.stop();
        // Otherwise "connected" follows with the hub's ready message.
      }, function () {
        starting = false;
        failedBefore = true;
        setState("disconnected");
        if (!stopped) scheduleStart(delayFor(++startAttempt));
      });
    }

    return {
      /** Subscribes to one notification type; returns the function that unsubscribes. */
      on: function (type, handler) {
        (typeHandlers[type] = typeHandlers[type] || []).push(handler);
        return function () {
          const list = typeHandlers[type] || [];
          const index = list.indexOf(handler);
          if (index >= 0) list.splice(index, 1);
        };
      },
      onState: function (handler) {
        stateHandlers.push(handler);
        return function () {
          const index = stateHandlers.indexOf(handler);
          if (index >= 0) stateHandlers.splice(index, 1);
        };
      },
      start: function () {
        if (!stopped) return Promise.resolve();
        stopped = false;
        return attemptStart();
      },
      stop: function () {
        stopped = true;
        clearStartTimer();
        return Promise.resolve(connection.stop()).catch(function () { /* already closed */ });
      },
      state: function () { return state; }
    };
  }

  return {
    CLIENT_METHOD: CLIENT_METHOD,
    READY_METHOD: READY_METHOD,
    delayFor: delayFor,
    createRealtimeClient: createRealtimeClient
  };
});
