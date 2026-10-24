// WAS-5: the Live Screen with a large active-order set (about 5,755 orders, ~5 MB of snapshot) and invalidation bursts
// that arrive while a snapshot is still being fetched, reconciled or rendered.
const test = require("node:test");
const assert = require("node:assert/strict");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

async function settle(times) {
  for (let i = 0; i < (times || 5); i++) await flush();
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise(function (res, rej) { resolve = res; reject = rej; });
  return { promise: promise, resolve: resolve, reject: reject };
}

function clock() {
  let seq = 1;
  const timers = new Map();
  let now = 0;
  return {
    setTimeout: function (fn, ms) {
      const id = seq++;
      timers.set(id, { fn: fn, at: now + ms });
      return id;
    },
    clearTimeout: function (id) { timers.delete(id); },
    pending: function () { return timers.size; },
    now: function () { return now; },
    advance: async function (ms) {
      now += ms;
      const due = Array.from(timers.entries())
        .filter(function (entry) { return entry[1].at <= now; })
        .sort(function (a, b) { return a[1].at - b[1].at; });
      for (const entry of due) {
        if (!timers.has(entry[0])) continue;
        timers.delete(entry[0]);
        entry[1].fn();
        await settle();
      }
    }
  };
}

function guid(n) {
  return "00000000-0000-4000-8000-" + String(n).padStart(12, "0");
}

function largeSnapshot(count, version, patch) {
  const orders = [];
  for (let i = 0; i < count; i++) {
    orders.push({
      id: guid(i + 1),
      displayNumber: "YS-" + (900000 + i),
      platform: i % 3 === 0 ? "Yemeksepeti" : (i % 3 === 1 ? "TrendyolYemek" : "GetirYemek"),
      status: i % 13 === 5 ? "Accepted" : "New",
      receivedAtUtc: new Date(Date.UTC(2026, 9, 14, 12, 0, 0) - i * 105000).toISOString(),
      deliveredAtUtc: null,
      customerName: "Zeynep Kaya",
      customerAddress: "Caferağa Mh. Moda Cd. No: 12, Kadıköy / İstanbul",
      customerNote: i % 3 === 0 ? "Please ring the bell twice." : null,
      totalAmount: 412.5,
      changeVersion: version,
      items: [
        { productName: "Adana Kebap", quantity: 2, notes: "Extra spicy" },
        { productName: "Ayran", quantity: 2, notes: null }
      ]
    });
  }
  return Object.assign({
    serverTimeUtc: "2026-10-14T12:00:00Z",
    todayOrderCount: 53,
    cancelledOrderCount: 0,
    orders: orders
  }, patch || {});
}

/**
 * A coordinator whose fetch and render both wait until the test lets them finish, so invalidations can arrive while
 * either is in progress. Every fetch answers with the server's newest version at the time it is answered.
 */
function slowHarness() {
  const time = clock();
  const fetches = [];
  const renders = [];
  const accepted = [];
  let inFlight = 0;
  let maxInFlight = 0;
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 30000,
    timeoutMs: 15000,
    maxBackoffMs: 30000,
    setTimeout: time.setTimeout,
    clearTimeout: time.clearTimeout,
    now: time.now,
    createAbortController: function () {
      return {
        signal: {},
        abort: function () {
          const pending = fetches.at(-1);
          pending.aborted = true;
          const error = new Error("aborted");
          error.name = "AbortError";
          pending.reject(error);
        }
      };
    },
    fetchSnapshot: function () {
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
      const pending = deferred();
      fetches.push(pending);
      return pending.promise.finally(function () { inFlight -= 1; });
    },
    onStatus: function () {},
    onAccepted: function (snapshot) {
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
      const pending = deferred();
      renders.push(pending);
      return pending.promise.then(function (value) {
        accepted.push(snapshot.orders[0].changeVersion);
        return value;
      }).finally(function () { inFlight -= 1; });
    }
  });
  return {
    time: time,
    fetches: fetches,
    renders: renders,
    accepted: accepted,
    coordinator: coordinator,
    maxInFlight: function () { return maxInFlight; }
  };
}

test("a burst during a slow fetch runs one trailing refresh and converges on the newest snapshot", async function () {
  const h = slowHarness();
  h.coordinator.start();
  await settle();
  assert.equal(h.fetches.length, 1);

  for (let i = 0; i < 200; i++) h.coordinator.requestRefresh();
  await settle();
  assert.equal(h.fetches.length, 1, "invalidations during a fetch never start a second request");

  h.fetches[0].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 1) });
  await settle();
  h.renders[0].resolve();
  await settle();
  assert.equal(h.fetches.length, 2, "exactly one trailing refresh");

  h.fetches[1].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 2) });
  await settle();
  h.renders[1].resolve();
  await settle();

  assert.equal(h.fetches.length, 2, "no further refresh until the next invalidation or poll");
  assert.deepEqual(h.accepted, [1, 2]);
  assert.equal(h.coordinator.lastSnapshot().orders[0].changeVersion, 2);
  assert.equal(h.maxInFlight(), 1, "one request or reconciliation at a time");
  assert.equal(h.time.pending(), 1, "only the next poll is scheduled; no timeout or retry timer leaks");
  assert.equal(h.fetches.some(function (f) { return f.aborted; }), false, "no invalidation aborts work in progress");
});

test("a burst during a slow render waits for it, then reads once more and shows the newest snapshot", async function () {
  const h = slowHarness();
  h.coordinator.start();
  await settle();
  h.fetches[0].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 1) });
  await settle();
  assert.equal(h.renders.length, 1);

  // Bursts keep arriving while the large snapshot is still being rendered.
  for (let round = 0; round < 5; round++) {
    for (let i = 0; i < 40; i++) h.coordinator.requestRefresh();
    await settle();
  }
  assert.equal(h.fetches.length, 1, "no request starts while the render is running");

  h.renders[0].resolve();
  await settle();
  assert.equal(h.fetches.length, 2, "one trailing refresh for the whole burst");

  // More invalidations during the trailing refresh: still one more read, not one per invalidation.
  for (let i = 0; i < 50; i++) h.coordinator.requestRefresh();
  h.fetches[1].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 2) });
  await settle();
  h.renders[1].resolve();
  await settle();
  assert.equal(h.fetches.length, 3);
  h.fetches[2].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 3) });
  await settle();
  h.renders[2].resolve();
  await settle();

  assert.equal(h.fetches.length, 3, "the burst is over: no endless restart loop");
  assert.deepEqual(h.accepted, [1, 2, 3]);
  assert.equal(h.coordinator.lastSnapshot().orders[0].changeVersion, 3);
  assert.equal(h.maxInFlight(), 1);
  assert.equal(h.time.pending(), 1);
});

test("a failed large refresh keeps the last usable snapshot and recovers on the next read", async function () {
  const h = slowHarness();
  h.coordinator.start();
  await settle();
  h.fetches[0].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 1) });
  await settle();
  h.renders[0].resolve();
  await settle();

  h.coordinator.requestRefresh();
  await settle();
  h.fetches[1].reject(new Error("network"));
  await settle();
  assert.equal(h.coordinator.lastSnapshot().orders.length, 5755, "the screen keeps the last accepted snapshot");
  assert.equal(h.coordinator.lastSnapshot().orders[0].changeVersion, 1);
  assert.equal(h.time.pending(), 1, "one backoff retry is scheduled");

  await h.time.advance(2000);
  assert.equal(h.fetches.length, 3);
  h.fetches[2].resolve({ kind: "ok", snapshot: largeSnapshot(5755, 2) });
  await settle();
  assert.equal(h.renders.length, 2, "the failed read rendered nothing");
  h.renders[1].resolve();
  await settle();
  assert.deepEqual(h.accepted, [1, 2]);
});

test("a large snapshot is a valid snapshot: nothing is dropped by validation or classification", function () {
  const body = largeSnapshot(5755, 1);
  const text = JSON.stringify(body);
  assert.ok(text.length > 2000000, "a multi-megabyte payload");
  const classified = store.classifyResponse(
    { ok: true, status: 200, redirected: false, headers: { get: function () { return "application/json; charset=utf-8"; } } },
    text,
    JSON.parse);
  assert.equal(classified.kind, "ok");
  assert.equal(classified.snapshot.orders.length, 5755);
  assert.equal(new Set(classified.snapshot.orders.map(function (o) { return o.id; })).size, 5755);
});

test("a timeout that fires late because the page was busy does not discard the response", async function () {
  const h = slowHarness();
  h.coordinator.start();
  await settle();
  h.fetches[0].resolve({ kind: "ok", snapshot: largeSnapshot(20, 1) });
  await settle();
  // The trailing read starts while the first large snapshot is still being rendered.
  h.coordinator.requestRefresh();
  h.renders[0].resolve();
  await settle();
  assert.equal(h.fetches.length, 2);

  // A long task kept the page busy for 60 s: the 15 s timeout runs 45 s late, next to the response that already arrived.
  await h.time.advance(60000);
  assert.equal(h.fetches[1].aborted, undefined, "a timer delayed by the page is not a network timeout");
  h.fetches[1].resolve({ kind: "ok", snapshot: largeSnapshot(20, 2) });
  await settle();
  h.renders[1].resolve();
  await settle();
  assert.deepEqual(h.accepted, [1, 2]);
  assert.equal(h.time.pending(), 1, "the extended timeout is cleared with the request");
});

test("a request the network really keeps beyond the timeout is still aborted and retried", async function () {
  const h = slowHarness();
  h.coordinator.start();
  await settle();
  await h.time.advance(15000);
  assert.equal(h.fetches[0].aborted, true);
  await settle();
  assert.equal(h.time.pending(), 1, "one backoff retry");
  await h.time.advance(2000);
  assert.equal(h.fetches.length, 2);

  // Delayed once by a busy page, then still not answered: aborted after the extension, not extended again.
  await h.time.advance(40000);
  assert.equal(h.fetches[1].aborted, undefined);
  await h.time.advance(15000);
  assert.equal(h.fetches[1].aborted, true);
});
