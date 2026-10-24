const test = require("node:test");
const assert = require("node:assert/strict");
const printStatus = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-print-status.js");

const ORDER = "8a1f0c6e-0000-4000-8000-000000000001";
const OTHER = "8a1f0c6e-0000-4000-8000-000000000002";

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

function clock() {
  let seq = 1;
  const timers = new Map();
  return {
    timers: timers,
    setTimeout: function (fn, ms) { const id = seq++; timers.set(id, { fn: fn, ms: ms }); return id; },
    clearTimeout: function (id) { timers.delete(id); },
    /** Runs the pending timers of one delay (the fallback interval), not request timeouts. */
    run: function (ms) {
      const due = [...timers.entries()].filter(function (e) { return e[1].ms === ms; });
      due.forEach(function (e) { timers.delete(e[0]); e[1].fn(); });
      return due.length;
    }
  };
}

function fakeView(shown) {
  const view = {
    shown: Object.assign({}, shown),
    applied: [],
    busy: {},
    generation: {},
    displayedOrderIds: function () { return Object.keys(view.shown); },
    shownVersion: function (id) { return id in view.shown ? view.shown[id] : -1; },
    apply: function (id, state) { view.applied.push([id, state.version, state.mode]); view.shown[id] = state.version; },
    isBusy: function (id) { return !!view.busy[id]; },
    submitGeneration: function (id) { return view.generation[id] || 0; }
  };
  return view;
}

// Each fetch waits until the test answers it, so overlapping and out-of-order answers can be arranged.
function fakeServer() {
  const server = { calls: [], pending: [] };
  server.fetchState = function (orderId) {
    server.calls.push(orderId);
    return new Promise(function (resolve) { server.pending.push({ orderId: orderId, resolve: resolve }); });
  };
  server.answer = function (version, mode, outcome) {
    const next = server.pending.shift();
    next.resolve({ orderId: next.orderId, version: version, mode: mode || "queued", outcome: outcome || "none" });
  };
  return server;
}

function sync(view, server, timers, hidden) {
  return printStatus.createPrintStatusSync({
    view: view,
    fetchState: server.fetchState,
    createAbortController: function () { return { signal: {}, abort: function () {} }; },
    setTimeout: timers.setTimeout,
    clearTimeout: timers.clearTimeout,
    isHidden: function () { return !!(hidden && hidden.value); }
  });
}

function notice(orderId, version) {
  return { type: "PrintJobChanged", orderId: orderId, printJobId: "job", status: "Printing", version: version };
}

test("a notification for an order that is not on screen fetches nothing", async function () {
  const server = fakeServer();
  const s = sync(fakeView({ [ORDER]: 1 }), server, clock());
  s.onConnectionState({ state: "connected" });
  await flush();
  server.answer(1);
  await flush();
  server.calls.length = 0;

  s.onNotification(notice(OTHER, 5));
  await flush();
  assert.deepEqual(server.calls, []);
});

test("duplicate and stale notifications fetch nothing; a newer one reads the server state and shows it", async function () {
  const view = fakeView({ [ORDER]: 3 });
  const server = fakeServer();
  const s = sync(view, server, clock());

  s.onNotification(notice(ORDER, 3));
  s.onNotification(notice(ORDER, 2));
  s.onNotification({ type: "OrderChanged", orderId: ORDER, version: 9 });
  await flush();
  assert.deepEqual(server.calls, []);

  s.onNotification(notice(ORDER, 4));
  await flush();
  server.answer(4, "printing");
  await flush();
  assert.deepEqual(server.calls, [ORDER]);
  assert.deepEqual(view.applied, [[ORDER, 4, "printing"]]);
});

test("notifications arriving together cause one request and one follow-up, not one each", async function () {
  const view = fakeView({ [ORDER]: 1 });
  const server = fakeServer();
  const s = sync(view, server, clock());

  s.onNotification(notice(ORDER, 2));
  s.onNotification(notice(ORDER, 3));
  s.onNotification(notice(ORDER, 4));
  await flush();
  assert.equal(server.calls.length, 1);

  server.answer(2, "queued");
  await flush();
  assert.equal(server.calls.length, 2);
  server.answer(4, "reprint", "printed");
  await flush();

  assert.equal(server.calls.length, 2);
  assert.deepEqual(view.applied, [[ORDER, 2, "queued"], [ORDER, 4, "reprint"]]);
});

test("an answer older than what is shown never moves the screen back", async function () {
  const view = fakeView({ [ORDER]: 1 });
  const server = fakeServer();
  const s = sync(view, server, clock());

  s.onNotification(notice(ORDER, 2));
  await flush();
  view.shown[ORDER] = 5; // a newer state was rendered meanwhile (for example the panel was reopened)
  server.answer(2, "queued");
  await flush();

  assert.deepEqual(view.applied, []);
});

test("while a manual print of the order is being submitted, its state is left to that submit", async function () {
  const view = fakeView({ [ORDER]: 1 });
  view.busy[ORDER] = true;
  const server = fakeServer();
  const s = sync(view, server, clock());

  s.onNotification(notice(ORDER, 2));
  await flush();
  server.answer(2, "queued");
  await flush();

  assert.deepEqual(view.applied, []);
});

test("a read that began before a manual print of the order finished is not shown", async function () {
  const view = fakeView({ [ORDER]: 3 });
  const server = fakeServer();
  const s = sync(view, server, clock());

  s.onNotification(notice(ORDER, 4));
  await flush();
  view.generation[ORDER] = 1; // the operator's print finished while this read was on its way
  server.answer(4, "reprint", "printed");
  await flush();

  assert.deepEqual(view.applied, []);
});

test("while disconnected, shown orders are read every 10 seconds; nothing is read without a shown order or in a hidden tab", async function () {
  const view = fakeView({});
  const server = fakeServer();
  const timers = clock();
  const hidden = { value: false };
  const s = sync(view, server, timers, hidden);

  s.onConnectionState({ state: "connecting" });
  assert.equal(s.isFallbackActive(), true);
  assert.equal(timers.run(printStatus.FALLBACK_INTERVAL_MS), 1);
  assert.deepEqual(server.calls, []);

  view.shown[ORDER] = 1;
  hidden.value = true;
  timers.run(printStatus.FALLBACK_INTERVAL_MS);
  assert.deepEqual(server.calls, []);

  hidden.value = false;
  timers.run(printStatus.FALLBACK_INTERVAL_MS);
  await flush();
  assert.deepEqual(server.calls, [ORDER]);
  server.answer(2, "printing");
  await flush();
  assert.deepEqual(view.applied, [[ORDER, 2, "printing"]]);
  assert.equal(s.isFallbackActive(), true);
});

test("a reconnect reads every shown order at once and stops the fallback", async function () {
  const view = fakeView({ [ORDER]: 1, [OTHER]: 4 });
  const server = fakeServer();
  const timers = clock();
  const s = sync(view, server, timers);

  s.onConnectionState({ state: "disconnected" });
  assert.equal(s.isFallbackActive(), true);
  s.onConnectionState({ state: "connected", reconnected: true });
  await flush();

  assert.equal(s.isFallbackActive(), false);
  assert.deepEqual(server.calls.sort(), [ORDER, OTHER].sort());
  assert.equal(timers.run(printStatus.FALLBACK_INTERVAL_MS), 0);
});

test("dispose stops the fallback and ignores later notifications", async function () {
  const server = fakeServer();
  const timers = clock();
  const s = sync(fakeView({ [ORDER]: 1 }), server, timers);
  s.onConnectionState({ state: "disconnected" });

  s.dispose();
  s.onNotification(notice(ORDER, 9));
  await flush();

  assert.equal(s.isFallbackActive(), false);
  assert.deepEqual(server.calls, []);
});

function fakeResponse(status, body, type) {
  return {
    ok: status >= 200 && status < 300,
    status: status,
    type: type || "basic",
    json: function () { return body instanceof Error ? Promise.reject(body) : Promise.resolve(body); }
  };
}

test("the state request is a same-origin, uncached GET; anything but a valid answer for this order is ignored", async function () {
  const requests = [];
  async function read(response) {
    return printStatus.fetchPrintState(function (url, init) {
      requests.push([url, init]);
      return Promise.resolve(response);
    }, "/orders/{id}/print-state", ORDER, null);
  }

  const valid = { orderId: ORDER.toUpperCase(), version: 3, mode: "reprint", outcome: "printed" };
  assert.deepEqual(await read(fakeResponse(200, valid)), valid);
  assert.equal(requests[0][0], "/orders/" + ORDER + "/print-state");
  assert.equal(requests[0][1].method, "GET");
  assert.equal(requests[0][1].credentials, "same-origin");
  assert.equal(requests[0][1].cache, "no-store");
  assert.equal(requests[0][1].redirect, "manual");

  assert.equal(await read(fakeResponse(0, null, "opaqueredirect")), null);
  assert.equal(await read(fakeResponse(500, valid)), null);
  assert.equal(await read(fakeResponse(200, new Error("html login page"))), null);
  assert.equal(await read(fakeResponse(200, Object.assign({}, valid, { orderId: OTHER }))), null);
  assert.equal(await read(fakeResponse(200, Object.assign({}, valid, { mode: "printed-on-paper" }))), null);
  assert.equal(await read(fakeResponse(200, Object.assign({}, valid, { version: "3" }))), null);
  assert.equal(await printStatus.fetchPrintState(function () { return Promise.reject(new Error("offline")); }, "/x/{id}", ORDER, null), null);
});

function element(attrs) {
  const el = {
    attrs: Object.assign({}, attrs),
    children: [],
    disabled: false,
    textWrites: 0,
    _text: "",
    classes: new Set(),
    getAttribute: function (n) { return n in el.attrs ? el.attrs[n] : null; },
    setAttribute: function (n, v) { el.attrs[n] = String(v); },
    querySelector: function (selector) {
      const name = selector.replace(/^\[|\]$/g, "");
      const stack = el.children.slice();
      while (stack.length) {
        const child = stack.shift();
        if (name in child.attrs) return child;
        stack.push.apply(stack, child.children);
      }
      return null;
    },
    classList: {
      toggle: function (name, on) { if (on) el.classes.add(name); else el.classes.delete(name); }
    }
  };
  Object.defineProperty(el, "textContent", {
    get: function () { return el._text; },
    set: function (v) { el._text = v; el.textWrites += 1; }
  });
  return el;
}

test("a server state is written into the print action with the labels the server rendered", function () {
  const root = element({ "data-print-action-for": ORDER, "data-print-version": "1" });
  const button = element({
    "data-order-print": ORDER,
    "data-print-mode": "queued",
    "data-label-print": "Print",
    "data-label-reprint": "Reprint",
    "data-label-queued": "In print queue",
    "data-label-printing": "Printing…"
  });
  const label = element({ "data-print-label": "" });
  label._text = "In print queue";
  const outcome = element({ "data-print-outcome": "none", "data-label-printed": "Receipt sent to the printer", "data-label-failed": "Receipt could not be printed" });
  button.children.push(label);
  root.children.push(button, outcome);

  printStatus.applyToRoot(root, { orderId: ORDER, version: 2, mode: "printing", outcome: "none" });
  assert.equal(root.getAttribute("data-print-version"), "2");
  assert.equal(button.getAttribute("data-print-mode"), "printing");
  assert.equal(button.disabled, true);
  assert.equal(label.textContent, "Printing…");
  assert.equal(outcome.textContent, "");

  printStatus.applyToRoot(root, { orderId: ORDER, version: 3, mode: "reprint", outcome: "failed" });
  assert.equal(button.disabled, false);
  assert.equal(label.textContent, "Reprint");
  assert.equal(outcome.textContent, "Receipt could not be printed");
  assert.equal(outcome.getAttribute("data-print-outcome"), "failed");
  assert.equal(outcome.classes.has("text-danger-emphasis"), true);

  // The status region is written only when its text changes, so it announces a change once.
  const writes = outcome.textWrites;
  printStatus.applyToRoot(root, { orderId: ORDER, version: 3, mode: "reprint", outcome: "failed" });
  assert.equal(outcome.textWrites, writes);
});
