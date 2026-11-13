// WAS-204: the Live Screen's invalidation (orders-live-realtime.js) driving the real snapshot coordinator
// (orders-live-store.js) against a fake /orders/live-data on a manual clock, so every interleaving is chosen by the test:
// notifications before a read, during one, around its answer, and a snapshot endpoint that stops answering. Each read
// records why it started, as LiveScreenRealtimeBrowserTests does in a real browser: a notification (or a reconnect) asked
// for it, or it is the coordinator's own safety net (a poll, or a retry after a failed read).
const test = require("node:test");
const assert = require("node:assert/strict");
const invalidation = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-realtime.js");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");

const CONNECTED_POLL_MS = 30000;
const TIMEOUT_MS = 15000;

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

function orderId(n) {
  return "8a1f0c6e-0000-4000-8000-" + String(n).padStart(12, "0");
}

// A manual clock whose advance lets promise chains settle after every timer, as the browser's event loop does.
function clock() {
  let now = 0;
  let seq = 1;
  const timers = new Map();
  return {
    now: function () { return now; },
    setTimeout: function (fn, ms) { const id = seq++; timers.set(id, { fn: fn, at: now + Math.max(0, ms || 0) }); return id; },
    clearTimeout: function (id) { timers.delete(id); },
    advance: async function (ms) {
      const until = now + ms;
      await flush();
      for (;;) {
        let next = null;
        for (const [id, t] of timers) {
          if (t.at <= until && (!next || t.at < next[1].at || (t.at === next[1].at && id < next[0]))) next = [id, t];
        }
        if (!next) break;
        timers.delete(next[0]);
        now = next[1].at;
        next[1].fn();
        await flush();
      }
      now = until;
      await flush();
    }
  };
}

/**
 * A connected Live Screen: invalidation + coordinator against a fake server. The server reads its orders when a request
 * is handled (at once, or when a stall ends) and answers latencyMs later; a request the page aborts is never answered.
 */
async function liveScreen(options) {
  const c = clock();
  const server = { orders: new Map(), latencyMs: (options && options.latencyMs) || 50, stalledUntil: -1 };
  const reads = [];
  let announcements = 0;
  let sync = null;

  function snapshotOf(orders) {
    return {
      kind: "ok",
      snapshot: {
        serverTimeUtc: "2026-11-13T10:00:00Z",
        todayOrderCount: orders.length,
        cancelledOrderCount: 0,
        orders: orders.map(function (o) {
          return {
            id: o.id, displayNumber: o.id.slice(-4), platform: "Trendyol", status: "New",
            receivedAtUtc: "2026-11-13T09:59:00Z", customerName: "", totalAmount: 1, changeVersion: o.version, items: []
          };
        })
      }
    };
  }

  const coordinator = store.createRefreshCoordinator({
    intervalMs: CONNECTED_POLL_MS,
    timeoutMs: TIMEOUT_MS,
    now: c.now,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    createAbortController: function () {
      const listeners = [];
      const signal = { aborted: false, onAbort: function (fn) { listeners.push(fn); } };
      return { signal: signal, abort: function () { if (!signal.aborted) { signal.aborted = true; listeners.forEach(function (fn) { fn(); }); } } };
    },
    fetchSnapshot: function (signal) {
      const stats = sync ? sync.stats() : { reads: 0, connectReads: 0 };
      const read = { start: c.now(), end: null, result: "pending", requested: stats.reads, connects: stats.connectReads };
      reads.push(read);
      return new Promise(function (resolve, reject) {
        signal.onAbort(function () {
          if (read.result !== "pending") return;
          read.end = c.now();
          read.result = "aborted";
          reject(new Error("aborted"));
        });
        c.setTimeout(function handle() {
          if (read.result !== "pending") return;
          if (c.now() < server.stalledUntil) { c.setTimeout(handle, server.stalledUntil - c.now()); return; }
          const answer = snapshotOf(Array.from(server.orders, function (e) { return { id: e[0], version: e[1] }; }));
          c.setTimeout(function () {
            if (read.result !== "pending") return;
            read.end = c.now();
            read.result = "ok";
            resolve(answer);
          }, server.latencyMs);
        }, 0);
      });
    },
    onStatus: function () {},
    onAccepted: function () { return { accepted: true }; },
    onNotify: function (_snapshot, meta) { if (meta.newIds.length) announcements += 1; }
  });

  sync = invalidation.createLiveInvalidation({
    requestRefresh: function () { return coordinator.requestRefresh(); },
    setPollingIntervalMs: function (ms) { coordinator.setIntervalMs(ms); },
    shownVersion: function (id) {
      const snapshot = coordinator.lastSnapshot();
      const order = snapshot && snapshot.orders.find(function (o) { return o.id === id; });
      return order ? order.changeVersion : -1;
    },
    disconnectedPollingMs: 10000,
    connectedPollingMs: CONNECTED_POLL_MS,
    setTimeout: c.setTimeout,
    clearTimeout: c.clearTimeout,
    now: c.now
  });

  server.orders.set(orderId(900), 1); // already on screen before the test starts
  sync.onConnectionState({ state: "connected", reconnected: false });
  await c.advance(server.latencyMs + 1000);
  assert.equal(reads.length, 1, "the connect read");
  assert.equal(coordinator.isBaselineReady(), true);

  return {
    clock: c,
    server: server,
    reads: reads,
    sync: sync,
    announcements: function () { return announcements; },
    store: function (n) { server.orders.set(orderId(n), (server.orders.get(orderId(n)) || 0) + 1); return orderId(n); },
    notify: function (n) { sync.onNotification({ type: "OrderChanged", orderId: orderId(n), version: server.orders.get(orderId(n)) }); },
    shown: function (n) {
      const snapshot = coordinator.lastSnapshot();
      return !!snapshot && snapshot.orders.some(function (o) { return o.id === orderId(n) && o.changeVersion === server.orders.get(orderId(n)); });
    },
    /** Why each read after `from` started, as the browser test classifies it from the same counters. */
    causes: function (from) {
      return reads.slice(from).map(function (read, i) {
        const previous = reads[from + i - 1];
        if (read.connects > previous.connects) return "connect";
        if (read.requested > previous.requested) return "notification";
        return "safety-net";
      });
    }
  };
}

function inFlightAtOnce(reads) {
  let most = 0;
  for (const read of reads) {
    const overlapping = reads.filter(function (other) {
      return other.start <= read.start && (other.end === null || other.end > read.start);
    }).length;
    most = Math.max(most, overlapping);
  }
  return most;
}

test("a burst that arrives before any read is read once and announced once", async function () {
  const live = await liveScreen({ latencyMs: 50 });
  const from = live.reads.length;
  for (let n = 1; n <= 5; n++) live.store(n);
  for (let n = 1; n <= 5; n++) live.notify(n);
  await live.clock.advance(5000);

  assert.deepEqual(live.causes(from).slice(1), []);
  assert.equal(live.reads.length - from, 1);
  for (let n = 1; n <= 5; n++) assert.ok(live.shown(n), "order " + n);
  assert.equal(live.announcements(), 1);
  assert.equal(live.sync.stats().coalesced, 4);
});

test("notifications during a slow read become one follow-up, never a second request at the same time", async function () {
  const live = await liveScreen({ latencyMs: 2000 });
  const from = live.reads.length;
  for (let n = 1; n <= 5; n++) live.store(n);
  live.notify(1);
  await live.clock.advance(300); // the first read is under way
  assert.equal(live.reads.length - from, 1);
  for (let n = 2; n <= 5; n++) live.notify(n);
  await live.clock.advance(10000);

  // The read under way already had all five; the notifications that arrived during it still get one read after it.
  assert.equal(live.reads.length - from, 2);
  assert.deepEqual(live.causes(from).slice(1), ["notification"]);
  assert.equal(inFlightAtOnce(live.reads), 1);
  assert.equal(live.announcements(), 1);
});

test("a change stored after the read under way was handled is not dropped: the follow-up shows it", async function () {
  const live = await liveScreen({ latencyMs: 2000 });
  const from = live.reads.length;
  live.store(1);
  live.notify(1);
  await live.clock.advance(500); // the server has already read its orders for this request
  live.store(2);
  live.notify(2);
  await live.clock.advance(2000); // the first answer arrives without order 2
  assert.ok(live.shown(1));
  assert.equal(live.shown(2), false);
  await live.clock.advance(3000);

  assert.ok(live.shown(2), "the trailing change was read");
  assert.equal(live.reads.length - from, 2);
  assert.equal(live.announcements(), 2);
});

test("around a read's answer: a change just before it is read again, one the answer already showed is skipped", async function () {
  const live = await liveScreen({ latencyMs: 1000 });
  const from = live.reads.length;
  live.store(1);
  live.notify(1);
  await live.clock.advance(1100); // the read started 150 ms after the notification and answers 1 s later
  live.store(2);
  live.notify(2); // 50 ms before that answer, which the server made without order 2
  await live.clock.advance(100);
  assert.equal(live.shown(2), false);
  live.notify(1); // after the answer, for a version it shows
  await live.clock.advance(5000);

  assert.ok(live.shown(2));
  assert.equal(live.reads.length - from, 2);
  assert.equal(live.sync.stats().skipped, 1);
  assert.equal(inFlightAtOnce(live.reads), 1);
});

test("while the snapshot endpoint does not answer, retries add reads that no notification caused", async function () {
  // The CI failure (run 42497490920): no test result for 150 s while the burst test ran. A server that does not answer
  // for 150 s from the burst's read: the read times out after 15 s and the coordinator retries with its backoff
  // (2, 4, 8, 16, 30 s). Six reads, one of them asked for by the burst; the burst is still announced once.
  const live = await liveScreen({ latencyMs: 50 });
  const from = live.reads.length;
  for (let n = 1; n <= 5; n++) live.store(n);
  live.server.stalledUntil = live.clock.now() + 150000;
  for (let n = 1; n <= 5; n++) live.notify(n);
  await live.clock.advance(151000);

  assert.equal(live.reads.length - from, 6);
  assert.deepEqual(live.causes(from), ["notification", "safety-net", "safety-net", "safety-net", "safety-net", "safety-net"]);
  assert.deepEqual(live.reads.slice(from).map(function (r) { return r.result; }), ["aborted", "aborted", "aborted", "aborted", "aborted", "ok"]);
  assert.equal(live.sync.stats().reads - 1, 1, "one notification-triggered read (the connect read is the other)");
  assert.equal(inFlightAtOnce(live.reads), 1);
  for (let n = 1; n <= 5; n++) assert.ok(live.shown(n), "order " + n);
  assert.equal(live.announcements(), 1);
});

test("with no notification at all, the same stall makes the same number of reads", async function () {
  const live = await liveScreen({ latencyMs: 50 });
  await live.clock.advance(CONNECTED_POLL_MS - 2000); // the next poll is due in about a second
  const from = live.reads.length;
  live.server.stalledUntil = live.clock.now() + 150000;
  await live.clock.advance(151000);

  assert.equal(live.reads.length - from, 6);
  assert.deepEqual(live.causes(from), ["safety-net", "safety-net", "safety-net", "safety-net", "safety-net", "safety-net"]);
  assert.equal(live.sync.stats().notifications, 0);
});
