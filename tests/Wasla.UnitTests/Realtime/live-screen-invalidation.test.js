const test = require("node:test");
const assert = require("node:assert/strict");
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

function live(c, shown) {
  const calls = { reads: 0, intervals: [] };
  const versions = shown || {};
  const sync = invalidation.createLiveInvalidation({
    requestRefresh: function () { calls.reads += 1; return Promise.resolve(); },
    setPollingIntervalMs: function (ms) { calls.intervals.push(ms); },
    shownVersion: function (id) { return id in versions ? versions[id] : -1; },
    disconnectedPollingMs: 10000,
    connectedPollingMs: 30000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });
  return { sync: sync, calls: calls };
}

function changed(version) {
  return { type: "OrderChanged", orderId: "8a1f0c6e-0000-4000-8000-000000000001", version: version };
}

test("connected: a lower polling rate and an immediate full read; disconnected: the normal rate again", function () {
  const c = clock();
  const { sync, calls } = live(c);

  sync.onConnectionState({ state: "connecting" });
  assert.deepEqual(calls.intervals, [10000]);
  assert.equal(calls.reads, 0);

  sync.onConnectionState({ state: "connected", reconnected: false });
  assert.deepEqual(calls.intervals, [10000, 30000]);
  assert.equal(calls.reads, 1);

  sync.onConnectionState({ state: "disconnected" });
  assert.deepEqual(calls.intervals, [10000, 30000, 10000]);

  // A reconnect (also after the back/forward cache) reads the full snapshot at once.
  sync.onConnectionState({ state: "connected", reconnected: true });
  assert.equal(calls.reads, 2);
  assert.deepEqual(calls.intervals, [10000, 30000, 10000, 30000]);
});

test("the connected rate is lowered only when the page says Web receives Worker and Api changes (WAS-103)", function () {
  // Web's internal listener on: the page renders the lower connected rate.
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: 30000 }), { disconnected: 10000, connected: 30000 });
  // Listener off: the page renders the normal rate for both, so a Worker order still shows within 10 seconds.
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: 10000 }), { disconnected: 10000, connected: 10000 });
  // No connected rate on the page, or an unusable one: the browser never lowers the rate on its own.
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000 }), { disconnected: 10000, connected: 10000 });
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: 0 }), { disconnected: 10000, connected: 10000 });
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: -1 }), { disconnected: 10000, connected: 10000 });
  assert.deepEqual(invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: "30000" }), { disconnected: 10000, connected: 10000 });
  assert.deepEqual(invalidation.pollingRates({}), { disconnected: 10000, connected: 10000 });
  assert.deepEqual(invalidation.pollingRates(undefined), { disconnected: 10000, connected: 10000 });
});

test("with Web's internal listener off, a connected Live Screen keeps polling every 10 seconds (WAS-103)", function () {
  const c = clock();
  const calls = { reads: 0, intervals: [] };
  const rates = invalidation.pollingRates({ pollingIntervalMs: 10000, connectedPollingIntervalMs: 10000 });
  const sync = invalidation.createLiveInvalidation({
    requestRefresh: function () { calls.reads += 1; return Promise.resolve(); },
    setPollingIntervalMs: function (ms) { calls.intervals.push(ms); },
    shownVersion: function () { return -1; },
    disconnectedPollingMs: rates.disconnected,
    connectedPollingMs: rates.connected,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });

  sync.onConnectionState({ state: "connecting" });
  sync.onConnectionState({ state: "connected", reconnected: false });
  // Still connected, still the normal rate; the read on connect is unchanged.
  assert.deepEqual(calls.intervals, [10000, 10000]);
  assert.equal(calls.reads, 1);
  sync.onConnectionState({ state: "disconnected" });
  assert.deepEqual(calls.intervals, [10000, 10000, 10000]);
});

test("without a connected rate, the invalidation keeps the disconnected rate when connected", function () {
  const c = clock();
  const intervals = [];
  const sync = invalidation.createLiveInvalidation({
    requestRefresh: function () { return Promise.resolve(); },
    setPollingIntervalMs: function (ms) { intervals.push(ms); },
    disconnectedPollingMs: 10000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });

  sync.onConnectionState({ state: "connected", reconnected: false });
  assert.deepEqual(intervals, [10000]);
});

test("a burst of notifications becomes one read, and reads are at least a second apart", function () {
  const c = clock();
  const { sync, calls } = live(c);

  for (let i = 0; i < 25; i++) sync.onNotification(changed(i));
  // Duplicates of the same change collapse the same way.
  sync.onNotification(changed(3));
  sync.onNotification(changed(3));
  assert.equal(calls.reads, 0);
  c.advance(invalidation.SETTLE_MS);
  assert.equal(calls.reads, 1);

  // More changes right after: the next read waits for the one-second gap.
  sync.onNotification(changed(30));
  c.advance(invalidation.SETTLE_MS);
  assert.equal(calls.reads, 1);
  c.advance(invalidation.MIN_GAP_MS);
  assert.equal(calls.reads, 2);

  // After a quiet second, a single change is read after the short settle window.
  c.advance(5000);
  sync.onNotification(changed(31));
  c.advance(invalidation.SETTLE_MS);
  assert.equal(calls.reads, 3);
});

test("a version the snapshot already shows is not read again; a newer one or an unshown order is", function () {
  const c = clock();
  const ORDER = "8a1f0c6e-0000-4000-8000-000000000001";
  const shown = { [ORDER]: 4 };
  const { sync, calls } = live(c, shown);

  sync.onNotification({ type: "OrderChanged", orderId: ORDER, version: 3 }); // stale
  sync.onNotification({ type: "OrderChanged", orderId: ORDER, version: 4 }); // duplicate
  c.advance(5000);
  assert.equal(calls.reads, 0);

  sync.onNotification({ type: "OrderChanged", orderId: ORDER, version: 5 }); // newer
  c.advance(invalidation.SETTLE_MS);
  assert.equal(calls.reads, 1);

  // An order the screen does not show (new, or hidden by training): always read.
  c.advance(5000);
  sync.onNotification({ type: "OrderChanged", orderId: "8a1f0c6e-0000-4000-8000-000000000009", version: 1 });
  c.advance(invalidation.SETTLE_MS);
  assert.equal(calls.reads, 2);
});

test("other notification types and malformed messages read nothing", function () {
  const c = clock();
  const { sync, calls } = live(c);

  sync.onNotification({ type: "PrintJobChanged", orderId: "x", version: 1 });
  sync.onNotification(null);
  sync.onNotification({ orderId: "x" });
  c.advance(5000);

  assert.equal(calls.reads, 0);
});

test("a (re)connect takes over a scheduled read instead of adding a second one", function () {
  const c = clock();
  const { sync, calls } = live(c);

  sync.onNotification(changed(1));
  assert.equal(sync.hasPendingRead(), true);
  sync.onConnectionState({ state: "connected", reconnected: true });
  c.advance(5000);

  assert.equal(calls.reads, 1);
  assert.equal(sync.hasPendingRead(), false);
});

test("after dispose nothing is read and no timer stays behind", function () {
  const c = clock();
  const { sync, calls } = live(c);
  sync.onNotification(changed(1));

  sync.dispose();
  sync.onNotification(changed(2));
  sync.onConnectionState({ state: "connected", reconnected: true });
  c.advance(5000);

  assert.equal(calls.reads, 0);
  assert.equal(c.timers.size, 0);
});

function coordinatorWith(c, fetchSnapshot) {
  return store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    createAbortController: function () { return { signal: {}, abort: function () {} }; },
    fetchSnapshot: fetchSnapshot,
    onStatus: function () {},
    onAccepted: function () { return { accepted: true }; },
    onNotify: function () {}
  });
}

function snapshot() {
  return {
    kind: "ok",
    snapshot: { serverTimeUtc: "2026-10-13T10:00:00Z", orders: [], todayOrderCount: 0, cancelledOrderCount: 0 }
  };
}

test("the snapshot coordinator's polling interval can be lowered and raised while it runs", async function () {
  const c = clock();
  let fetches = 0;
  const coordinator = coordinatorWith(c, function () { fetches += 1; return Promise.resolve(snapshot()); });

  await coordinator.start();
  await flush();
  assert.equal(fetches, 1);

  // Connected: 30 seconds; the poll already scheduled at 10 s still runs, later ones wait 30 s.
  coordinator.setIntervalMs(30000);
  assert.equal(coordinator.intervalMs(), 30000);
  c.advance(10000);
  await flush();
  assert.equal(fetches, 2);
  c.advance(20000);
  await flush();
  assert.equal(fetches, 2);
  c.advance(10000);
  await flush();
  assert.equal(fetches, 3);

  // Disconnected: back to 10 seconds, rescheduled at once rather than after the pending 30 s.
  coordinator.setIntervalMs(10000);
  c.advance(10000);
  await flush();
  assert.equal(fetches, 4);
});
