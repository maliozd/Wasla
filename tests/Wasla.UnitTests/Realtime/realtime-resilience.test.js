// WAS-25: the browser side of the live channel under reconnects, repeated start/stop, bursts and disposal.
// Deterministic: a fake SignalR client, a manual clock and a seeded random source; nothing waits on real time.
const test = require("node:test");
const assert = require("node:assert/strict");
const realtime = require("../../../src/Wasla.Web/wwwroot/js/realtime/wasla-realtime.js");
const invalidation = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-realtime.js");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

// A manual clock: timers run only when the test advances time.
function clock() {
  let now = 0;
  let seq = 1;
  const timers = new Map();
  return {
    now: function () { return now; },
    timers: timers,
    setTimeout: function (fn, ms) { const id = seq++; timers.set(id, { fn: fn, at: now + ms, ms: ms }); return id; },
    clearTimeout: function (id) { timers.delete(id); },
    advance: function (ms) {
      const until = now + ms;
      for (;;) {
        const due = [...timers.entries()].filter(function (e) { return e[1].at <= until; }).sort(function (a, b) { return a[1].at - b[1].at; })[0];
        if (!due) break;
        timers.delete(due[0]);
        now = due[1].at;
        due[1].fn();
      }
      now = until;
    },
    pending: function () { return [...timers.values()].map(function (t) { return t.ms; }); }
  };
}

// A small seeded generator (mulberry32), so a "storm" of clients is the same on every run.
function seeded(seed) {
  let a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// A stand-in for the browser SignalR client: like the real one, the URL can change only while disconnected or
// reconnecting, and every start uses the URL current at that moment.
function fakeSignalR() {
  const api = {
    builds: 0,
    starts: [],
    urls: [],
    HubConnectionState: { Disconnected: "Disconnected", Connected: "Connected", Reconnecting: "Reconnecting" },
    LogLevel: { None: 6 },
    connection: null,
    HubConnectionBuilder: function () {
      const self = this;
      let url = null;
      // Like the real client, a relative URL is resolved when the connection is built, never when it is changed later.
      self.withUrl = function (u) { url = /^https?:\/\//.test(u) ? u : "https://tenant.example" + u; return self; };
      self.withAutomaticReconnect = function (policy) { api.policy = policy; return self; };
      self.configureLogging = function () { return self; };
      self.build = function () {
        api.builds += 1;
        const handlers = { on: {}, reconnecting: [], reconnected: [], close: [] };
        const connection = {
          state: "Disconnected",
          handlers: handlers,
          get baseUrl() { return url; },
          set baseUrl(value) {
            if (connection.state !== "Disconnected" && connection.state !== "Reconnecting") throw new Error("not now");
            url = value;
          },
          on: function (name, fn) { handlers.on[name] = fn; },
          onreconnecting: function (fn) { handlers.reconnecting.push(fn); },
          onreconnected: function (fn) { handlers.reconnected.push(fn); },
          onclose: function (fn) { handlers.close.push(fn); },
          start: function () {
            api.urls.push(url);
            if (!/^https?:\/\//.test(url)) return Promise.reject(new TypeError("Invalid URL"));
            const outcome = api.starts.shift() || "ok";
            if (outcome === "ok") {
              connection.state = "Connected";
              Promise.resolve().then(function () { handlers.on.tenantEventsReady(); });
              return Promise.resolve();
            }
            return Promise.reject(new Error("negotiate failed"));
          },
          stop: function () {
            // With holdStop, a stop stays in "Disconnecting" until the test calls finishStop(), like the real client
            // while it closes its transport; a second stop meanwhile gets the same pending promise. When it finishes
            // the close handlers run first and then the promise resolves, in that order, as in the real client.
            if (api.pendingStop) return api.pendingStop.promise;
            const wasOpen = connection.state !== "Disconnected";
            if (!api.holdStop) {
              connection.state = "Disconnected";
              if (wasOpen) handlers.close.forEach(function (fn) { fn(); });
              return Promise.resolve();
            }
            connection.state = "Disconnecting";
            let resolve;
            const promise = new Promise(function (r) { resolve = r; });
            api.pendingStop = {
              promise: promise,
              finish: function () {
                api.pendingStop = null;
                connection.state = "Disconnected";
                if (wasOpen) handlers.close.forEach(function (fn) { fn(); });
                resolve();
              }
            };
            api.stopCalls = (api.stopCalls || 0) + 1;
            return promise;
          },
          emit: function (message) { handlers.on.tenantChange(message); },
          drop: function () {
            connection.state = "Reconnecting";
            handlers.reconnecting.forEach(function (fn) { fn(); });
          },
          back: function () {
            api.urls.push(url);
            connection.state = "Connected";
            handlers.reconnected.forEach(function (fn) { fn(); });
            handlers.on.tenantEventsReady();
          }
        };
        api.connection = connection;
        return connection;
      };
    }
  };
  return api;
}

// The page passes an absolute hub URL (WaslaRealtime.get resolves it against the page).
function client(signalR, c, random, hubUrl) {
  return realtime.createRealtimeClient({
    signalR: signalR,
    hubUrl: hubUrl || "https://tenant.example/hubs/tenant-events",
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    random: random || function () { return 0; }
  });
}

test("reconnect delays get 0 to 50 % jitter, the immediate first retry stays immediate, and bad random values add none", function () {
  const bases = [0, 2000, 5000, 10000, 30000, 30000];
  bases.forEach(function (base, attempt) {
    assert.equal(realtime.jitteredDelayFor(attempt, function () { return 0; }), base);
    const highest = realtime.jitteredDelayFor(attempt, function () { return 0.999999; });
    assert.ok(highest >= base && highest < base * 1.5 + 1, `attempt ${attempt}: ${highest}`);
  });
  [NaN, 1, -0.1, 7, undefined].forEach(function (bad) {
    assert.equal(realtime.jitteredDelayFor(1, function () { return bad; }), 2000);
  });
  // The longest wait between two attempts is 45 seconds.
  assert.equal(realtime.jitteredDelayFor(100, function () { return 0.9999999; }), 44999);
});

test("a Wasla.Web restart that drops many screens at once does not bring them back in the same instant", function () {
  // 300 screens, each with its own random source, all dropped together; their second and fifth retries.
  const random = seeded(25);
  const second = [];
  const fifth = [];
  for (let i = 0; i < 300; i++) {
    const signalR = fakeSignalR();
    const r = function () { return random(); };
    client(signalR, clock(), r);
    second.push(signalR.policy.nextRetryDelayInMilliseconds({ previousRetryCount: 1 }));
    fifth.push(signalR.policy.nextRetryDelayInMilliseconds({ previousRetryCount: 4 }));
  }
  // The most screens that retry within any one 100 ms window.
  function peak(delays) {
    const buckets = {};
    delays.forEach(function (d) { const w = Math.floor(d / 100); buckets[w] = (buckets[w] || 0) + 1; });
    return Math.max.apply(null, Object.values(buckets));
  }
  // Without jitter all 300 would retry in the same instant. The second retry spreads them over one second (about 30
  // per 100 ms), the fifth over fifteen seconds (about 2 per 100 ms).
  assert.ok(peak(second) <= 60, `second retry: ${peak(second)} within 100 ms`);
  assert.ok(peak(fifth) <= 10, `fifth retry: ${peak(fifth)} within 100 ms`);
  assert.ok(Math.min.apply(null, second) >= 2000 && Math.max.apply(null, second) < 3000);
  assert.ok(Math.min.apply(null, fifth) >= 30000 && Math.max.apply(null, fifth) < 45000);
});

test("the hub URL says whether a connection is the page's first or a reconnect, for the server's metrics only", async function () {
  const signalR = fakeSignalR();
  const c = clock();
  const live = client(signalR, c);

  live.start();
  await flush();
  // A drop and automatic reconnect, then a back/forward-cache stop and start, then a closed connection restarted.
  signalR.connection.drop();
  signalR.connection.back();
  await live.stop();
  live.start();
  await flush();
  signalR.connection.state = "Disconnected";
  signalR.connection.handlers.close.forEach(function (fn) { fn(); });
  c.advance(30000);
  await flush();

  assert.deepEqual(signalR.urls, [
    "https://tenant.example/hubs/tenant-events?attempt=initial",
    "https://tenant.example/hubs/tenant-events?attempt=reconnect",
    "https://tenant.example/hubs/tenant-events?attempt=reconnect",
    "https://tenant.example/hubs/tenant-events?attempt=reconnect"
  ]);
  assert.equal(realtime.attemptUrl("/hubs/tenant-events?x=1", "initial"), "/hubs/tenant-events?x=1&attempt=initial");

  // A relative hub URL keeps the URL the connection was built with instead of breaking the next start.
  const relative = fakeSignalR();
  const other = client(relative, clock(), null, "/hubs/tenant-events");
  other.start();
  await flush();
  relative.connection.drop();
  relative.connection.back();
  assert.deepEqual(relative.urls, ["https://tenant.example/hubs/tenant-events?attempt=initial", "https://tenant.example/hubs/tenant-events?attempt=initial"]);
  assert.equal(other.state(), "connected");
  const stats = live.stats();
  assert.equal(stats.readies, 4);
  assert.equal(stats.reconnects, 3);
  assert.equal(stats.drops, 1);
});

test("a failed first start retries as a reconnect, and counts the failures", async function () {
  const signalR = fakeSignalR();
  signalR.starts.push("fail", "fail");
  const c = clock();
  const live = client(signalR, c);

  live.start();
  await flush();
  c.advance(2000);
  await flush();
  c.advance(5000);
  await flush();

  assert.deepEqual(signalR.urls, [
    "https://tenant.example/hubs/tenant-events?attempt=initial",
    "https://tenant.example/hubs/tenant-events?attempt=reconnect",
    "https://tenant.example/hubs/tenant-events?attempt=reconnect"
  ]);
  assert.equal(live.state(), "connected");
  assert.deepEqual(
    { starts: live.stats().starts, startFailures: live.stats().startFailures, reconnects: live.stats().reconnects, pendingStart: live.stats().pendingStart },
    { starts: 3, startFailures: 2, reconnects: 1, pendingStart: false });
});

test("100 stop/start cycles keep one connection, one set of handlers and no timers", async function () {
  const signalR = fakeSignalR();
  const c = clock();
  const live = client(signalR, c);
  const states = [];
  live.onState(function (info) { states.push(info.state); });
  let received = 0;
  live.on("OrderChanged", function () { received += 1; });

  for (let i = 0; i < 100; i++) {
    live.start();
    await flush();
    assert.equal(live.state(), "connected");
    await live.stop();
    assert.equal(live.state(), "disconnected");
  }
  live.start();
  await flush();
  signalR.connection.emit({ type: "OrderChanged", orderId: "a", version: 1 });

  assert.equal(signalR.builds, 1);
  assert.equal(c.timers.size, 0);
  assert.equal(received, 1, "one handler, called once");
  assert.equal(states.filter(function (s) { return s === "connected"; }).length, 101);
  assert.equal(live.stats().readies, 101);
});

// pagehide calls stop() and pageshow (back/forward cache) can call start() before that stop has finished.
test("a start asked for while a stop is still completing connects as soon as the stop completes, with one connection and no timer", async function () {
  const signalR = fakeSignalR();
  const c = clock();
  const live = client(signalR, c);
  live.start();
  await flush();
  assert.equal(live.state(), "connected");

  signalR.holdStop = true;
  const stopping = live.stop();
  live.start();
  await flush();
  // Nothing may start while the old connection is still closing.
  assert.equal(signalR.connection.state, "Disconnecting");
  assert.equal(signalR.urls.length, 1, "a second start was attempted before the stop completed");

  signalR.pendingStop.finish();
  await stopping;
  await flush();

  // Started again at once (not after the 30-45 s retry), on the same connection, with nothing left scheduled.
  assert.equal(signalR.urls.length, 2);
  assert.equal(signalR.urls[1], "https://tenant.example/hubs/tenant-events?attempt=reconnect");
  assert.equal(live.state(), "connected");
  assert.equal(signalR.builds, 1);
  assert.equal(c.timers.size, 0, "a retry timer was left behind: " + JSON.stringify(c.pending()));
  assert.equal(live.stats().pendingStart, false);
});

test("repeated stop/start while a stop is completing ends in one start, and a final stop ends in none", async function () {
  const signalR = fakeSignalR();
  const c = clock();
  const live = client(signalR, c);
  live.start();
  await flush();

  // pagehide/pageshow several times before the first stop completes: still started once.
  signalR.holdStop = true;
  live.stop();
  live.start();
  live.stop();
  live.start();
  live.stop();
  live.start();
  await flush();
  assert.equal(signalR.urls.length, 1);
  signalR.pendingStop.finish();
  await flush();
  await flush();
  assert.equal(signalR.urls.length, 2);
  assert.equal(live.state(), "connected");
  assert.equal(c.timers.size, 0);
  assert.equal(signalR.builds, 1);

  // The page goes away again before the stop completes: nothing starts and nothing is scheduled.
  live.stop();
  live.start();
  live.stop();
  signalR.pendingStop.finish();
  await flush();
  await flush();
  assert.equal(signalR.urls.length, 2);
  assert.equal(live.state(), "disconnected");
  assert.equal(c.timers.size, 0);
});

test("after stop, a message or ready already on its way reaches no screen", async function () {
  const signalR = fakeSignalR();
  const live = client(signalR, clock());
  let received = 0;
  const states = [];
  live.on("OrderChanged", function () { received += 1; });
  live.onState(function (info) { states.push(info.state); });
  live.start();
  await flush();

  await live.stop();
  signalR.connection.emit({ type: "OrderChanged", orderId: "a", version: 2 });
  signalR.connection.handlers.on.tenantEventsReady();

  assert.equal(received, 0);
  assert.deepEqual(states, ["connected", "disconnected"]);
});

// The Live Screen's invalidation module over the real snapshot coordinator, with fetches the test resolves.
function liveScreen(c) {
  const fetches = [];
  const applied = [];
  let notified = 0;
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    createAbortController: function () { return { signal: {}, abort: function () {} }; },
    fetchSnapshot: function () {
      return new Promise(function (resolve) { fetches.push(resolve); });
    },
    onStatus: function () {},
    onAccepted: function (snapshot) { applied.push(snapshot.serverTimeUtc); return { accepted: true }; },
    onNotify: function (snapshot, meta) { notified += meta.newIds.length ? 1 : 0; }
  });
  const sync = invalidation.createLiveInvalidation({
    requestRefresh: function () { return coordinator.requestRefresh(); },
    setPollingIntervalMs: function (ms) { coordinator.setIntervalMs(ms); },
    shownVersion: function (id) {
      const shown = coordinator.lastSnapshot();
      const order = shown && shown.orders.find(function (o) { return o.id === id; });
      return order ? order.changeVersion : -1;
    },
    disconnectedPollingMs: 10000,
    connectedPollingMs: 30000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });
  return { coordinator: coordinator, sync: sync, fetches: fetches, applied: applied, notified: function () { return notified; } };
}

function id(n) {
  return "00000000-0000-4000-8000-" + String(n).padStart(12, "0");
}

function response(at, orders) {
  return {
    kind: "ok",
    snapshot: {
      serverTimeUtc: at,
      todayOrderCount: orders.length,
      cancelledOrderCount: 0,
      orders: orders.map(function (o) {
        return {
          id: o.id, displayNumber: "TY", platform: "TrendyolYemek", status: "New", receivedAtUtc: "2026-10-13T10:00:00Z",
          deliveredAtUtc: null, customerName: "Ada", totalAmount: 10, changeVersion: o.version,
          items: [{ productName: "Lahmacun", quantity: 1, notes: null }]
        };
      })
    }
  };
}

test("a burst of 1,000 invalidations over ten seconds costs at most one snapshot read per second, one at a time", async function () {
  const c = clock();
  const screen = liveScreen(c);
  screen.sync.onConnectionState({ state: "connected" });
  await flush();
  assert.equal(screen.fetches.length, 1);
  screen.fetches.shift()(response("2026-10-13T10:00:00Z", []));
  await flush();

  let inFlightMax = 0;
  let fetchesStarted = 1;
  for (let i = 0; i < 1000; i++) {
    screen.sync.onNotification({ type: "OrderChanged", orderId: id(i % 50), version: i + 1 });
    c.advance(10);
    await flush();
    inFlightMax = Math.max(inFlightMax, screen.fetches.length);
    // Every read answers 200 ms of event time later, while notifications keep arriving.
    if (screen.fetches.length && c.now() % 200 === 0) {
      fetchesStarted += 1;
      screen.fetches.shift()(response("2026-10-13T10:00:00Z", []));
      await flush();
    }
  }
  for (let i = 0; i < 5; i++) {
    c.advance(1000);
    await flush();
    while (screen.fetches.length) {
      fetchesStarted += 1;
      screen.fetches.shift()(response("2026-10-13T10:00:00Z", []));
      await flush();
    }
  }

  const stats = screen.sync.stats();
  assert.equal(stats.notifications, 1000);
  assert.equal(inFlightMax, 1, "the coordinator never runs two snapshot reads at once");
  // Ten seconds of events: at most one notification-triggered read per second (plus the connect read) reached the
  // coordinator, and it fetched no more than that plus its single follow-ups.
  assert.ok(stats.reads <= 12, `reads asked for: ${stats.reads}`);
  assert.ok(stats.coalesced >= 1000 - 12, `coalesced: ${stats.coalesced}`);
  assert.ok(fetchesStarted <= 2 * stats.reads, `snapshot fetches: ${fetchesStarted}`);
});

test("an invalidation during a snapshot read causes exactly one follow-up, and an older answer never replaces a newer one", async function () {
  const c = clock();
  const screen = liveScreen(c);
  screen.sync.onConnectionState({ state: "connected" });
  await flush();
  screen.fetches.shift()(response("2026-10-13T10:00:00Z", [{ id: id(1), version: 1 }]));
  await flush();

  // A read for version 2 is under way when versions 3, 4 and 5 arrive.
  screen.sync.onNotification({ type: "OrderChanged", orderId: id(1), version: 2 });
  c.advance(1000);
  await flush();
  assert.equal(screen.fetches.length, 1);
  for (let v = 3; v <= 5; v++) {
    screen.sync.onNotification({ type: "OrderChanged", orderId: id(1), version: v });
    c.advance(1000);
    await flush();
  }
  assert.equal(screen.fetches.length, 1, "still one read in flight");

  screen.fetches.shift()(response("2026-10-13T10:00:02Z", [{ id: id(1), version: 2 }]));
  await flush();
  await flush();
  // Its single follow-up reads the newest state.
  assert.equal(screen.fetches.length, 1);
  screen.fetches.shift()(response("2026-10-13T10:00:05Z", [{ id: id(1), version: 5 }]));
  await flush();
  c.advance(2000);
  await flush();

  assert.deepEqual(screen.applied, ["2026-10-13T10:00:00Z", "2026-10-13T10:00:02Z", "2026-10-13T10:00:05Z"]);
  assert.equal(screen.fetches.length, 0, "no further read: version 5 is shown");
  // A late duplicate of an older version reads nothing.
  screen.sync.onNotification({ type: "OrderChanged", orderId: id(1), version: 4 });
  c.advance(2000);
  await flush();
  assert.equal(screen.fetches.length, 0);
});

test("a new order is announced once however many invalidations, reconnects and duplicate reads bring it", async function () {
  const c = clock();
  const screen = liveScreen(c);
  screen.sync.onConnectionState({ state: "connected" });
  await flush();
  screen.fetches.shift()(response("2026-10-13T10:00:00Z", []));
  await flush();

  const arrived = [{ id: id(7), version: 1 }];
  for (let i = 0; i < 20; i++) screen.sync.onNotification({ type: "OrderChanged", orderId: id(7), version: 1 });
  c.advance(1000);
  await flush();
  screen.fetches.shift()(response("2026-10-13T10:00:01Z", arrived));
  await flush();
  // A drop, a reconnect and a back/forward-cache restore each read the snapshot again.
  for (let i = 0; i < 3; i++) {
    screen.sync.onConnectionState({ state: "disconnected" });
    screen.sync.onConnectionState({ state: "connected", reconnected: true });
    await flush();
    screen.fetches.shift()(response("2026-10-13T10:00:0" + (2 + i) + "Z", arrived));
    await flush();
  }

  assert.equal(screen.notified(), 1);
  assert.equal(screen.sync.stats().connectReads, 4);
});

test("a disposed Live Screen reads nothing, schedules nothing and counts what it ignored", function () {
  const c = clock();
  const screen = liveScreen(c);
  screen.sync.onConnectionState({ state: "connected" });
  screen.sync.onNotification({ type: "OrderChanged", orderId: id(1), version: 9 });
  assert.equal(screen.sync.stats().pendingRead, true);

  screen.sync.dispose();
  for (let i = 0; i < 10; i++) screen.sync.onNotification({ type: "OrderChanged", orderId: id(1), version: 10 + i });
  screen.sync.onConnectionState({ state: "connected", reconnected: true });
  c.advance(60000);

  const stats = screen.sync.stats();
  assert.equal(stats.reads, 1, "only the connect read before disposal");
  assert.equal(stats.ignoredAfterDispose, 10);
  assert.equal(stats.pendingRead, false);
  assert.equal(stats.disposed, true);
});
