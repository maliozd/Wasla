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
        // Absolute: the client resolves a relative URL only when it is built, not when its URL changes (attempt label).
        hubUrl: new URL(hubUrl, root.location.href).href,
        setTimeout: root.setTimeout.bind(root),
        clearTimeout: root.clearTimeout.bind(root),
        random: Math.random
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
  // Every delay after the immediate first retry is lengthened by a random 0 to 50 %, so the screens dropped together by a
  // Wasla.Web restart do not all come back in the same second (a reconnect storm): at most 45 seconds.
  const JITTER_RATIO = 0.5;
  // The hub URL's query parameter that tells the server's metrics whether this is the page's first connection or a
  // reconnect (wasla.realtime.connection.attempts). A diagnostic label only: the server decides nothing with it.
  const ATTEMPT_PARAMETER = "attempt";

  function delayFor(attempt) {
    return attempt < RECONNECT_DELAYS_MS.length ? RECONNECT_DELAYS_MS[attempt] : MAX_DELAY_MS;
  }

  /** delayFor with the jitter; random() returns [0, 1). */
  function jitteredDelayFor(attempt, random) {
    const base = delayFor(attempt);
    if (base <= 0) return 0;
    const r = Number(random());
    return base + Math.floor(base * JITTER_RATIO * (r >= 0 && r < 1 ? r : 0));
  }

  function attemptUrl(hubUrl, kind) {
    return hubUrl + (hubUrl.indexOf("?") >= 0 ? "&" : "?") + ATTEMPT_PARAMETER + "=" + kind;
  }

  /**
   * options: signalR (the browser client), hubUrl, setTimeout, clearTimeout, random (optional, for the jitter).
   * States: "connecting" (never connected yet), "connected", "disconnected". Handlers registered with onState are told
   * every change; { reconnected: true } marks a connection that came back after a drop or a failed first attempt.
   * "connected" is reported when the hub says the connection's groups are joined, not when the transport opens: the
   * client's start completes at the handshake, before the server joins the groups, and a screen that read its state in
   * between could miss a change made just before the join.
   */
  function createRealtimeClient(options) {
    const signalR = options.signalR;
    const random = typeof options.random === "function" ? options.random : Math.random;
    const typeHandlers = {};
    const stateHandlers = [];
    let state = "connecting";
    let readyCount = 0;
    let failedBefore = false;
    let stopped = true;
    let startAttempt = 0;
    let startTimer = null;
    let starting = false;
    // The stop in progress (pagehide), or null. A start asked for meanwhile (pageshow from the back/forward cache)
    // waits for it: the client cannot start while it is still disconnecting.
    let stopping = null;
    // Read-only counters for tests and support (window.WaslaRealtime.get().stats()); never sent anywhere.
    const counters = { starts: 0, startFailures: 0, drops: 0, closes: 0, readies: 0, reconnects: 0, messages: 0 };

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(attemptUrl(options.hubUrl, "initial"))
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: function (context) { return jitteredDelayFor(context.previousRetryCount, random); }
      })
      .configureLogging(signalR.LogLevel.None)
      .build();

    // Handlers are attached before the first start, so nothing that arrives right after connecting is missed.
    connection.on(CLIENT_METHOD, function (message) {
      // A message already on its way when the page stopped (pagehide) reaches no screen.
      if (stopped || !message || typeof message.type !== "string") return;
      counters.messages += 1;
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
      counters.readies += 1;
      if (reconnected) counters.reconnects += 1;
      setState("connected", { reconnected: reconnected });
    });
    connection.onreconnecting(function () {
      counters.drops += 1;
      setAttempt("reconnect");
      setState("disconnected");
    });
    // A reconnect is a new server connection: it is reported as connected when its ready message arrives.
    connection.onreconnected(function () { /* wait for READY_METHOD */ });
    connection.onclose(function () {
      // Automatic reconnect gave up or the server refused the session: keep trying, slowly, while the page is open.
      counters.closes += 1;
      setState("disconnected");
      // The close of a stop in progress is not a drop: if the page wants to run again, the stop's end starts it.
      if (!stopped && !stopping) scheduleStart(jitteredDelayFor(RECONNECT_DELAYS_MS.length, random));
    });

    function setState(next, detail) {
      if (state === next && !(detail && detail.reconnected)) return;
      state = next;
      const info = Object.assign({ state: next, reconnected: false }, detail || {});
      stateHandlers.slice().forEach(function (handler) {
        try { handler(info); } catch (_) { /* ignore */ }
      });
    }

    // The URL can change only while disconnected or reconnecting; the client then uses it for its next negotiate. It
    // must be absolute there (the client resolves a relative URL only when it is built), so a relative hub URL keeps the
    // label it was built with.
    function setAttempt(kind) {
      if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(options.hubUrl)) return;
      try { connection.baseUrl = attemptUrl(options.hubUrl, kind); } catch (_) { /* keep the current URL */ }
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
      if (stopped || starting || stopping) return Promise.resolve();
      if (connection.state && connection.state !== signalR.HubConnectionState.Disconnected) return Promise.resolve();
      starting = true;
      counters.starts += 1;
      setAttempt(readyCount > 0 || failedBefore ? "reconnect" : "initial");
      return connection.start().then(function () {
        starting = false;
        startAttempt = 0;
        if (stopped) connection.stop();
        // Otherwise "connected" follows with the hub's ready message.
      }, function () {
        starting = false;
        failedBefore = true;
        counters.startFailures += 1;
        setState("disconnected");
        if (!stopped) scheduleStart(jitteredDelayFor(++startAttempt, random));
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
        const current = Promise.resolve(connection.stop())
          .catch(function () { /* already closed */ })
          .then(function () {
            if (stopping !== current) return;
            stopping = null;
            // Started again while it was stopping: connect now, not after a retry delay.
            if (!stopped) {
              clearStartTimer();
              attemptStart();
            }
          });
        stopping = current;
        return current;
      },
      state: function () { return state; },
      /** Counters since the page loaded: starts and failed starts, drops, closes, ready messages, reconnects, messages. */
      stats: function () { return Object.assign({ pendingStart: startTimer !== null }, counters); }
    };
  }

  return {
    CLIENT_METHOD: CLIENT_METHOD,
    READY_METHOD: READY_METHOD,
    JITTER_RATIO: JITTER_RATIO,
    ATTEMPT_PARAMETER: ATTEMPT_PARAMETER,
    delayFor: delayFor,
    jitteredDelayFor: jitteredDelayFor,
    attemptUrl: attemptUrl,
    createRealtimeClient: createRealtimeClient
  };
});
