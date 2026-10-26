// WAS-24: the page's live connection, the Live Screen invalidation and the snapshot coordinator together. The hub's
// ready message (sent after the groups are joined) is what makes the page "connected"; until then the Live Screen keeps
// its normal polling rate and reads nothing extra. Order training keeps real orders off the board, and real-order
// events, duplicates, reconnects and restores refresh the snapshot and announce each new real order once (WAS-117).
const test = require("node:test");
const assert = require("node:assert/strict");
const realtime = require("../../../src/Wasla.Web/wwwroot/js/realtime/wasla-realtime.js");
const invalidation = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-realtime.js");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

function clock() {
  let now = 0;
  let seq = 1;
  const timers = new Map();
  return {
    now: function () { return now; },
    timers: timers,
    setTimeout: function (fn, ms) { const id = seq++; timers.set(id, { fn: fn, at: now + ms }); return id; },
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
    }
  };
}

// A SignalR stand-in whose hub sends the ready message only when the test says so.
function fakeSignalR() {
  const api = {
    registrations: [],
    builds: 0,
    HubConnectionState: { Disconnected: "Disconnected", Connected: "Connected" },
    LogLevel: { None: 6 },
    HubConnectionBuilder: function () {
      const self = this;
      self.withUrl = function () { return self; };
      self.withAutomaticReconnect = function () { return self; };
      self.configureLogging = function () { return self; };
      self.build = function () {
        api.builds += 1;
        const handlers = { on: {}, reconnecting: [], reconnected: [], close: [] };
        api.connection = {
          state: "Disconnected",
          handlers: handlers,
          on: function (name, fn) { api.registrations.push(name); handlers.on[name] = fn; },
          onreconnecting: function (fn) { handlers.reconnecting.push(fn); },
          onreconnected: function (fn) { handlers.reconnected.push(fn); },
          onclose: function (fn) { handlers.close.push(fn); },
          start: function () {
            api.registrations.push("start");
            api.connection.state = "Connected";
            return Promise.resolve();
          },
          stop: function () {
            api.connection.state = "Disconnected";
            handlers.close.forEach(function (fn) { fn(); });
            return Promise.resolve();
          }
        };
        return api.connection;
      };
    },
    ready: function () { api.connection.handlers.on.tenantEventsReady(); },
    send: function (message) { api.connection.handlers.on.tenantChange(message); },
    drop: function () { api.connection.state = "Reconnecting"; api.connection.handlers.reconnecting.forEach(function (fn) { fn(); }); },
    reconnected: function () { api.connection.state = "Connected"; api.connection.handlers.reconnected.forEach(function (fn) { fn(); }); }
  };
  return api;
}

function page(options) {
  const c = clock();
  const signalR = fakeSignalR();
  const calls = { reads: 0, intervals: [] };
  const client = realtime.createRealtimeClient({ signalR: signalR, hubUrl: "/hubs/tenant-events", setTimeout: c.setTimeout, clearTimeout: c.clearTimeout });
  const live = invalidation.createLiveInvalidation({
    requestRefresh: (options && options.requestRefresh) || function () { calls.reads += 1; return Promise.resolve(); },
    setPollingIntervalMs: function (ms) { calls.intervals.push(ms); },
    shownVersion: (options && options.shownVersion) || function () { return -1; },
    // The rates a page renders when Web's internal listener is on (WAS-103).
    disconnectedPollingMs: 10000,
    connectedPollingMs: 30000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });
  client.on("OrderChanged", live.onNotification);
  client.onState(live.onConnectionState);
  live.onConnectionState({ state: client.state() });
  return { c: c, signalR: signalR, calls: calls, client: client, live: live };
}

test("the ready handler is registered before start, and nothing is 'connected' or read before ready", async function () {
  const p = page();
  p.client.start();
  await flush();

  assert.ok(p.signalR.registrations.indexOf("tenantEventsReady") < p.signalR.registrations.indexOf("start"));
  assert.equal(p.client.state(), "connecting");
  assert.equal(p.calls.reads, 0);
  assert.deepEqual(p.calls.intervals, [10000]); // still the normal polling rate

  p.signalR.ready();
  assert.equal(p.client.state(), "connected");
  assert.equal(p.calls.reads, 1); // the full read happens only once the groups are joined
  assert.deepEqual(p.calls.intervals, [10000, 30000]);
});

test("every reconnect waits for a new ready before reading or lowering the polling rate", async function () {
  const p = page();
  p.client.start();
  await flush();
  p.signalR.ready();

  p.signalR.drop();
  assert.deepEqual(p.calls.intervals, [10000, 30000, 10000]);
  p.signalR.reconnected(); // transport back, groups not joined yet
  assert.equal(p.client.state(), "disconnected");
  assert.equal(p.calls.reads, 1);
  assert.deepEqual(p.calls.intervals, [10000, 30000, 10000]);

  p.signalR.ready();
  assert.equal(p.client.state(), "connected");
  assert.equal(p.calls.reads, 2);
  assert.deepEqual(p.calls.intervals, [10000, 30000, 10000, 30000]);
});

test("a missing ready leaves no timers or extra handlers behind, however often the page stops and starts", async function () {
  const p = page();
  for (let i = 0; i < 3; i++) {
    p.client.start();
    await flush();
    p.client.stop();
    await flush();
  }

  assert.equal(p.signalR.builds, 1);
  assert.equal(p.signalR.registrations.filter(function (r) { return r === "tenantEventsReady"; }).length, 1);
  assert.equal(p.signalR.registrations.filter(function (r) { return r === "tenantChange"; }).length, 1);
  assert.equal(p.c.timers.size, 0);
  assert.equal(p.calls.reads, 0);
  assert.equal(p.client.state(), "disconnected");
});

test("a ready that arrives after stop does not mark the page connected", async function () {
  const p = page();
  p.client.start();
  await flush();
  p.client.stop();
  await flush();

  p.signalR.ready();
  assert.notEqual(p.client.state(), "connected");
  assert.equal(p.calls.reads, 0);
});

// Snapshots as the server sends them during isolated order training: no real order on the board, only the ids of
// the hidden ones and how many are waiting.
function trainingSnapshot(realOrderIds) {
  return {
    serverTimeUtc: "2026-10-13T10:00:00Z",
    orders: [],
    todayOrderCount: 0,
    cancelledOrderCount: 0,
    training: { isolated: true, waitingRealOrders: realOrderIds.length, realOrderIds: realOrderIds.slice() }
  };
}

test("during order training, real-order events, duplicates, reconnects and restores announce a hidden real order once", async function () {
  const c = clock();
  const announced = [];
  const accepted = [];
  const realOrder = "8a1f0c6e-0000-4000-8000-0000000000aa";
  let hidden = [];
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    createAbortController: function () { return { signal: {}, abort: function () {} }; },
    fetchSnapshot: function () { return Promise.resolve({ kind: "ok", snapshot: trainingSnapshot(hidden) }); },
    onStatus: function () {},
    onAccepted: function (snapshot, meta) { accepted.push([snapshot.training.waitingRealOrders, meta.isBaseline]); return { accepted: true }; },
    onNotify: function (snapshot, meta) { announced.push(meta.newIds); }
  });
  const p = page({ requestRefresh: function () { return coordinator.requestRefresh(); } });
  await coordinator.start();
  p.client.start();
  await flush();
  p.signalR.ready();
  await flush();

  // A real order arrives: the event (and three duplicates of it) refresh the count and announce it once.
  hidden = [realOrder];
  for (let i = 0; i < 4; i++) p.signalR.send({ type: "OrderChanged", orderId: realOrder, version: 1 });
  p.c.advance(invalidation.SETTLE_MS);
  await flush();

  // A drop and reconnect, then a back/forward-cache style stop and start: each reads again, none announces again.
  p.signalR.drop();
  p.signalR.reconnected();
  p.signalR.ready();
  await flush();
  p.client.stop();
  await flush();
  p.client.start();
  await flush();
  p.signalR.ready();
  await flush();

  assert.deepEqual(announced, [[realOrder]]);
  assert.ok(accepted.some(function (a) { return a[0] === 1; }), "the waiting real-order count was not refreshed");
  assert.ok(accepted.every(function (a) { return a[0] <= 1; }));
  assert.equal(accepted.filter(function (a) { return a[1]; }).length, 1, "only the first snapshot is a baseline");
});
