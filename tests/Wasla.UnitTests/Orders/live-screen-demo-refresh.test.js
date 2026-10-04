// The practice order's automatic stages as a continuously watching Live Screen sees them: the real refresh coordinator
// (orders-live-store.js), the real countdown (orders-demo-countdown.js) and the real order actions (orders-actions.js)
// against a simulated server whose Worker moves the practice order at its persisted deadline. Fake time throughout:
// nothing waits 20 seconds.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "..", "..", "..");
const js = (name) => path.join(root, "src", "Wasla.Web", "wwwroot", "js", "orders", name);
const countdown = require(js("orders-demo-countdown.js"));
const store = require(js("orders-live-store.js"));

// A small DOM ---------------------------------------------------------------------------------------------------

function matches(el, selector) {
  const m = selector.match(/^([a-z]*)((?:\[[^\]]+\])*)$/);
  if (!m) throw new Error("unsupported selector " + selector);
  if (m[1] && el.tagName !== m[1]) return false;
  return (m[2].match(/\[[^\]]+\]/g) || []).every((part) => {
    const a = part.slice(1, -1).match(/^([\w-]+)(?:="([^"]*)")?$/);
    const value = el.getAttribute(a[1]);
    return a[2] === undefined ? value !== null : value === a[2];
  });
}

function element(tagName, attrs = {}) {
  const attributes = new Map(Object.entries(attrs));
  const el = {
    tagName, children: [], parentNode: null, hidden: false, style: {}, textContent: "", title: "",
    get id() { return attributes.get("id") || ""; },
    set id(v) { attributes.set("id", v); },
    get className() { return attributes.get("class") || ""; },
    set className(v) { attributes.set("class", v); },
    getAttribute: (n) => (attributes.has(n) ? attributes.get(n) : null),
    setAttribute: (n, v) => { attributes.set(n, String(v)); },
    removeAttribute: (n) => { attributes.delete(n); },
    appendChild(child) { child.parentNode = el; el.children.push(child); return child; },
    insertBefore(child, ref) {
      child.parentNode = el;
      const at = ref ? el.children.indexOf(ref) : -1;
      if (at < 0) el.children.push(child); else el.children.splice(at, 0, child);
      return child;
    },
    removeChild(child) { el.children.splice(el.children.indexOf(child), 1); child.parentNode = null; return child; },
    get nextSibling() { const p = el.parentNode; return p ? p.children[p.children.indexOf(el) + 1] || null : null; },
    matches: (s) => matches(el, s),
    querySelectorAll: (s) => all(el).filter((c) => c !== el && matches(c, s)),
    querySelector: (s) => el.querySelectorAll(s)[0] || null,
    closest(s) { for (let n = el; n; n = n.parentNode) if (n.matches && n.matches(s)) return n; return null; }
  };
  return el;
}

function all(el) { return [el].concat(...el.children.map(all)); }

const MESSAGES = {
  demoCountdownPickUp: "pickup {0}", demoCountdownDeliver: "deliver {0}", demoCountdownLeave: "leave {0}",
  demoCountdownWaiting: "waiting", demoCountdownSeconds: "{0} s", demoCountdownLabel: "left", demoCountdownHint: "auto"
};

// Fake time: one clock for timeouts, intervals and the simulated server -----------------------------------------

function flush() { return new Promise((resolve) => setImmediate(resolve)); }

async function settle() { for (let i = 0; i < 8; i++) await flush(); }

function fakeTime(start) {
  let now = start;
  let seq = 0;
  const timers = new Map();
  const api = {
    now: () => now,
    setTimeout: (fn, ms) => { seq += 1; timers.set(seq, { fn, at: now + Math.max(0, ms || 0), every: 0 }); return seq; },
    clearTimeout: (id) => { timers.delete(id); },
    setInterval: (fn, ms) => { seq += 1; timers.set(seq, { fn, at: now + ms, every: ms }); return seq; },
    clearInterval: (id) => { timers.delete(id); },
    pending: () => timers.size,
    /** Runs every timer due up to now + ms in time order, letting promises settle after each. */
    async advance(ms) {
      const target = now + ms;
      for (;;) {
        let nextId = null;
        let next = null;
        for (const [id, timer] of timers) {
          if (timer.at <= target && (!next || timer.at < next.at || (timer.at === next.at && id < nextId))) { next = timer; nextId = id; }
        }
        if (!next) break;
        now = Math.max(now, next.at);
        if (next.every) next.at += next.every; else timers.delete(nextId);
        next.fn();
        await settle();
      }
      now = target;
      await settle();
    }
  };
  return api;
}

// The simulated server -------------------------------------------------------------------------------------------

const DEMO = "11111111-2222-4333-8444-555555555555";
const REAL = "22222222-2222-4333-8444-555555555555";
const STAGE_MS = 20000;
const START = Date.parse("2026-10-03T09:00:00Z");
const iso = (ms) => new Date(ms).toISOString();
const NEXT = { ReadyForPickup: "OnTheWay", OnTheWay: "Delivered" };
const ACTION = { ReadyForPickup: "PickUp", OnTheWay: "Deliver", Delivered: "Leave" };

/**
 * The persisted practice order plus Wasla.Worker's demo scheduler: it moves an automatic stage when its deadline (from
 * the stage's persisted start) has passed, `workerLagMs` late; a stopped Worker moves nothing. A delivered practice
 * order is in the snapshot only until its own deadline. The browser's clock may be skewed from the server's.
 */
function server(time, options) {
  const o = Object.assign({ workerLagMs: 50, latencyMs: 40, workerStopped: false, skewMs: 0 }, options || {});
  const state = { status: "Preparing", enteredAt: START, transitions: [], requests: 0, inFlight: 0, maxInFlight: 0, posts: 0 };
  const serverNow = () => time.now() + o.skewMs;

  function worker() {
    if (o.workerStopped || !NEXT[state.status]) return;
    const due = state.enteredAt + STAGE_MS;
    time.setTimeout(function () {
      if (o.workerStopped || serverNow() < due) return;
      state.status = NEXT[state.status];
      state.enteredAt = serverNow();
      state.transitions.push({ status: state.status, at: state.enteredAt, due });
      worker();
    }, due + o.workerLagMs - serverNow());
  }

  function snapshot() {
    const t = serverNow();
    const orders = [{ id: REAL, displayNumber: "TY-1", platform: "TrendyolYemek", status: "Preparing", receivedAtUtc: iso(START - 600000), deliveredAtUtc: null, customerName: "Ada", totalAmount: 10, items: [{ productName: "Lahmacun", quantity: 1, notes: null }] }];
    const visible = state.status !== "Delivered" || t < state.enteredAt + STAGE_MS;
    if (visible) {
      orders.push({
        id: DEMO, displayNumber: "DEMO", platform: "TrendyolYemek", status: state.status, isDemo: true,
        receivedAtUtc: iso(START), deliveredAtUtc: state.status === "Delivered" ? iso(state.enteredAt) : null,
        customerName: "Demo", totalAmount: 0, items: [{ productName: "Pizza", quantity: 1, notes: null }],
        demoAutomation: ACTION[state.status] ? { action: ACTION[state.status], dueAtUtc: iso(state.enteredAt + STAGE_MS), durationSeconds: 20 } : null
      });
    }
    return { serverTimeUtc: iso(t), todayOrderCount: 1, cancelledOrderCount: 0, training: null, orders };
  }

  return {
    state,
    options: o,
    serverNow,
    /** GET /orders/live-data: read-only; the response is built halfway through the round trip. */
    fetchSnapshot() {
      state.requests += 1;
      state.inFlight += 1;
      state.maxInFlight = Math.max(state.maxInFlight, state.inFlight);
      return new Promise((resolve) => {
        let body = null;
        time.setTimeout(() => { body = snapshot(); }, o.latencyMs / 2);
        time.setTimeout(() => { state.inFlight -= 1; resolve({ kind: "ok", snapshot: body }); }, o.latencyMs);
      });
    },
    /** POST /orders/demo/{id}/mark-ready, committed halfway through the round trip. */
    markReady() {
      state.posts += 1;
      return new Promise((resolve) => {
        time.setTimeout(() => { state.status = "ReadyForPickup"; state.enteredAt = serverNow(); worker(); }, o.latencyMs / 2);
        time.setTimeout(() => resolve({ ok: true }), o.latencyMs);
      });
    },
    stopWorker() { o.workerStopped = true; },
    startWorker() { o.workerStopped = false; worker(); }
  };
}

// One Live Screen tab: coordinator + countdown, rendering the practice order's card ---------------------------

function liveScreen(time, api) {
  const body = element("body");
  const dispatched = [];
  const doc = {
    body, createElement: (tag) => element(tag),
    querySelectorAll: (s) => body.querySelectorAll(s), querySelector: (s) => body.querySelector(s),
    dispatchEvent: (e) => { dispatched.push(e); return true; }
  };
  const card = element("article", { "data-order-id": DEMO, "data-wasla-demo-card": "true" });
  const head = element("div");
  head.appendChild(element("span", { "data-status-badge": "" }));
  card.appendChild(head);

  const metas = [];
  const notified = [];
  const shown = [];          // { status, seconds, at } the first time each stage is drawn
  const countdownCalls = { total: 0, overlapping: 0, pending: 0 };
  let lastStatus = null;

  const controller = countdown.createController({
    document: doc, now: time.now, setInterval: time.setInterval, clearInterval: time.clearInterval,
    setTimeout: time.setTimeout, clearTimeout: time.clearTimeout, message: (k) => MESSAGES[k],
    requestRefresh() {
      countdownCalls.total += 1;
      if (countdownCalls.pending > 0) countdownCalls.overlapping += 1;
      countdownCalls.pending += 1;
      return Promise.resolve(coordinator.requestRefresh()).finally(() => { countdownCalls.pending -= 1; });
    }
  });

  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000, timeoutMs: 15000,
    setTimeout: time.setTimeout, clearTimeout: time.clearTimeout,
    createAbortController: () => ({ abort() {}, signal: null }),
    fetchSnapshot: () => api.fetchSnapshot(),
    onStatus() {},
    onAccepted(snapshot, meta) {
      metas.push(meta);
      // What the Live Screen does with the practice order's card: follow the server's status, or remove it.
      const order = snapshot.orders.find((o) => o.id === DEMO);
      if (!order) { if (card.parentNode) body.removeChild(card); lastStatus = null; }
      else { if (!card.parentNode) body.appendChild(card); card.setAttribute("data-order-status", order.status); }
      controller.update({ orders: snapshot.orders, serverTimeUtc: snapshot.serverTimeUtc });
      if (order && order.status !== lastStatus) {
        lastStatus = order.status;
        const v = view();
        shown.push({ status: order.status, seconds: v ? Number(v.now) : null, text: v ? v.text : null, at: api.serverNow() });
      }
    },
    onNotify(snapshot, meta) { notified.push(meta); }
  });

  function view() {
    const box = card.querySelector("[data-demo-countdown]");
    if (!box) return null;
    const bar = box.querySelector("[data-demo-countdown-bar]");
    return { text: box.querySelector("[data-demo-countdown-text]").textContent, now: bar.getAttribute("aria-valuenow"), fill: box.querySelector("[data-demo-countdown-fill]").style.inlineSize };
  }

  return {
    body, card, controller, coordinator, metas, notified, shown, dispatched, countdownCalls, view,
    first: (status) => shown.find((s) => s.status === status),
    onScreen: () => !!card.parentNode,
    /** The Mark ready button as orders-actions.js runs it on the Live Screen: mutation guard, POST, then release. The
     *  simulated POST completes as fake time advances, so callers do not await it. */
    async markReady() {
      const endMutation = coordinator.beginMutation();
      try { await api.markReady(); } finally { endMutation(); }
    }
  };
}

async function watchedTraining(options) {
  const time = fakeTime(START);
  const api = server(time, options);
  const tab = liveScreen(time, api);
  tab.coordinator.start();
  await time.advance(3000);
  return { time, api, tab };
}

// Continuous watching -------------------------------------------------------------------------------------------

test("a successful Mark ready asks for one read-only snapshot at once: Ready appears at 19-20 seconds", async () => {
  const { time, api, tab } = await watchedTraining();
  const requestsBefore = api.state.requests;
  tab.markReady();
  await time.advance(2 * api.options.latencyMs); // the POST and then the snapshot's own round trip, far less than the 10-second poll

  assert.equal(api.state.requests, requestsBefore + 1, "exactly one immediate refresh");
  const ready = tab.first("ReadyForPickup");
  assert.ok(ready, "the Ready stage is on screen without waiting for the poll");
  assert.ok(ready.seconds >= 19 && ready.seconds <= 20, "Ready first shows " + ready.seconds);
  assert.ok(ready.at - api.state.enteredAt <= 100, "drawn within the round trip after the POST committed");
  assert.equal(api.state.posts, 1, "the only POST is the user's own action");
});

test("a continuously watched practice order starts OnTheWay and Delivered near 20 seconds and waits only briefly at zero", async () => {
  const { time, api, tab } = await watchedTraining();
  tab.markReady();
  // Another refresh (e.g. a second action) moves the 10-second poll off the stage deadlines, so only the countdown's
  // own deadline refresh can show the Worker's change on time.
  await time.advance(4300);
  tab.coordinator.requestRefresh();
  await time.advance(70000);
  assert.ok(tab.countdownCalls.total >= 3, "the deadline refreshes did the work, not a lucky poll");

  const [pickedUp, delivered] = api.state.transitions;
  assert.equal(pickedUp.status, "OnTheWay");
  assert.equal(delivered.status, "Delivered");
  for (const [status, transition] of [["OnTheWay", pickedUp], ["Delivered", delivered]]) {
    const first = tab.first(status);
    assert.ok(first.seconds >= 18 && first.seconds <= 20, status + " first shows " + first.seconds);
    assert.ok(first.at - transition.due <= 1000, status + " is visible " + (first.at - transition.due) + " ms after the deadline");
  }
  // The delivered practice order leaves the screen right after its own deadline.
  assert.equal(tab.onScreen(), false);
  assert.equal(api.state.maxInFlight, 1, "never two snapshot requests at once");
  assert.equal(tab.countdownCalls.overlapping, 0);
  // Ordinary polls (one per 10 s) plus about one deadline refresh per stage: bounded.
  assert.ok(api.state.requests <= 12, "requests " + api.state.requests);
});

test("deadline refreshes are read-only snapshots coalesced with the store: no POST, no overlap, no new-order effect", async () => {
  const { time, api, tab } = await watchedTraining({ latencyMs: 1500 });
  tab.markReady();
  await time.advance(70000);

  assert.equal(api.state.posts, 1, "only the user's own Mark ready");
  assert.equal(api.state.maxInFlight, 1, "a slow response never overlaps the next refresh");
  assert.equal(tab.countdownCalls.overlapping, 0, "the countdown waits for each refresh before the next");
  assert.ok(tab.countdownCalls.total >= 3, "it did refresh at the deadlines");
  // The first snapshot is the only baseline; nothing after it is "new": no sound, notification or highlight.
  assert.equal(tab.metas[0].isBaseline, true);
  assert.ok(tab.metas.slice(1).every((meta) => meta.isBaseline === false && meta.newIds.length === 0));
  assert.deepEqual(tab.notified, []);
  assert.deepEqual(tab.dispatched, [], "the countdown raises no event of its own");
  assert.equal(tab.coordinator.isBaselineReady(), true);
  const source = fs.readFileSync(js("orders-live-store.js"), "utf8");
  const fetchSnapshot = source.slice(source.indexOf("async function fetchSnapshot"), source.indexOf("async function onAccepted"));
  assert.match(fetchSnapshot, /fetch\(O\.opts\.liveDataUrl \|\| "\/orders\/live-data"/);
  assert.equal(/method\s*:/.test(fetchSnapshot), false, "the snapshot request is a plain GET");
});

test("a late Worker is seen within a second of its change, and the new stage still starts near 20", async () => {
  const { time, api, tab } = await watchedTraining({ workerLagMs: 2500 });
  tab.markReady();
  await time.advance(80000);

  for (const transition of api.state.transitions) {
    const first = tab.first(transition.status);
    assert.ok(first.at - transition.at <= 1100, transition.status + " seen " + (first.at - transition.at) + " ms after the Worker moved it");
    assert.ok(first.seconds >= 18, transition.status + " first shows " + first.seconds);
  }
  assert.equal(api.state.maxInFlight, 1);
});

test("the browser's clock offset changes nothing: the next stage still appears near 20", async () => {
  for (const skewMs of [-3600000, 90000]) {
    const { time, api, tab } = await watchedTraining({ skewMs });
    tab.markReady();
    await time.advance(45000);
    const first = tab.first("OnTheWay");
    assert.ok(first && first.seconds >= 18, "skew " + skewMs + ": " + (first && first.seconds));
  }
});

// Reload and stale data -------------------------------------------------------------------------------------------

test("reloading halfway through a stage shows the real remaining time, including 10, never a fresh 20", async () => {
  const { time, api } = await watchedTraining();
  const first = liveScreen(time, api);
  first.markReady();
  await time.advance(10000);
  first.controller.dispose();

  // A reload exactly ten seconds into the Ready stage: a new page, coordinator and countdown.
  const reloaded = liveScreen(time, api);
  reloaded.coordinator.start();
  await settle(); // the request is issued on a microtask
  await time.advance(api.options.latencyMs);
  assert.equal(reloaded.first("ReadyForPickup").seconds, 10);
  assert.equal(reloaded.view().fill, "50.00%");

  // The same stage arriving again (an ordinary poll) never restarts it.
  await time.advance(5000);
  reloaded.coordinator.requestRefresh();
  await settle();
  await time.advance(api.options.latencyMs);
  assert.equal(reloaded.view().now, "5");
});

test("while the Worker has not moved it yet, repeated snapshots keep the stage at zero instead of resetting it", async () => {
  const { time, api, tab } = await watchedTraining({ workerLagMs: 4000 });
  tab.markReady();
  await time.advance(STAGE_MS + 2000);
  assert.equal(api.state.status, "ReadyForPickup");
  assert.equal(tab.view().text, "waiting");
  assert.equal(tab.view().now, "0");
  assert.equal(tab.card.getAttribute("data-order-status"), "ReadyForPickup", "no fake status in the browser");
});

// Bounded retries, a stopped Worker, hidden and disposed pages --------------------------------------------------

test("a stopped Worker leaves the honest waiting state: refreshes stop at their ceiling, the ordinary poll goes on", async () => {
  const { time, api, tab } = await watchedTraining();
  api.stopWorker();
  tab.markReady();
  await time.advance(STAGE_MS + 15000);

  assert.equal(api.state.status, "ReadyForPickup");
  assert.equal(tab.view().text, "waiting");
  assert.equal(tab.card.getAttribute("data-order-status"), "ReadyForPickup");
  assert.equal(tab.countdownCalls.total, countdown.MAX_DEADLINE_REFRESHES, "bounded deadline refreshes");
  assert.equal(tab.controller.isRefreshPending(), false, "no retry timer left");
  assert.equal(tab.controller.isTicking(), false);

  const before = api.state.requests;
  await time.advance(60000);
  assert.equal(api.state.requests - before, 6, "only the ordinary 10-second poll");
  assert.equal(tab.countdownCalls.total, countdown.MAX_DEADLINE_REFRESHES);
  assert.equal(tab.view().text, "waiting");

  // The Worker comes back: the next ordinary poll shows the move, and that new stage has its own refreshes again.
  api.startWorker();
  await time.advance(11000);
  assert.equal(api.state.status, "OnTheWay");
  assert.equal(tab.card.getAttribute("data-order-status"), "OnTheWay");
});

test("a slow deadline refresh is never doubled, even if the tab is shown again while it runs", async () => {
  const { time, api, tab } = await watchedTraining({ latencyMs: 3000 });
  api.stopWorker();
  tab.markReady();
  await time.advance(STAGE_MS + 3000 + 500); // past the deadline: the first refresh is on its way
  assert.equal(tab.countdownCalls.pending, 1);
  tab.controller.setHidden(false); // a visibilitychange while it is still running
  await time.advance(1500);
  assert.equal(tab.countdownCalls.overlapping, 0, "the next refresh waits for this one");
  assert.equal(tab.countdownCalls.total, 1);
  assert.equal(api.state.maxInFlight, 1);
  await time.advance(10000);
  assert.equal(tab.countdownCalls.overlapping, 0);
  assert.ok(tab.countdownCalls.total >= 2, "and then goes on, one at a time");
});

test("boot wiring: a tab opened in the background refreshes nothing until shown, and uses only the store's refresh", async () => {
  const time = fakeTime(START);
  const listeners = {};
  const body = element("body");
  const doc = {
    hidden: true, body, createElement: (tag) => element(tag),
    querySelectorAll: (s) => body.querySelectorAll(s), querySelector: (s) => body.querySelector(s),
    addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
    fire: (type, detail) => (listeners[type] || []).forEach((fn) => fn({ type, detail }))
  };
  const win = { setTimeout: time.setTimeout, clearTimeout: time.clearTimeout, setInterval: time.setInterval, clearInterval: time.clearInterval, addEventListener: (type, fn) => doc.addEventListener("win:" + type, fn) };
  let refreshes = 0;
  const O = { getMessage: (k) => MESSAGES[k], liveStore: { requestRefresh: () => { refreshes += 1; return Promise.resolve({ applied: true }); } } };
  const card = element("article", { "data-order-id": DEMO, "data-wasla-demo-card": "true", "data-order-status": "ReadyForPickup" });
  const head = element("div");
  head.appendChild(element("span", { "data-status-badge": "" }));
  card.appendChild(head);
  body.appendChild(card);

  const controller = countdown.boot(doc, win, O);
  // boot() measures with the real Date.now(): this stage's deadline passed a second ago on the server's clock.
  const serverNow = Date.now();
  doc.fire("wasla:live-rendered", { serverTimeUtc: iso(serverNow), orders: [{ id: DEMO, status: "ReadyForPickup", isDemo: true, demoAutomation: { action: "PickUp", dueAtUtc: iso(serverNow - 1000), durationSeconds: 20 } }] });
  await time.advance(5000);
  assert.equal(refreshes, 0, "nothing while the tab has never been visible");
  assert.equal(controller.isTicking(), false);

  doc.hidden = false;
  doc.fire("visibilitychange");
  await time.advance(100);
  assert.equal(refreshes, 1, "one check once it is shown");
  doc.fire("win:pagehide");
  await time.advance(20000);
  assert.equal(refreshes, 1, "nothing after pagehide");
});

test("a hidden or disposed page makes no countdown refresh; becoming visible again checks once", async () => {
  const { time, api, tab } = await watchedTraining();
  api.stopWorker();
  tab.markReady();
  await time.advance(STAGE_MS - 1000);
  tab.controller.setHidden(true);
  await time.advance(5000);
  assert.equal(tab.countdownCalls.total, 0, "nothing while hidden");
  assert.equal(tab.controller.isRefreshPending(), false);
  assert.equal(tab.controller.isTicking(), false);

  tab.controller.setHidden(false);
  await time.advance(100);
  assert.equal(tab.countdownCalls.total, 1, "one check when the tab is visible again");

  tab.controller.dispose();
  const calls = tab.countdownCalls.total;
  await time.advance(20000);
  assert.equal(tab.countdownCalls.total, calls);
  assert.equal(tab.controller.isRefreshPending(), false);
});

test("deadline refreshes end once the practice order is gone or guided setup has ended", async () => {
  const { time, api, tab } = await watchedTraining();
  tab.markReady();
  await time.advance(70000);
  assert.equal(tab.onScreen(), false);
  const calls = tab.countdownCalls.total;
  assert.equal(tab.controller.size(), 0);
  assert.equal(tab.controller.isRefreshPending(), false);
  await time.advance(60000);
  assert.equal(tab.countdownCalls.total, calls, "no background retries once the practice order has left");
});

test("a refresh while the user's own action is pending waits; it is never sent alongside it", async () => {
  const { time, api, tab } = await watchedTraining({ workerLagMs: 3000 });
  tab.markReady();
  await time.advance(STAGE_MS);
  const end = tab.coordinator.beginMutation(); // e.g. another action button pressed at the deadline
  const before = api.state.requests;
  await time.advance(2000);
  assert.equal(api.state.requests, before, "no snapshot request during a pending action");
  end();
  await time.advance(3000);
  assert.equal(api.state.maxInFlight, 1);
  assert.equal(tab.card.getAttribute("data-order-status"), "OnTheWay");
});

// The real Mark ready button --------------------------------------------------------------------------------------

test("orders-actions.js: a successful practice Mark ready releases the Live Screen store at once, which re-reads with a GET", async () => {
  const requests = [];
  const listeners = {};
  const time = fakeTime(START);
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000, timeoutMs: 15000, setTimeout: time.setTimeout, clearTimeout: time.clearTimeout,
    createAbortController: () => ({ abort() {} }),
    fetchSnapshot: () => { requests.push("GET /orders/live-data"); return new Promise(() => {}); },
    onStatus() {}, onAccepted() {}
  });
  const doc = {
    addEventListener: (type, fn) => { listeners[type] = fn; },
    querySelector: () => ({ value: "token" }),
    dispatchEvent() { return true; }
  };
  const O = {
    opts: { pageMode: "liveDisplay" },
    table: { refreshOrdersTable() { requests.push("table refresh"); } },
    getMessage: (k) => k,
    isDebugEnabled: () => false,
    liveStore: { beginMutation: () => coordinator.beginMutation() }
  };
  const sandbox = {
    WaslaOrders: O, document: doc, CustomEvent: function (type, init) { this.type = type; this.detail = init && init.detail; },
    fetch: (url, init) => { requests.push(init.method + " " + url); return Promise.resolve({ ok: true, json: () => Promise.resolve({ message: "Orders.MarkReadySuccess" }) }); },
    confirm: () => { throw new Error("a practice action asks for no confirmation"); }
  };
  sandbox.window = sandbox;
  vm.runInNewContext(fs.readFileSync(js("orders-actions.js"), "utf8"), sandbox);
  listeners.DOMContentLoaded();

  const button = {
    disabled: false,
    attrs: { "data-order-id": DEMO, "data-order-action": "mark-ready", "data-wasla-demo": "true" },
    getAttribute(n) { return this.attrs[n] === undefined ? null : this.attrs[n]; },
    hasAttribute(n) { return this.attrs[n] !== undefined; },
    closest(s) { return s === "[data-order-action][data-order-id]" || s === "#ordersLiveScreenHost" ? this : null; }
  };
  listeners.click({ target: button, preventDefault() {} });
  await settle();

  assert.deepEqual(requests, ["POST /orders/demo/" + DEMO + "/mark-ready", "GET /orders/live-data"],
    "the store re-reads immediately, without waiting for the 10-second poll and without a table refresh");
});
