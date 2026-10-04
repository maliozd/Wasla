const test = require("node:test");
const assert = require("node:assert/strict");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");
const detailModal = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-detail-modal.js");

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

function deferredFetch() {
  const state = { calls: 0, pending: null };
  state.fetch = function () {
    state.calls += 1;
    let resolve;
    let reject;
    const promise = new Promise(function (res, rej) {
      resolve = res;
      reject = rej;
    });
    const pending = { resolve: resolve, reject: reject };
    state.pending = pending;
    return promise;
  };
  state.abort = function () {
    if (state.pending) {
      const pending = state.pending;
      state.pending = null;
      pending.reject(new Error("aborted"));
    }
  };
  return state;
}

function clock() {
  let now = 0;
  let seq = 1;
  const timers = [];
  return {
    setTimeout: function (fn, ms) {
      const id = seq++;
      timers.push({ id: id, fn: fn, at: now + ms, cleared: false });
      return id;
    },
    clearTimeout: function (id) {
      timers.forEach(function (timer) {
        if (timer.id === id) timer.cleared = true;
      });
    },
    advance: async function (ms) {
      now += ms;
      const due = timers
        .filter(function (timer) { return !timer.cleared && !timer.ran && timer.at <= now; })
        .sort(function (a, b) { return a.at - b.at; });
      for (const timer of due) {
        timer.ran = true;
        timer.fn();
        await flush();
      }
    }
  };
}

function guid(n) {
  return "00000000-0000-4000-8000-" + String(n).padStart(12, "0");
}

function snapshot(ids, patch) {
  const body = {
    serverTimeUtc: "2026-09-23T08:00:00Z",
    todayOrderCount: ids.length,
    cancelledOrderCount: 0,
    orders: ids.map(function (id) {
      return {
        id: id,
        displayNumber: "TY",
        platform: "TrendyolYemek",
        status: "New",
        receivedAtUtc: "2026-09-23T07:00:00Z",
        deliveredAtUtc: null,
        customerName: "Ada",
        totalAmount: 10,
        items: [{ productName: "Lahmacun", quantity: 1, notes: null }]
      };
    })
  };
  return Object.assign(body, patch || {});
}

function harness() {
  const time = clock();
  const http = deferredFetch();
  const accepted = [];
  const statuses = [];
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    maxBackoffMs: 30000,
    setTimeout: time.setTimeout,
    clearTimeout: time.clearTimeout,
    createAbortController: function () { return { abort: http.abort }; },
    fetchSnapshot: http.fetch,
    onStatus: function (status) { statuses.push(status); },
    onAccepted: function (body, meta) { accepted.push({ ids: body.orders.map(function (order) { return order.id; }), meta: meta }); }
  });
  return { time: time, http: http, accepted: accepted, statuses: statuses, coordinator: coordinator };
}

test("first snapshot is the notification baseline", async function () {
  const h = harness();
  const pending = h.coordinator.start();
  await flush();
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2), guid(3)]) });
  const result = await pending;
  assert.equal(result.applied, true);
  assert.equal(result.isBaseline, true);
  assert.deepEqual(result.newIds, []);
  assert.deepEqual(h.accepted[0].meta.newIds, []);
});

test("later snapshots report only ids absent from the baseline", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2)]) });
  await first;
  await h.time.advance(10000);
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2), guid(3)]) });
  await flush();
  assert.deepEqual(h.accepted[1].meta.newIds, [guid(3)]);
  assert.equal(h.accepted[1].meta.isBaseline, false);
});

test("one in-flight request coalesces a second refresh", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  const second = h.coordinator.requestRefresh();
  assert.equal(h.http.calls, 1);
  assert.equal(first, second);
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
  await first;
  await flush();
  assert.equal(h.http.calls, 2);
});

test("a response started before a mutation cannot overwrite the later snapshot", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  const resolveStale = h.http.pending.resolve;
  const endMutation = h.coordinator.beginMutation();
  resolveStale({ kind: "ok", snapshot: snapshot([guid(9)]) });
  await first;
  assert.equal(h.accepted.length, 0);

  endMutation();
  await flush();
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(4)]) });
  await flush();
  assert.deepEqual(h.accepted.map(function (item) { return item.ids; }), [[guid(4)]]);
});

test("a failed refresh keeps the last snapshot and backs off", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
  await first;

  await h.time.advance(10000);
  h.http.pending.resolve({ kind: "error" });
  await flush();
  assert.deepEqual(h.statuses.at(-1), "stale");
  assert.equal(h.accepted.length, 1);

  await h.time.advance(1999);
  assert.equal(h.http.calls, 2);
  await h.time.advance(1);
  assert.equal(h.http.calls, 3);
});

test("retry backoff is capped", function () {
  assert.equal(store.nextBackoffMs(0, 30000), 2000);
  assert.equal(store.nextBackoffMs(1, 30000), 4000);
  assert.equal(store.nextBackoffMs(4, 30000), 30000);
  assert.equal(store.nextBackoffMs(8, 30000), 30000);
});

test("an html 500 is retried and a login redirect stops the loop", async function () {
  const h = harness();
  let parsed = false;
  const serverError = store.classifyResponse(
    { ok: false, status: 500, redirected: false, headers: { get: function () { return "text/html; charset=utf-8"; } } },
    "<!DOCTYPE html><html><body>server error</body></html>",
    function () { parsed = true; return {}; });
  assert.equal(serverError.kind, "error");
  assert.equal(parsed, false);
  assert.equal(store.classifyResponse(
    { ok: false, status: 502, redirected: false, headers: { get: function () { return "text/html"; } } },
    "<html>bad gateway</html>",
    function () { throw new Error("parsed"); }).kind, "error");
  assert.equal(store.classifyResponse(
    { ok: false, status: 503, redirected: false, headers: { get: function () { return "application/json"; } } },
    "",
    function () { throw new Error("parsed"); }).kind, "error");

  const login = store.classifyResponse(
    { ok: true, status: 200, redirected: true, url: "https://sushi-m.wasla.local:7200/auth/login?ReturnUrl=%2Forders%2Flive-data", headers: { get: function () { return "text/html"; } } },
    "<!DOCTYPE html><html><body>login</body></html>",
    function () { parsed = true; return {}; });
  assert.equal(login.kind, "session");
  assert.equal(parsed, false);
  assert.equal(store.classifyResponse(
    { ok: false, status: 401, redirected: false, headers: { get: function () { return "application/json"; } } },
    "{\"message\":\"unauthorized\"}",
    JSON.parse).kind, "session");

  const pending = h.coordinator.start();
  await flush();
  h.http.pending.resolve({ kind: "session" });
  await pending;
  await h.time.advance(60000);
  assert.equal(h.http.calls, 1);
  assert.equal(h.statuses.at(-1), "session");
});

test("an incomplete payload is not an empty success", function () {
  const result = store.classifyResponse(
    { ok: true, status: 200, redirected: false, headers: { get: function () { return "application/json"; } } },
    "{\"serverTimeUtc\":\"2026-09-23T08:00:00Z\"}",
    JSON.parse);
  assert.equal(result.kind, "error");
});

test("unchanged signature comparison stays cheap for thousands of orders", function () {
  const orders = [];
  for (let i = 0; i < 5700; i++) {
    orders.push({
      id: "00000000-0000-0000-0000-" + String(i).padStart(12, "0"),
      displayNumber: "TY-" + i,
      platform: "TrendyolYemek",
      status: "Accepted",
      receivedAtUtc: "2026-09-22T08:00:00Z",
      deliveredAtUtc: null,
      customerName: "Customer <b>" + i + "</b>",
      totalAmount: 120.5,
      items: [
        { productName: "Lahmacun <img>", quantity: 1, notes: "<script>alert(1)</script>" },
        { productName: "Ayran", quantity: 2, notes: null }
      ]
    });
  }
  const payloadBytes = Buffer.byteLength(JSON.stringify({ serverTimeUtc: "2026-09-23T08:00:00Z", orders: orders }));
  const started = process.hrtime.bigint();
  let same = true;
  for (let i = 0; i < orders.length; i++) {
    const left = store.orderContentSignature(orders[i], "2026-09-23T08:00:00Z");
    const right = store.orderContentSignature(orders[i], "2026-09-23T08:00:00Z");
    if (left !== right) same = false;
  }
  const elapsedMs = Number(process.hrtime.bigint() - started) / 1e6;
  const changedStarted = process.hrtime.bigint();
  orders[10].status = "Preparing";
  let changed = 0;
  for (let i = 0; i < orders.length; i++) {
    const before = store.orderContentSignature(
      i === 10 ? Object.assign({}, orders[i], { status: "Accepted" }) : orders[i],
      "2026-09-23T08:00:00Z");
    const after = store.orderContentSignature(orders[i], "2026-09-23T08:00:00Z");
    if (before !== after) changed += 1;
  }
  const changedMs = Number(process.hrtime.bigint() - changedStarted) / 1e6;
  console.log(JSON.stringify({
    orders: orders.length,
    payloadBytes: payloadBytes,
    unchangedSignatureMs: Number(elapsedMs.toFixed(1)),
    changedSignatureMs: Number(changedMs.toFixed(1)),
    changedCards: changed
  }));
  assert.equal(same, true);
  assert.equal(changed, 1);
  assert.ok(payloadBytes > 1000000);
  assert.equal(store.orderContentSignature(orders[0]), store.orderContentSignature(orders[0]));
  assert.notEqual(
    store.elapsedMinutes("2026-09-23T08:00:00Z", orders[0].receivedAtUtc),
    store.elapsedMinutes("2026-09-23T08:01:00Z", orders[0].receivedAtUtc));
  assert.equal(store.orderContentSignature(orders[0]).indexOf("1440"), -1);
});

test("live screen money shows the lira symbol while the locale only changes number formatting", function () {
  const samples = [100, 1230.10, 98765.43];
  function assertLira(value) {
    assert.equal(value.indexOf("₺") >= 0, true);
    assert.equal(value.indexOf("TRY"), -1);
    assert.equal((value.match(/₺/g) || []).length, 1);
  }
  const tr = samples.map(function (amount) { return store.formatAmount(amount, "tr-TR"); });
  const en = samples.map(function (amount) { return store.formatAmount(amount, "en-US"); });
  const ar = samples.map(function (amount) { return store.formatAmount(amount, "ar-SA"); });
  const ru = samples.map(function (amount) { return store.formatAmount(amount, "ru-RU"); });
  assert.deepEqual(tr, ["₺100,00", "₺1.230,10", "₺98.765,43"]);
  assert.equal(en[1].indexOf("1,230.10") >= 0, true);
  assert.equal(en[0].indexOf("100.00") >= 0, true);
  [tr, en, ar, ru].forEach(function (values) {
    values.forEach(function (value) {
      assertLira(value);
      assert.equal(value.indexOf("$"), -1);
      assert.equal(value.toUpperCase().indexOf("SAR"), -1);
      assert.equal(value.indexOf("₽"), -1);
      assert.equal(value.indexOf("RUB"), -1);
    });
  });
  assert.notEqual(tr[1], en[1]);
  assert.equal(store.formatAmount(1230.1, "tr-TR"), store.formatAmount(1230.10, "tr-TR"));
});

test("snapshots apply only after every pending mutation settles", async function () {
  const h = harness();
  const endA = h.coordinator.beginMutation();
  const endB = h.coordinator.beginMutation();
  endA();
  await flush();
  assert.equal(h.http.calls, 0);
  endB();
  await flush();
  assert.equal(h.http.calls, 1);
  const preparing = snapshot([guid(1)]);
  preparing.orders[0].status = "Preparing";
  h.http.pending.resolve({ kind: "ok", snapshot: preparing });
  await flush();
  assert.equal(h.accepted.length, 1);
  assert.equal(h.accepted[0].ids[0], guid(1));
  assert.equal(preparing.orders[0].status, "Preparing");
});

test("the other mutation finish order still coalesces one read", async function () {
  const h = harness();
  const endA = h.coordinator.beginMutation();
  const endB = h.coordinator.beginMutation();
  endB();
  await flush();
  assert.equal(h.http.calls, 0);
  endA();
  await flush();
  assert.equal(h.http.calls, 1);
});

test("an in-flight snapshot cannot apply while another mutation is unresolved", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  h.coordinator.beginMutation();
  h.coordinator.beginMutation();
  await first;
  assert.equal(h.accepted.length, 0);
  assert.equal(h.http.calls, 1);
});

test("a classified html 500 keeps the last snapshot and retries", async function () {
  const time = clock();
  let mode = "ok";
  const http = { calls: 0 };
  const accepted = [];
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    maxBackoffMs: 30000,
    setTimeout: time.setTimeout,
    clearTimeout: time.clearTimeout,
    createAbortController: function () { return { abort: function () {} }; },
    fetchSnapshot: function () {
      http.calls += 1;
      if (mode === "ok") return Promise.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
      return Promise.resolve(store.classifyResponse(
        { ok: false, status: 500, redirected: false, headers: { get: function () { return "text/html"; } } },
        "<html>500</html>",
        JSON.parse));
    },
    onStatus: function () {},
    onAccepted: function (body) { accepted.push(body.orders[0].id); }
  });
  await coordinator.start();
  await flush();
  assert.deepEqual(accepted, [guid(1)]);
  mode = "error";
  await time.advance(10000);
  assert.equal(accepted.length, 1);
  assert.equal(coordinator.lastSnapshot().orders[0].id, guid(1));
  await time.advance(2000);
  assert.equal(http.calls, 3);
});

test("a malformed snapshot does not move the notification baseline", async function () {
  const h = harness();
  const first = h.coordinator.start();
  await flush();
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
  await first;
  await h.time.advance(10000);
  h.http.pending.resolve({
    kind: "ok",
    snapshot: { serverTimeUtc: "not-a-date", orders: [{ id: guid(2) }], todayOrderCount: 0, cancelledOrderCount: 0 }
  });
  await flush();
  assert.equal(h.accepted.length, 1);
  await h.time.advance(2000);
  h.http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2)]) });
  await flush();
  assert.deepEqual(h.accepted[1].meta.newIds, [guid(2)]);
  assert.equal(h.accepted[1].meta.isBaseline, false);
});

test("a failed render does not consume new ids", async function () {
  const time = clock();
  const http = deferredFetch();
  const seen = [];
  let fail = false;
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    maxBackoffMs: 30000,
    setTimeout: time.setTimeout,
    clearTimeout: time.clearTimeout,
    createAbortController: function () { return { abort: http.abort }; },
    fetchSnapshot: http.fetch,
    onStatus: function () {},
    onAccepted: function (_body, meta) {
      if (fail) throw new Error("render failed");
      seen.push(meta.newIds.slice());
    }
  });
  const first = coordinator.start();
  await flush();
  http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
  await first;
  fail = true;
  await time.advance(10000);
  http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2)]) });
  await flush();
  assert.deepEqual(seen, [[]]);
  fail = false;
  await time.advance(2000);
  http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2)]) });
  await flush();
  assert.deepEqual(seen[1], [guid(2)]);
});

test("a minute boundary updates elapsed labels without rebuilding item nodes", function () {
  const document = fakeDocument();
  const previousDocument = globalThis.document;
  globalThis.document = document;
  const host = document.createElement("div");
  host.id = "ordersLiveScreenHost";
  const orders = [];
  for (let i = 0; i < 5700; i++) {
    orders.push({
      id: guid(i),
      displayNumber: "TY-" + i,
      platform: "TrendyolYemek",
      status: "Accepted",
      receivedAtUtc: "2026-09-22T08:00:00Z",
      deliveredAtUtc: null,
      customerName: "Customer <b>" + i + "</b>",
      totalAmount: 120.5,
      items: [
        { productName: "Lahmacun <img>", quantity: 1, notes: "<script>alert(1)</script>" },
        { productName: "Ayran", quantity: 2, notes: null }
      ]
    });
  }
  let browser;
  try {
  browser = store.attachBrowser({
    opts: {
      displayCulture: "tr-TR",
      timeZoneId: "Europe/Istanbul",
      canManageOrders: true,
      messages: {
        elapsedMinutes: "{0} dk",
        ordersStartPreparing: "Hazırla",
        viewDetails: "Detay",
        statusAccepted: "Kabul",
        statusNewBadge: "Yeni",
        platformTrendyolYemek: "Trendyol",
        ordersFullscreenCustomer: "Müşteri",
        ordersFullscreenTotal: "Tutar",
        ordersFullscreenReceived: "Saat",
        ordersActions: "İşlemler"
      }
    },
    state: {},
    getMessage: function (key) { return this.opts.messages[key] || key; },
    showMessage: function () {},
    debugWarn: function () {},
    table: {},
    audio: {}
  }, { document: document, performance: { now: function () { return Date.now(); } } }, store);

  const initialStarted = process.hrtime.bigint();
  browser.renderSnapshot({ serverTimeUtc: "2026-09-23T08:00:00Z", orders: orders, todayOrderCount: 1, cancelledOrderCount: 0 });
  const initialMs = Number(process.hrtime.bigint() - initialStarted) / 1e6;
  const card = host.querySelector('[data-order-id="' + guid(10) + '"]');
  const item = card.querySelector(".wasla-live-screen-card__item");
  const image = card.querySelector("img");
  const action = card.querySelector("[data-order-action]");
  const elapsed = card.querySelector("[data-elapsed]");
  const before = elapsed.textContent;
  const minuteStarted = process.hrtime.bigint();
  const diff = browser.renderSnapshot({ serverTimeUtc: "2026-09-23T08:01:00Z", orders: orders, todayOrderCount: 1, cancelledOrderCount: 0 });
  const minuteMs = Number(process.hrtime.bigint() - minuteStarted) / 1e6;
  console.log(JSON.stringify({
    orders: 5700,
    initialRenderMs: Number(initialMs.toFixed(1)),
    minuteBoundaryMs: Number(minuteMs.toFixed(1)),
    timeUpdates: diff.timeUpdates,
    changedCards: diff.changed
  }));
  assert.equal(diff.changed, 0);
  assert.equal(diff.timeUpdates, 5700);
  assert.equal(card.querySelector(".wasla-live-screen-card__item"), item);
  assert.equal(card.querySelector("img"), image);
  assert.equal(card.querySelector("[data-order-action]"), action);
  assert.notEqual(card.querySelector("[data-elapsed]").textContent, before);
  assert.equal(card.querySelector("[data-elapsed]"), elapsed);
  assert.equal(item.querySelector(".wasla-live-screen-card__note").textContent, "<script>alert(1)</script>");
  } finally {
    globalThis.document = previousDocument;
  }
});

test("pending audio cannot delay polling or notify the same order again after a mutation", async function () {
  const time = clock();
  const http = deferredFetch();
  const document = fakeDocument();
  const previousDocument = globalThis.document;
  const previousFetch = globalThis.fetch;
  const host = document.createElement("div");
  host.id = "ordersLiveScreenHost";
  globalThis.document = document;
  globalThis.fetch = http.fetch;
  let finishAudio;
  let audioFinished = false;
  let soundCalls = 0;
  let browserNotifications = 0;
  const highlighted = [];
  const audio = new Promise(function (resolve) { finishAudio = resolve; });
  function respond(ids) {
    http.pending.resolve({
      ok: true, status: 200, redirected: false,
      headers: { get: function () { return "application/json"; } },
      text: async function () { return JSON.stringify(snapshot(ids)); }
    });
  }
  try {
    const browser = store.attachBrowser({
      opts: { displayCulture: "tr-TR", timeZoneId: "Europe/Istanbul" },
      state: { notificationSettings: { newOrderSoundEnabled: true, showBrowserNotification: true } },
      getMessage: function (key) { return key; },
      table: {
        markOrdersAsRecentlyNew: function (ids) { highlighted.push(ids.slice()); },
        applyNewOrderVisualState: function () {},
        scheduleNewOrderHighlightCleanup: function () {}
      },
      audio: {
        isSoundUnlocked: function () { return true; },
        playSoundNow: function () {
          soundCalls += 1;
          return audio.then(function () { audioFinished = true; });
        },
        showBrowserNotificationIfAllowed: function () { browserNotifications += 1; }
      }
    }, { setTimeout: time.setTimeout, clearTimeout: time.clearTimeout }, store);
    const baseline = browser.start();
    await flush();
    respond([guid(1)]);
    await baseline;
    assert.equal(soundCalls, 0);
    assert.equal(browserNotifications, 0);
    assert.deepEqual(highlighted, []);

    let accepted = false;
    const arrival = browser.requestRefresh().then(function (result) { accepted = result.applied; });
    await flush();
    respond([guid(1), guid(2)]);
    await flush();
    assert.equal(soundCalls, 1);
    assert.equal(accepted, true, "snapshot acceptance must finish while audio is pending");
    assert.equal(audioFinished, false);
    await arrival;

    const endMutation = browser.beginMutation();
    await time.advance(10000);
    assert.equal(http.calls, 2, "polling stays paused while the mutation is open");
    endMutation();
    await flush();
    assert.equal(http.calls, 3);
    respond([guid(1), guid(2)]);
    await flush();
    assert.equal(soundCalls, 1);
    assert.deepEqual(highlighted, [[guid(2)]]);

    await time.advance(10000);
    assert.equal(http.calls, 4, "scheduled polling must continue before audio finishes");
    respond([guid(1), guid(2)]);
    await flush();
    assert.equal(audioFinished, false);
    finishAudio();
    await flush();
    assert.equal(soundCalls, 1);
    assert.equal(browserNotifications, 1);
  } finally {
    finishAudio();
    await flush();
    globalThis.document = previousDocument;
    globalThis.fetch = previousFetch;
  }
});

for (const failure of ["throw", "reject"]) {
  test("notification " + failure + " preserves accepted state and the normal polling interval", async function () {
    const time = clock();
    const http = deferredFetch();
    const statuses = [];
    let notifications = 0;
    const coordinator = store.createRefreshCoordinator({
      setTimeout: time.setTimeout,
      clearTimeout: time.clearTimeout,
      createAbortController: function () { return { abort: http.abort }; },
      fetchSnapshot: http.fetch,
      onStatus: function (status) { statuses.push(status); },
      onAccepted: function () {},
      onNotify: function (body) {
        assert.equal(coordinator.lastSnapshot(), body);
        notifications += 1;
        if (failure === "throw") throw new Error("notification failed");
        return Promise.reject(new Error("notification failed"));
      }
    });
    const baseline = coordinator.start();
    await flush();
    http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1)]) });
    await baseline;
    assert.equal(notifications, 0);
    const arrival = coordinator.requestRefresh();
    await flush();
    const body = snapshot([guid(1), guid(2)]);
    http.pending.resolve({ kind: "ok", snapshot: body });
    assert.equal((await arrival).applied, true);
    await flush();
    assert.equal(coordinator.lastSnapshot(), body);
    assert.deepEqual(statuses, ["ok", "ok"]);
    await time.advance(9999);
    assert.equal(http.calls, 2);
    await time.advance(1);
    assert.equal(http.calls, 3);
    http.pending.resolve({ kind: "ok", snapshot: snapshot([guid(1), guid(2)]) });
    await flush();
    assert.equal(notifications, 1);
  });
}

function fakeDocument() {
  const byId = new Map();
  let doc;
  function El(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this.nodeType = 1;
    this.childNodes = [];
    this.parentElement = null;
    this.attrs = {};
    this.className = "";
    this.hidden = false;
  }
  El.prototype = {
    get children() {
      return this.childNodes.filter(function (node) { return node.nodeType === 1; });
    },
    get firstChild() { return this.childNodes[0] || null; },
    get firstElementChild() { return this.children[0] || null; },
    get nextElementSibling() {
      if (!this.parentElement) return null;
      const kids = this.parentElement.children;
      return kids[kids.indexOf(this) + 1] || null;
    },
    get classList() {
      const self = this;
      function names() { return self.className.split(/\s+/).filter(Boolean); }
      function write(list) { self.className = list.join(" "); }
      return {
        toggle: function (name, on) {
          const current = names();
          const has = current.indexOf(name) >= 0;
          const enable = on === undefined ? !has : !!on;
          const next = current.filter(function (item) { return item !== name; });
          if (enable) next.push(name);
          write(next);
        },
        add: function () {
          const next = names();
          for (let i = 0; i < arguments.length; i++) {
            if (next.indexOf(arguments[i]) < 0) next.push(arguments[i]);
          }
          write(next);
        },
        remove: function () {
          const drop = Array.prototype.slice.call(arguments);
          write(names().filter(function (name) { return drop.indexOf(name) < 0; }));
        },
        contains: function (name) { return names().indexOf(name) >= 0; }
      };
    },
    get style() {
      if (!this._style) {
        const props = {};
        this._style = {
          setProperty: function (name, value) { props[name] = String(value); },
          removeProperty: function (name) { delete props[name]; },
          getPropertyValue: function (name) { return props[name] || ""; }
        };
      }
      return this._style;
    },
    set textContent(value) {
      this.childNodes = [{ nodeType: 3, textContent: value == null ? "" : String(value), parentElement: this }];
    },
    get textContent() {
      return this.childNodes.map(function (node) { return node.textContent || ""; }).join("");
    },
    set id(value) {
      this._id = value;
      byId.set(value, this);
    },
    get id() { return this._id || ""; },
    setAttribute: function (name, value) { this.attrs[name] = String(value); },
    getAttribute: function (name) { return Object.prototype.hasOwnProperty.call(this.attrs, name) ? this.attrs[name] : null; },
    hasAttribute: function (name) { return Object.prototype.hasOwnProperty.call(this.attrs, name); },
    removeAttribute: function (name) { delete this.attrs[name]; },
    appendChild: function (node) {
      if (node.parentElement && node.parentElement.removeChild) node.parentElement.removeChild(node);
      node.parentElement = this;
      this.childNodes.push(node);
      return node;
    },
    removeChild: function (node) {
      if (node.contains && node.contains(doc.activeElement)) doc.activeElement = doc.body;
      const index = this.childNodes.indexOf(node);
      if (index >= 0) this.childNodes.splice(index, 1);
      node.parentElement = null;
      return node;
    },
    insertBefore: function (node, ref) {
      if (node.parentElement && node.parentElement.removeChild) node.parentElement.removeChild(node);
      node.parentElement = this;
      const index = ref ? this.childNodes.indexOf(ref) : -1;
      if (index < 0) this.childNodes.push(node);
      else this.childNodes.splice(index, 0, node);
      return node;
    },
    remove: function () {
      if (this.parentElement) this.parentElement.removeChild(this);
    },
    replaceWith: function (node) {
      const parent = this.parentElement;
      const index = parent.childNodes.indexOf(this);
      if (node.parentElement && node.parentElement.removeChild) node.parentElement.removeChild(node);
      node.parentElement = parent;
      parent.childNodes.splice(index, 1, node);
      this.parentElement = null;
    },
    contains: function (node) {
      while (node) { if (node === this) return true; node = node.parentElement; }
      return false;
    },
    closest: function (selector) {
      let node = this;
      while (node) { if (matches(node, selector)) return node; node = node.parentElement; }
      return null;
    },
    focus: function () { doc.activeElement = this; },
    querySelector: function (selector) { return queryAll(this, selector)[0] || null; },
    querySelectorAll: function (selector) { return queryAll(this, selector); }
  };
  function matchSimple(el, selector) {
    if (!selector) return false;
    if (selector.charAt(0) === "." && selector.indexOf("[") < 0) {
      return el.className.split(/\s+/).indexOf(selector.slice(1)) >= 0;
    }
    if (selector.charAt(0) === "[" && selector.charAt(selector.length - 1) === "]") {
      const body = selector.slice(1, -1);
      const eq = body.indexOf("=");
      if (eq < 0) return el.getAttribute(body) !== null;
      let expected = body.slice(eq + 1).trim();
      if ((expected.charAt(0) === '"' && expected.charAt(expected.length - 1) === '"') ||
          (expected.charAt(0) === "'" && expected.charAt(expected.length - 1) === "'")) {
        expected = expected.slice(1, -1);
      }
      return el.getAttribute(body.slice(0, eq).trim()) === expected;
    }
    if (selector.indexOf("[") >= 0 || selector.indexOf(".") > 0) {
      const tokens = selector.match(/(\.[a-zA-Z0-9_-]+|\[[^\]]+\]|[a-zA-Z][\w-]*)/g);
      if (tokens && tokens.join("") === selector) {
        return tokens.every(function (token) { return matchSimple(el, token); });
      }
    }
    return el.tagName === selector.toUpperCase();
  }
  function matches(el, selector) {
    const parts = selector.split(",");
    if (parts.length > 1) return parts.some(function (part) { return matches(el, part.trim()); });
    return matchSimple(el, selector.trim());
  }
  function walk(node, visit) {
    node.childNodes.forEach(function (child) {
      if (child.nodeType !== 1) return;
      visit(child);
      walk(child, visit);
    });
  }
  function queryAll(root, selector) {
    if (selector.indexOf(",") >= 0) {
      const seen = [];
      selector.split(",").forEach(function (part) {
        queryAll(root, part.trim()).forEach(function (node) {
          if (seen.indexOf(node) < 0) seen.push(node);
        });
      });
      return seen;
    }
    const parts = selector.trim().split(/\s+/);
    let current = [root];
    parts.forEach(function (part) {
      const next = [];
      current.forEach(function (node) {
        walk(node, function (desc) {
          if (matches(desc, part)) next.push(desc);
        });
      });
      current = next;
    });
    return current;
  }
  doc = {
    addEventListener: function () {},
    dispatchEvent: function () { return true; },
    createElement: function (tag) { return new El(tag); },
    createTextNode: function (text) { return { nodeType: 3, textContent: String(text), parentElement: null }; },
    getElementById: function (id) { return byId.get(id) || null; },
    documentElement: { lang: "tr-TR" }
  };
  doc.body = new El("body");
  doc.activeElement = doc.body;
  return doc;
}

function boardHarness(canManageOrders = true, extras) {
  const previous = globalThis.document;
  const doc = fakeDocument();
  globalThis.document = doc;
  const host = doc.createElement("div");
  host.id = "ordersLiveScreenHost";
  doc.body.appendChild(host);
  let view = "board";
  const options = extras || {};
  const browser = store.attachBrowser({
    opts: { displayCulture: "tr-TR", timeZoneId: "Europe/Istanbul", canManageOrders },
    liveView: { getView: () => view },
    getMessage: key => key,
    liveDetailModal: options.liveDetailModal,
    audio: options.audio
  }, options.global || {}, store);
  return { doc, host, browser,
    view: value => { view = value; },
    card: id => host.querySelector('.wasla-live-screen-card[data-order-id="' + id + '"]'),
    restore: () => { globalThis.document = previous; }
  };
}

test("demo cards never change operational column counts or new-order notifications in any view", () => {
  const h = boardHarness();
  try {
    for (const view of ["board", "list", "focus"]) {
      h.view(view);
      const body = snapshot([guid(1), guid(2)]);
      body.orders[1].isDemo = true;
      h.browser.renderSnapshot(body);
      assert.equal(h.host.querySelectorAll("[data-board-count]").reduce((n, e) => n + Number(e.textContent), 0), 1);
      assert.deepEqual(store.collectNewIds(new Set([guid(1)]), body.orders, true), []);
      body.orders.push(Object.assign({}, body.orders[0], { id: guid(3) }));
      assert.deepEqual(store.collectNewIds(new Set([guid(1)]), body.orders, true), [guid(3)]);
    }
  } finally { h.restore(); }
});

test("Focus renders demo controls locally and leaves real detail loading available", () => {
  let reads = 0;
  const h = boardHarness(true, { liveDetailModal: { loadPanel: () => { reads++; return Promise.resolve(true); } } });
  try {
    h.view("focus");
    const body = snapshot([guid(1), guid(2)]);
    body.orders[0].isDemo = true;
    h.browser.renderSnapshot(body);
    h.browser.selectOrder(guid(1), true);
    assert.equal(reads, 0);
    const card = h.host.querySelector("[data-wasla-demo-card]");
    assert.ok(card);
    assert.equal(card.querySelector("[data-order-action]").getAttribute("data-wasla-demo"), "true");
    body.orders[0].status = "OnTheWay";
    h.browser.renderSnapshot(body);
    const onTheWay = h.host.querySelector("[data-wasla-demo-card]");
    assert.equal(onTheWay.getAttribute("data-order-status"), "OnTheWay");
    assert.equal(onTheWay.querySelector("[data-order-action]"), null, "the platform courier delivers; no restaurant action");
    h.browser.selectOrder(guid(2), true);
    assert.equal(reads, 1);
  } finally { h.restore(); }
});

test("A new practice order offers Approve but no Reject in Board, List or Focus; real orders keep Reject", () => {
  const h = boardHarness(true, { liveDetailModal: { loadPanel: () => Promise.resolve(true) } });
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[0].status = "New";
    body.orders[0].isDemo = true;
    body.orders[1].status = "New";
    for (const view of ["board", "list", "focus"]) {
      h.view(view);
      h.browser.renderSnapshot(body);
      if (view === "focus") h.browser.selectOrder(guid(1), true);
      const demoActions = h.host.querySelectorAll("[data-order-action]")
        .filter(b => b.getAttribute("data-order-id") === guid(1))
        .map(b => b.getAttribute("data-order-action"));
      assert.deepEqual(demoActions, ["approve"], view + ": practice order is only approved");
      if (view !== "focus") {
        const realActions = h.host.querySelectorAll("[data-order-action]")
          .filter(b => b.getAttribute("data-order-id") === guid(2))
          .map(b => b.getAttribute("data-order-action"));
        assert.deepEqual(realActions, ["approve", "reject"], view + ": real orders are unchanged");
      }
    }
  } finally { h.restore(); }
});

test("Courier steps offer no manual Hand to courier or Delivered action in Board, List or Focus, real or demo", () => {
  const h = boardHarness(true, { liveDetailModal: { loadPanel: () => Promise.resolve(true) } });
  try {
    const body = snapshot([guid(1), guid(2), guid(3), guid(4), guid(5)]);
    body.orders[0].status = "OnTheWay";
    body.orders[1].status = "OnTheWay";
    body.orders[1].isDemo = true;
    body.orders[2].status = "Delivered";
    body.orders[3].status = "ReadyForPickup";
    body.orders[4].status = "Preparing";
    for (const view of ["board", "list", "focus"]) {
      h.view(view);
      h.browser.renderSnapshot(body);
      if (view === "focus") h.browser.selectOrder(guid(2), true);
      const actions = h.host.querySelectorAll("[data-order-action]").map(b => b.getAttribute("data-order-action"));
      assert.equal(actions.indexOf("mark-delivered"), -1, view + " must not render mark-delivered");
      assert.equal(actions.indexOf("hand-to-courier"), -1, view + " must not render hand-to-courier");
      if (view !== "focus") assert.ok(actions.indexOf("mark-ready") >= 0, view + ": Mark ready stays the last restaurant action");
      for (const id of [guid(1), guid(2), guid(4)]) {
        const withActions = h.host.querySelectorAll("[data-order-action]").filter(b => b.getAttribute("data-order-id") === id);
        assert.equal(withActions.length, 0, view + ": courier-step order " + id + " has no restaurant action");
      }
      const delivered = h.host.querySelectorAll("[data-order-status='Delivered']");
      assert.ok(delivered.length > 0 || view === "focus", view + " still shows Delivered orders");
    }
  } finally { h.restore(); }
});

test("Board groups actual statuses, keeps item notes and authorized actions, and moves the same card with focus", () => {
  const h = boardHarness();
  try {
    const body = snapshot([1, 2, 3, 4, 5, 6].map(guid));
    const statuses = ["New", "Accepted", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered"];
    body.orders.forEach((o, i) => { o.status = statuses[i]; });
    body.orders[1].items[0].notes = "<script>not markup</script>";
    h.browser.renderSnapshot(body);
    const columns = h.host.querySelectorAll("[data-board-column]");
    assert.equal(columns.length, 5);
    assert.deepEqual(columns.map(c => c.querySelector("[data-board-count]").textContent), ["2", "1", "1", "1", "1"]);
    const cards = h.host.querySelectorAll(".wasla-live-screen-card");
    const accepted = cards[1];
    assert.equal(accepted.getAttribute("data-order-status"), "Accepted");
    assert.equal(accepted.querySelector("[data-status-badge]").textContent, "statusAccepted");
    assert.equal(accepted.querySelector("[data-order-action]").getAttribute("data-order-action"), "start-preparing");
    assert.deepEqual(cards[0].querySelectorAll("[data-order-action]").map(b => b.getAttribute("data-order-action")), ["approve", "reject"]);
    assert.equal(accepted.querySelector(".wasla-live-screen-card__note").textContent, "<script>not markup</script>");
    assert.equal(h.host.querySelectorAll("script").length, 0);
    const item = accepted.querySelector(".wasla-live-screen-card__item");
    accepted.querySelector("[data-order-action]").focus();
    body.orders[1].status = "Preparing";
    const diff = h.browser.renderSnapshot(body);
    assert.equal(diff.changed, 1);
    assert.equal(accepted.parentElement.getAttribute("data-board-list"), "preparing");
    assert.equal(accepted.querySelector(".wasla-live-screen-card__item"), item);
    assert.equal(h.doc.activeElement, accepted.querySelector("[data-order-action]"));
    assert.equal(h.doc.activeElement.getAttribute("data-order-action"), "mark-ready");
    assert.equal(h.browser.renderSnapshot(body).moved, 0);

    const modalButton = h.doc.createElement("button");
    h.doc.body.appendChild(modalButton);
    modalButton.focus();
    body.orders[1].status = "ReadyForPickup";
    h.browser.renderSnapshot(body);
    assert.equal(h.doc.activeElement, modalButton, "polling must not steal focus from the modal");

    accepted.querySelector("[data-order-detail]").focus();
    body.orders = body.orders.filter(o => o.id !== guid(2) && o.id !== guid(6));
    assert.equal(h.browser.renderSnapshot(body).removed, 2);
    assert.equal(h.doc.activeElement, h.host, "removed orders leave a keyboard focus target");
    assert.equal(columns[4].querySelector("[data-board-empty]").hidden, false);
  } finally { h.restore(); }
});

test("Grouped list uses Board groups once, shows customer and address, and reuses unchanged rows", () => {
  const h = boardHarness(false);
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[0].status = "New";
    body.orders[1].status = "Accepted";
    body.orders[0].customerName = "Ada";
    body.orders[0].customerAddress = "Ada Street 4";
    body.orders[0].customerNote = "Zili çalmayın, arayın.";
    body.orders[1].customerName = "Hidden Customer";
    body.orders[1].customerAddress = "   ";
    body.orders[1].customerNote = "   ";
    body.orders[1].items[0].notes = "<b>not html</b>";
    body.orders[1].items.push({ productName: "Ayran", quantity: 1, notes: "   " });
    h.browser.renderSnapshot(body);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 1);
    assert.equal(h.host.querySelectorAll("[data-order-note]").length, 1);
    const boardNote = h.host.querySelector("[data-order-note]");
    assert.equal(boardNote.querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");
    const boardBody = boardNote.parentElement;
    assert.equal(boardBody.className.indexOf("orders-card-body") >= 0, true);
    assert.equal(boardBody.children[0].className.indexOf("wasla-live-card__identity") >= 0, true);
    assert.equal(boardBody.children[1], boardNote);
    assert.equal(boardBody.children[2].className.indexOf("wasla-live-board-items") >= 0, true);
    assert.equal(h.host.querySelector(".wasla-live-screen-card__items").contains(boardNote), false);
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, 2);

    h.view("list");
    const switched = h.browser.renderSnapshot(body);
    assert.equal(switched.changed, 2);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-groups").length, 1);
    assert.equal(h.host.querySelectorAll(".orders-card-grid").length, 0);
    assert.equal(h.host.querySelectorAll("[data-board-column]").length, 5);
    const rows = h.host.querySelectorAll(".wasla-live-screen-card");
    assert.equal(rows.length, 2);
    assert.equal(new Set(rows.map(row => row.getAttribute("data-order-id"))).size, 2);
    assert.equal(rows[0].getAttribute("data-live-layout"), "list");
    assert.equal(rows[0].parentElement.getAttribute("data-board-list"), "new");
    assert.equal(rows[1].parentElement.getAttribute("data-board-list"), "new");
    assert.equal(rows[0].querySelector("[data-status-badge]").textContent, "statusNew");
    assert.equal(rows[1].querySelector("[data-status-badge]").textContent, "statusAccepted");
    assert.equal(h.host.querySelectorAll(".wasla-live-list-columns").length, 0);
    assert.equal(rows[0].children[0].className.indexOf("wasla-live-list-row__identity") >= 0, true);
    assert.equal(rows[0].children[1].className.indexOf("wasla-live-list-row__items") >= 0, true);
    assert.equal(rows[0].children[2].className.indexOf("wasla-live-list-row__customer") >= 0, true);
    assert.equal(rows[0].children[3].className.indexOf("wasla-live-list-row__total") >= 0, true);
    assert.equal(rows[0].children[4].className.indexOf("wasla-live-list-row__elapsed") >= 0, true);
    assert.equal(rows[0].children[5].className.indexOf("wasla-live-list-row__aside") >= 0, true);
    const identity = rows[0].querySelector(".wasla-live-list-row__identity");
    const headline = identity.firstElementChild;
    assert.equal(headline.className.indexOf("wasla-live-list-row__headline") >= 0, true);
    assert.equal(headline.querySelector(".platform-badge") !== null, true);
    assert.equal(headline.querySelector(".wasla-live-screen-card__code") !== null, true);
    assert.equal(identity.contains(rows[0].querySelector("[data-total]")), false);
    assert.equal(identity.contains(rows[0].querySelector("[data-received]")), true);
    assert.equal(identity.querySelector(".wasla-live-list-row__received-label").textContent, "ordersFullscreenReceived");
    assert.equal(headline.contains(rows[0].querySelector("[data-status-badge]")), true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__meta").contains(rows[0].querySelector("[data-status-badge]")), false);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__aside").contains(rows[0].querySelector("[data-elapsed]")), false);
    assert.equal(rows[0].children[4].getAttribute("data-elapsed") !== null, true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__aside").contains(rows[0].querySelector("[data-total]")), false);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__total").contains(rows[0].querySelector("[data-total]")), true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__total").firstElementChild.className.indexOf("wasla-live-list-row__total-label") >= 0, true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__total-label").textContent, "listTotal");
    assert.equal(identity.contains(rows[0].querySelector("[data-customer-name]")), false);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__customer-main").contains(rows[0].querySelector("[data-customer-name]")), true);
    assert.equal(rows[0].querySelector("[data-customer-name]").textContent, "Ada");
    assert.equal(rows[0].querySelector("[data-customer-address]").textContent, "Ada Street 4");
    assert.equal(rows[0].querySelector("[data-customer-address]").hidden, false);
    assert.equal(rows[1].querySelector("[data-customer-name]").textContent, "Hidden Customer");
    assert.equal(rows[1].querySelector("[data-customer-address]").hidden, true);
    const orderNote = rows[0].querySelector("[data-order-note]");
    assert.equal(orderNote !== null, true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__customer").contains(orderNote), true);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__items").contains(orderNote), false);
    assert.equal(orderNote.querySelector(".wasla-live-list-row__order-note-label").textContent, "orderNote");
    assert.equal(orderNote.querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");
    assert.equal(rows[0].className.indexOf("wasla-live-list-row__customer--with-note") >= 0, false);
    assert.equal(rows[0].querySelector(".wasla-live-list-row__customer").className.indexOf("wasla-live-list-row__customer--with-note") >= 0, true);
    assert.equal(rows[1].querySelector("[data-order-note]"), null);
    body.orders[0].customerNote = "   ";
    h.browser.renderSnapshot(body);
    assert.equal(rows[0].querySelector("[data-order-note]"), null);
    body.orders[0].customerNote = "Zili çalmayın, arayın.";
    h.browser.renderSnapshot(body);
    assert.equal(rows[0].querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");
    const note = rows[1].querySelector(".wasla-live-screen-card__note");
    assert.equal(note.querySelector(".wasla-live-list-row__note-text").textContent, "<b>not html</b>");
    assert.equal(note.querySelector(".wasla-live-list-row__note-icon").getAttribute("aria-hidden"), "true");
    assert.equal(note.querySelector(".visually-hidden").textContent, "itemNote");
    assert.equal(note.querySelector(".wasla-live-list-row__note-text").children.length, 0);
    assert.equal(rows[1].querySelectorAll(".wasla-live-screen-card__note").length, 1);
    assert.notEqual(rows[0].querySelector("[data-received]").textContent, "");
    assert.equal(h.host.querySelectorAll("[data-order-action]").length, 0);
    assert.equal(h.host.querySelectorAll("[data-order-detail]").length, 2);
    assert.equal(h.host.querySelector('[data-board-column="preparing"] [data-board-empty]').hidden, false);

    const row = id => h.host.querySelectorAll(".wasla-live-screen-card").find(el => el.getAttribute("data-order-id") === id);
    const item = rows[1].querySelector(".wasla-live-screen-card__item");
    const again = h.browser.renderSnapshot(body);
    assert.equal(again.changed, 0);
    assert.equal(again.moved, 0);
    assert.equal(row(guid(2)).querySelector(".wasla-live-screen-card__item"), item);

    body.serverTimeUtc = "2026-09-23T08:01:00Z";
    const minute = h.browser.renderSnapshot(body);
    assert.equal(minute.changed, 0);
    assert.equal(minute.timeUpdates, 2);
    assert.equal(row(guid(2)).querySelector(".wasla-live-screen-card__item"), item);
    assert.equal(row(guid(2)).querySelector("[data-order-detail]").parentElement, row(guid(2)).querySelector("[data-order-actions]").parentElement);
    body.orders[1].totalAmount = 1023.9;
    const priced = h.browser.renderSnapshot(body);
    assert.equal(priced.changed, 1);
    assert.equal(row(guid(2)).querySelector(".wasla-live-list-row__total-label").textContent, "listTotal");
    assert.equal(row(guid(2)).querySelector("[data-total]").textContent, "₺1.023,90");

    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(h.host.querySelectorAll(".wasla-live-groups").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 1);
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, 2);
    assert.equal(row(guid(1)).getAttribute("data-live-layout"), "board");
    assert.equal(row(guid(1)).querySelector("[data-customer-name]"), null);
    assert.equal(row(guid(1)).querySelector("[data-customer-address]"), null);
    assert.equal(row(guid(1)).textContent.indexOf("Ada") < 0, true);
    assert.equal(row(guid(1)).querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");
    h.view("list");
    h.browser.renderSnapshot(snapshot([]));
    assert.equal(h.host.querySelectorAll(".wasla-live-groups [data-board-column]").length, 0);
    assert.equal(h.host.querySelector(".wasla-live-empty").getAttribute("data-live-empty"), "none");
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0);
  } finally { h.restore(); }
});

test("Board places the order note above items and keeps only elapsed time", () => {
  const h = boardHarness();
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[0].customerNote = "Güvenliğe bırakmayın, 3. kata çıkarabilir misiniz?";
    body.orders[0].items = [
      { productName: "Cacık", quantity: 1, notes: null },
      { productName: "Kuru Fasulye", quantity: 1, notes: "Yağı mümkünse az olsun." }
    ];
    body.orders[0].receivedAtUtc = "2026-09-22T16:30:00Z";
    body.orders[1].customerNote = null;
    h.browser.renderSnapshot(body);
    const card = h.card(guid(1));
    const classes = Array.from(card.querySelector(".orders-card-body").children).map(el => el.className);
    assert.equal(classes[0].indexOf("wasla-live-card__identity") >= 0, true);
    assert.equal(classes[1].indexOf("wasla-live-board-order-note") >= 0, true);
    assert.equal(classes[2].indexOf("wasla-live-board-items") >= 0, true);
    assert.equal(classes[3].indexOf("wasla-live-card__secondary") >= 0, true);
    assert.equal(classes[4].indexOf("orders-card-actions") >= 0, true);
    const note = card.querySelector("[data-order-note-text]");
    assert.equal(note.textContent, "Güvenliğe bırakmayın, 3. kata çıkarabilir misiniz?");
    assert.equal(note.title, note.textContent);
    assert.equal(card.querySelector(".wasla-live-board-items").contains(note), false);
    const item = card.querySelectorAll(".wasla-live-screen-card__item")[1];
    assert.equal(item.querySelector(".wasla-live-screen-card__product").textContent, "Kuru Fasulye");
    assert.equal(item.querySelector(".wasla-live-screen-card__note").textContent, "Yağı mümkünse az olsun.");
    assert.equal(card.querySelector("[data-received]"), null);
    assert.equal(card.querySelector("[data-received-row]"), null);
    assert.notEqual(card.querySelector("[data-elapsed]").textContent, "");
    assert.equal(card.textContent.indexOf("2026") < 0, true);
    assert.equal(card.querySelector("[data-status-badge]") !== null, true);
    assert.equal(card.querySelector("[data-total]") !== null, true);
    const plain = h.card(guid(2));
    assert.equal(plain.querySelector("[data-order-note]"), null);
    assert.equal(plain.querySelector("[data-received]"), null);
    assert.equal(plain.querySelector(".orders-card-body").children[0].className.indexOf("wasla-live-card__identity") >= 0, true);
    assert.equal(plain.querySelector(".orders-card-body").children[1].className.indexOf("wasla-live-board-items") >= 0, true);
    assert.notEqual(plain.querySelector("[data-elapsed]").textContent, "");

    h.view("list");
    h.browser.renderSnapshot(body);
    const list = h.card(guid(1));
    assert.notEqual(list.querySelector("[data-received]"), null);
    assert.equal(list.querySelector("[data-order-note]").className.indexOf("wasla-live-list-row__order-note") >= 0, true);
    assert.equal(list.querySelector(".wasla-live-board-order-note"), null);

    const fs = require("fs");
    const path = require("path");
    const detail = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/Areas/Tenant/Views/Orders/_OrderDetailPanel.cshtml"), "utf8");
    assert.match(detail, /Model\.CustomerNote/);
    assert.match(detail, /FormatReceivedAtUtc/);
    assert.match(detail, /@foreach \(var i in Model\.Items\)/);
  } finally { h.restore(); }
});

test("board product overflow is marked only when the item area is taller than its viewport", () => {
  const h = boardHarness();
  try {
    const body = snapshot([guid(1)]);
    body.orders[0].items = [{ productName: "Lahmacun", quantity: 1, notes: "Acısız" }];
    h.browser.renderSnapshot(body);
    const card = h.card(guid(1));
    const viewport = card.querySelector(".wasla-live-board-items__viewport");
    const shell = card.querySelector(".wasla-live-board-items");
    const details = card.querySelector(".wasla-live-screen-card__details");
    assert.ok(viewport);
    assert.equal(shell.classList.contains("has-item-overflow"), false);
    assert.equal(details.classList.contains("has-item-overflow"), false);
    assert.equal(shell.querySelector(".wasla-live-board-items__more").textContent, "boardItemsMore");
    assert.equal(card.querySelector(".wasla-live-screen-card__items").contains(shell.querySelector(".wasla-live-board-items__more")), false);

    viewport.scrollHeight = 40;
    viewport.clientHeight = 80;
    h.browser.renderSnapshot(body);
    assert.equal(shell.classList.contains("has-item-overflow"), false);
    assert.equal(details.classList.contains("has-item-overflow"), false);

    viewport.scrollHeight = 400;
    viewport.clientHeight = 0;
    h.browser.renderSnapshot(body);
    assert.equal(shell.classList.contains("has-item-overflow"), false);

    const names = ["A", "B", "C", "D", "E", "F", "G", "H"];
    body.orders[0].items = names.map(name => ({ productName: name, quantity: 1, notes: "note " + name }));
    viewport.scrollHeight = 400;
    viewport.clientHeight = 80;
    h.browser.renderSnapshot(body);
    const longCard = h.card(guid(1));
    assert.equal(longCard.querySelector(".wasla-live-board-items").classList.contains("has-item-overflow"), true);
    assert.equal(longCard.querySelector(".wasla-live-screen-card__details").classList.contains("has-item-overflow"), true);
    assert.deepEqual(longCard.querySelectorAll(".wasla-live-screen-card__product").map(node => node.textContent), names);
    assert.deepEqual(longCard.querySelectorAll(".wasla-live-screen-card__note").map(node => node.textContent), names.map(name => "note " + name));

    body.orders[0].items = [{ productName: "Lahmacun", quantity: 1, notes: null }];
    longCard.querySelector(".wasla-live-board-items__viewport").scrollHeight = 30;
    longCard.querySelector(".wasla-live-board-items__viewport").clientHeight = 80;
    h.browser.renderSnapshot(body);
    const shortCard = h.card(guid(1));
    assert.equal(shortCard.querySelector(".wasla-live-board-items").classList.contains("has-item-overflow"), false);
    assert.equal(shortCard.querySelector(".wasla-live-screen-card__details").classList.contains("has-item-overflow"), false);
    assert.equal(shortCard.querySelector(".wasla-live-screen-card__product").textContent, "Lahmacun");

    h.view("list");
    h.browser.renderSnapshot(snapshot([guid(2)]));
    const listCard = h.card(guid(2));
    assert.equal(listCard.querySelector(".wasla-live-board-items"), null);
    assert.equal(listCard.querySelector(".wasla-live-screen-card__details").classList.contains("has-item-overflow"), false);

    const fs = require("fs");
    const path = require("path");
    const detail = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/Areas/Tenant/Views/Orders/_OrderDetailPanel.cshtml"), "utf8");
    assert.match(detail, /@foreach \(var i in Model\.Items\)/);
    assert.match(detail, /wasla-live-detail__product/);
    assert.equal(detail.includes("wasla-live-board-items"), false);
    const css = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");
    assert.match(css, /\.wasla-live-board \.wasla-live-board-items__viewport \{\s*max-height: 6\.75rem;\s*overflow: hidden;/);
    assert.match(css, /\.wasla-live-board \.wasla-live-screen-card__details\.has-item-overflow \{\s*animation: wasla-board-details-nudge 2\.4s ease-in-out infinite;/);
    assert.match(css, /@media \(prefers-reduced-motion: reduce\) \{\s*\.wasla-live-board \.wasla-live-screen-card__details\.has-item-overflow \{\s*animation: none;/);
  } finally { h.restore(); }
});

test("Grouped list keeps lifecycle actions and moves a row without rebuilding its products", () => {
  const h = boardHarness(true);
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[1].status = "Accepted";
    h.view("list");
    h.browser.renderSnapshot(body);
    const row = id => h.host.querySelectorAll(".wasla-live-screen-card").find(el => el.getAttribute("data-order-id") === id);
    const accepted = row(guid(2));
    const item = accepted.querySelector(".wasla-live-screen-card__item");
    assert.deepEqual(row(guid(1)).querySelectorAll("[data-order-action]").map(b => b.getAttribute("data-order-action")), ["approve", "reject"]);
    assert.equal(accepted.querySelector("[data-order-action]").getAttribute("data-order-action"), "start-preparing");
    accepted.querySelector("[data-order-action]").focus();
    body.orders[1].status = "Preparing";
    const diff = h.browser.renderSnapshot(body);
    assert.equal(diff.changed, 1);
    assert.equal(row(guid(2)), accepted);
    assert.equal(accepted.parentElement.getAttribute("data-board-list"), "preparing");
    assert.equal(accepted.querySelector(".wasla-live-screen-card__item"), item);
    assert.equal(h.doc.activeElement.getAttribute("data-order-action"), "mark-ready");
    assert.equal(h.host.querySelector('[data-board-column="new"] [data-board-count]').textContent, "1");
    assert.equal(h.host.querySelector('[data-board-column="preparing"] [data-board-count]').textContent, "1");

    const modalButton = h.doc.createElement("button");
    h.doc.body.appendChild(modalButton);
    modalButton.focus();
    body.orders[1].status = "ReadyForPickup";
    h.browser.renderSnapshot(body);
    assert.equal(h.doc.activeElement, modalButton);
  } finally { h.restore(); }
});

test("view preference defaults to Board and preserves cards/list even when storage is unavailable", () => {
  const vm = require("node:vm");
  const fs = require("node:fs");
  const source = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-view.js"), "utf8");
  for (const saved of [null, "invalid", "board", "cards", "list", "focus", "throws"]) {
    let init;
    let rendered = 0;
    let written;
    const O = { opts: { pageMode: "liveDisplay" }, liveStore: { refreshView: () => rendered++ } };
    vm.runInNewContext(source, {
      window: { WaslaOrders: O },
      document: { getElementById: () => null, querySelectorAll: () => [], addEventListener: (name, fn) => { if (name === "DOMContentLoaded") init = fn; } },
      localStorage: { getItem: () => { if (saved === "throws") throw Error("denied"); return saved; }, setItem: (key, value) => { written = value; } }
    });
    init();
    assert.equal(O.liveView.getView(), saved === "list" || saved === "board" || saved === "focus" ? saved : "board");
    assert.equal(written, saved === "cards" ? "board" : undefined, "only a retired cards preference is migrated");
    O.liveView.setView("list");
    assert.equal(written, "list");
    assert.equal(rendered, 2);
  }
});

for (const count of [30, 5755]) {
  test("Board synthetic " + count + " orders retains all IDs and avoids unchanged DOM moves", () => {
    const h = boardHarness();
    try {
      const body = snapshot(Array.from({ length: count }, (_, i) => guid(i + 1)));
      const statuses = ["New", "Accepted", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered"];
      body.orders.forEach((o, i) => { o.status = statuses[i % 6]; });
      const start = performance.now();
      h.browser.renderSnapshot(body);
      const first = performance.now();
      const unchanged = h.browser.renderSnapshot(body);
      const next = performance.now();
      assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, count);
      assert.equal(unchanged.moved, 0);
      assert.equal(unchanged.changed, 0);
      assert.equal(unchanged.timeUpdates, 0);
      body.serverTimeUtc = "2026-09-23T08:01:00Z";
      const minute = h.browser.renderSnapshot(body);
      const end = performance.now();
      assert.equal(minute.timeUpdates, count);
      assert.equal(minute.changed, 0);
      assert.equal(minute.moved, 0);
      console.log(JSON.stringify({ fixture: "Board fake DOM (no layout/paint)", count, initialMs: +(first-start).toFixed(1), unchangedMs: +(next-first).toFixed(1), minuteMs: +(end-next).toFixed(1) }));
    } finally { h.restore(); }
  });
}

function paintDetail(container, action) {
  while (container.firstChild) container.removeChild(container.firstChild);
  const wrap = document.createElement("div");
  wrap.className = "wasla-live-detail";
  const actions = document.createElement("div");
  actions.className = "wasla-live-detail__actions";
  const button = document.createElement("button");
  button.setAttribute("data-order-action", action);
  actions.appendChild(button);
  wrap.appendChild(actions);
  container.appendChild(wrap);
}

test("Focus keeps Board groups, selects the first order once, and ignores unchanged snapshots", async () => {
  let loads = 0;
  const h = boardHarness(true, {
    liveDetailModal: {
      loadPanel: function (container) {
        loads += 1;
        paintDetail(container, "approve");
        return Promise.resolve(true);
      },
      cancelPanel: function () {}
    }
  });
  try {
    const body = snapshot([guid(1), guid(2), guid(3)]);
    body.orders[0].status = "New";
    body.orders[0].displayNumber = "TY-10000000000000000001";
    body.orders[0].customerName = "Secret Customer";
    body.orders[0].items = [
      { productName: "Soup", quantity: 1, notes: "no onion" },
      { productName: "Rice", quantity: 2, notes: null },
      { productName: "Tea", quantity: 1, notes: null }
    ];
    body.orders[1].status = "Accepted";
    body.orders[1].customerName = "Other Customer";
    body.orders[2].status = "Preparing";
    h.view("focus");
    h.browser.renderSnapshot(body);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-groups").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-focus").length, 1);
    assert.equal(h.host.querySelectorAll("[data-board-column]").length, 5);
    const entries = h.host.querySelectorAll("[data-focus-select]");
    assert.equal(entries.length, 3);
    assert.equal(entries[0].parentElement.getAttribute("data-board-list"), "new");
    assert.equal(entries[1].parentElement.getAttribute("data-board-list"), "new");
    assert.equal(entries[2].parentElement.getAttribute("data-board-list"), "preparing");
    assert.equal(entries[0].getAttribute("aria-pressed"), "true");
    assert.equal(entries[0].querySelector("[data-status-badge]").textContent, "statusNew");
    assert.equal(entries[1].querySelector("[data-status-badge]").textContent, "statusAccepted");
    assert.equal(entries[0].querySelector("[data-order-code]").textContent, "TY-10000000000000000001");
    assert.equal(entries[0].querySelector("[data-product-summary]").textContent.indexOf("1× Soup"), 0);
    assert.equal(entries[0].querySelector("[data-product-summary]").textContent.indexOf("Tea"), -1);
    assert.equal(entries[0].querySelector("[data-product-summary]").textContent.indexOf("no onion"), -1);
    assert.equal(h.host.textContent.indexOf("Secret Customer"), -1);
    assert.equal(h.host.querySelectorAll("[data-focus-select] [data-order-action]").length, 0);
    assert.equal(h.browser.getSelectedOrderId(), guid(1));
    assert.equal(loads, 1);
    await flush();
    h.browser.renderSnapshot(body);
    assert.equal(loads, 1);
    const summary = entries[0].querySelector("[data-product-summary]");
    body.serverTimeUtc = "2026-09-23T08:01:00Z";
    const timed = h.browser.renderSnapshot(body);
    assert.equal(timed.changed, 0);
    assert.equal(timed.timeUpdates, 3);
    assert.equal(entries[0].querySelector("[data-product-summary]"), summary);
    assert.equal(loads, 1);

    const header = h.doc.createElement("button");
    h.doc.body.appendChild(header);
    header.focus();
    body.orders.unshift({
      id: guid(9),
      displayNumber: "NEW-1",
      platform: "GetirYemek",
      status: "New",
      receivedAtUtc: "2026-09-23T07:59:00Z",
      deliveredAtUtc: null,
      customerName: "Incoming",
      totalAmount: 5,
      items: [{ productName: "Water", quantity: 1, notes: null }]
    });
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.getSelectedOrderId(), guid(1));
    assert.equal(h.doc.activeElement, header);
    assert.equal(loads, 1);
  } finally { h.restore(); }
});

test("Focus keeps a status change selected, moves its group, and drops outdated actions", async () => {
  let calls = 0;
  let second;
  const h = boardHarness(true, {
    liveDetailModal: {
      loadPanel: function (container) {
        calls += 1;
        if (calls === 1) {
          paintDetail(container, "approve");
          return Promise.resolve(true);
        }
        return new Promise(function (resolve) { second = { container: container, resolve: resolve }; });
      },
      cancelPanel: function () {}
    }
  });
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[1].status = "Accepted";
    h.view("focus");
    h.browser.renderSnapshot(body);
    await flush();
    const selected = h.host.querySelectorAll("[data-focus-select]")[0];
    assert.equal(h.host.querySelector("[data-order-action]").getAttribute("data-order-action"), "approve");
    body.orders[0].status = "Preparing";
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.getSelectedOrderId(), guid(1));
    assert.equal(selected.parentElement.getAttribute("data-board-list"), "preparing");
    assert.equal(selected.getAttribute("aria-pressed"), "true");
    assert.equal(h.host.querySelector("[data-order-action]"), null);
    assert.equal(calls, 2);
    paintDetail(second.container, "mark-ready");
    second.resolve(true);
    await flush();
    assert.equal(h.host.querySelector("[data-order-action]").getAttribute("data-order-action"), "mark-ready");
  } finally { h.restore(); }
});

test("Focus detail failure can be retried without treating the failed snapshot as fresh", async () => {
  let calls = 0;
  const h = boardHarness(true, {
    liveDetailModal: {
      loadPanel: function (container) {
        calls += 1;
        while (container.firstChild) container.removeChild(container.firstChild);
        if (calls === 1) {
          const retry = document.createElement("button");
          retry.setAttribute("data-focus-detail-retry", "");
          retry.textContent = "detailRetry";
          container.appendChild(retry);
          return Promise.resolve(false);
        }
        paintDetail(container, "approve");
        return Promise.resolve(true);
      },
      cancelPanel: function () {}
    }
  });
  try {
    const body = snapshot([guid(1)]);
    h.view("focus");
    h.browser.renderSnapshot(body);
    await flush();
    assert.equal(h.host.querySelector("[data-focus-detail-retry]").textContent, "detailRetry");
    h.browser.renderSnapshot(body);
    assert.equal(calls, 1);
    h.browser.retrySelectedDetail();
    await flush();
    assert.equal(calls, 2);
    assert.equal(h.host.querySelector("[data-order-action]").getAttribute("data-order-action"), "approve");
  } finally { h.restore(); }
});

test("removing the selected order cancels its detail request and ignores a late response", async () => {
  const pending = [];
  const h = boardHarness(true, {
    liveDetailModal: wiredDetailClient(pending)
  });
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[0].customerName = "Secret Customer";
    h.view("focus");
    h.browser.renderSnapshot(body);
    const detail = h.host.querySelector("[data-focus-detail]");
    assert.equal(pending.length, 1);
    body.orders = [body.orders[1]];
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.getSelectedOrderId(), null);
    assert.equal(pending[0].signal.aborted, true);
    assert.equal(detail.querySelector(".wasla-live-empty").getAttribute("data-live-empty"), "selectOrder");
    assert.equal(detail.textContent.indexOf("selectOrder") >= 0, true);
    assert.equal(h.host.querySelectorAll("[data-focus-select]").length, 1);
    assert.equal(h.host.textContent.indexOf("Secret Customer"), -1);
    const placeholder = detail.querySelector(".wasla-live-empty");
    pending[0].resolve({ ok: true, text: async () => "<button data-order-action='approve'>Late</button>" });
    await flush();
    assert.equal(detail.querySelector(".wasla-live-empty"), placeholder);
    assert.equal(detail.querySelector("[data-order-action]"), null);
    assert.equal(detail.innerHTML, undefined);
    assert.equal(h.browser.getSelectedOrderId(), null);
  } finally { h.restore(); }
});

test("leaving Focus cancels a pending detail request", async () => {
  const pending = [];
  const notes = [];
  let fetches = 0;
  const previousFetch = global.fetch;
  global.fetch = function () {
    fetches += 1;
    return Promise.reject(new Error("snapshot"));
  };
  const h = boardHarness(true, {
    audio: { showBrowserNotificationIfAllowed: function () { notes.push("notify"); } },
    liveDetailModal: wiredDetailClient(pending)
  });
  try {
    const body = snapshot([guid(1), guid(2)]);
    h.browser.renderSnapshot(body);
    h.view("list");
    h.browser.renderSnapshot(body);
    h.view("focus");
    h.browser.renderSnapshot(body);
    const detail = h.host.querySelector("[data-focus-detail]");
    assert.equal(pending.length, 1);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0);
    assert.equal(h.host.querySelectorAll(".wasla-live-groups").length, 0);
    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(pending[0].signal.aborted, true);
    assert.equal(h.host.querySelector(".wasla-live-focus"), null);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 1);
    pending[0].resolve({ ok: true, text: async () => "<p>LATE</p>" });
    await flush();
    assert.equal(detail.innerHTML, undefined);
    assert.equal(fetches, 0);
    assert.deepEqual(notes, []);
  } finally {
    global.fetch = previousFetch;
    h.restore();
  }
});

test("a late detail response cannot replace a newer Focus selection", async () => {
  const doc = fakeDocument();
  const container = doc.createElement("div");
  container.isConnected = true;
  const pending = [];
  const client = detailModal.createDetailClient({
    fetch: function (url, options) {
      return new Promise(function (resolve) {
        pending.push({ url: url, signal: options.signal, resolve: resolve });
      });
    },
    getMessage: function (key) { return key; },
    urlFor: function (id) { return "/panel/" + id; },
    document: doc
  });
  const first = client.load(container, guid(1), "focus");
  const second = client.load(container, guid(2), "focus");
  assert.equal(pending[0].signal.aborted, true);
  pending[0].resolve({ ok: true, text: async () => "<p>AAA</p>" });
  assert.equal(await first, false);
  assert.notEqual(container.innerHTML, "<p>AAA</p>");
  pending[1].resolve({ ok: true, text: async () => "<p>BBB</p>" });
  assert.equal(await second, true);
  assert.equal(container.innerHTML, "<p>BBB</p>");
});

test("detail failure renders a retry control for the current Focus selection", async () => {
  const doc = fakeDocument();
  const container = doc.createElement("div");
  container.isConnected = true;
  let fail = true;
  const client = detailModal.createDetailClient({
    fetch: function () {
      return Promise.resolve(fail
        ? { ok: false, status: 503, text: async () => "" }
        : { ok: true, text: async () => "<p>ready</p>" });
    },
    getMessage: function (key) { return key; },
    urlFor: function (id) { return "/panel/" + id; },
    document: doc
  });
  assert.equal(await client.load(container, guid(1), "focus"), false);
  assert.equal(container.querySelector("[data-focus-detail-retry]").textContent, "detailRetry");
  fail = false;
  assert.equal(await client.load(container, guid(1), "focus"), true);
  assert.equal(container.innerHTML, "<p>ready</p>");
});

test("narrow Focus shows the queue until an order is chosen", () => {
  const h = boardHarness(true, {
    global: { matchMedia: function () { return { matches: true, addEventListener: function () {}, addListener: function () {} }; } },
    liveDetailModal: {
      loadPanel: function (container) {
        paintDetail(container, "approve");
        return Promise.resolve(true);
      },
      cancelPanel: function () {}
    }
  });
  try {
    const body = snapshot([guid(1)]);
    h.view("focus");
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.getSelectedOrderId(), null);
    assert.equal(h.host.querySelector("[data-focus-detail] .wasla-live-empty").getAttribute("data-live-empty"), "selectOrder");
    assert.equal(h.host.querySelectorAll("[data-focus-select]").length, 1);
    assert.equal(h.host.querySelector(".wasla-live-focus").className.indexOf("wasla-live-focus--show-detail"), -1);
    h.browser.selectOrder(guid(1), true);
    assert.equal(h.host.querySelector(".wasla-live-focus").className.indexOf("wasla-live-focus--show-detail") >= 0, true);
    const opener = h.host.querySelector("[data-focus-select]");
    h.doc.body.focus();
    h.browser.showQueue();
    assert.equal(h.host.querySelector(".wasla-live-focus").className.indexOf("wasla-live-focus--show-detail"), -1);
    assert.equal(h.doc.activeElement, opener);
  } finally { h.restore(); }
});

test("an empty Focus snapshot uses one branded empty state and the next order restores Focus", () => {
  const h = boardHarness();
  try {
    h.view("focus");
    h.browser.renderSnapshot(snapshot([]));
    const empty = h.host.querySelector(".wasla-live-empty");
    assert.equal(empty.getAttribute("data-live-empty"), "none");
    assert.equal(empty.parentElement, h.host);
    assert.equal(empty.textContent.indexOf("noDisplayableOrders") >= 0, true);
    assert.equal(h.host.querySelectorAll(".wasla-live-empty").length, 1);
    assert.equal(h.host.querySelector(".wasla-live-focus"), null);
    assert.equal(h.host.querySelector("[data-board-column]"), null);
    h.browser.renderSnapshot(snapshot([]));
    assert.equal(h.host.querySelector(".wasla-live-empty"), empty);
    h.browser.renderSnapshot(snapshot([guid(1)]));
    assert.equal(h.host.querySelector(".wasla-live-empty"), null);
    assert.equal(h.host.querySelectorAll("[data-focus-select]").length, 1);
    assert.equal(h.browser.getSelectedOrderId(), guid(1));
  } finally { h.restore(); }
});

function groupIds(host, key) {
  const cards = host.querySelectorAll('[data-board-list="' + key + '"] .wasla-live-screen-card');
  const entries = cards.length ? cards : host.querySelectorAll('[data-board-list="' + key + '"] [data-focus-select]');
  return entries.map(function (el) { return el.getAttribute("data-order-id"); });
}

test("New and Accepted keep received-time order across polls, views, and group changes", () => {
  const h = boardHarness(true, {
    liveDetailModal: {
      loadPanel: function () { return Promise.resolve(true); },
      cancelPanel: function () {}
    }
  });
  try {
    const body = snapshot([1, 2, 3, 4, 5, 6, 7].map(guid));
    const received = {
      [guid(1)]: "2026-09-23T07:50:00Z",
      [guid(2)]: "2026-09-23T07:40:00Z",
      [guid(3)]: "2026-09-23T07:30:00Z",
      [guid(4)]: "2026-09-23T07:30:00Z",
      [guid(5)]: "2026-09-23T07:10:00Z",
      [guid(6)]: "2026-09-23T07:25:00Z",
      [guid(7)]: "2026-09-23T07:05:00Z"
    };
    body.orders.forEach(function (order) { order.receivedAtUtc = received[order.id]; });
    body.orders[0].status = "New";
    body.orders[1].status = "New";
    body.orders[2].status = "Accepted";
    body.orders[3].status = "New";
    body.orders[4].status = "New";
    body.orders[5].status = "Preparing";
    body.orders[6].status = "Preparing";
    // Status-sorted input would place the Accepted order after every New order.
    body.orders = [0, 1, 3, 4, 2, 5, 6].map(function (index) { return body.orders[index]; });
    h.browser.renderSnapshot(body);
    const newestFirst = [guid(1), guid(2), guid(3), guid(4), guid(5)];
    assert.deepEqual(groupIds(h.host, "new"), newestFirst, "mixed New and Accepted follow received time, then id");
    assert.deepEqual(groupIds(h.host, "preparing"), [guid(6), guid(7)]);

    const cardById = function (id) {
      return h.host.querySelectorAll(".wasla-live-screen-card").find(function (el) {
        return el.getAttribute("data-order-id") === id;
      });
    };
    const middle = cardById(guid(2));
    const item = middle.querySelector(".wasla-live-screen-card__item");
    middle.querySelector("[data-order-action]").focus();
    const second = body.orders.find(function (order) { return order.id === guid(2); });
    second.status = "Accepted";
    body.orders = body.orders.filter(function (order) { return order.id !== guid(2); }).concat([second]);
    const accepted = h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), newestFirst);
    assert.equal(accepted.moved, 0);
    assert.equal(cardById(guid(2)), middle);
    assert.equal(middle.querySelector(".wasla-live-screen-card__item"), item);
    assert.equal(middle.querySelector("[data-status-badge]").textContent, "statusAccepted");
    assert.equal(middle.querySelector("[data-order-action]").getAttribute("data-order-action"), "start-preparing");
    assert.equal(h.doc.activeElement, middle.querySelector("[data-order-action]"));

    const first = body.orders.find(function (order) { return order.id === guid(1); });
    first.status = "Accepted";
    body.orders = [guid(5), guid(4), guid(3), guid(2), guid(1), guid(7), guid(6)].map(function (id) {
      return body.orders.find(function (order) { return order.id === id; });
    });
    h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), newestFirst, "a later status-sorted snapshot does not reshuffle the group");

    const preparing = body.orders.find(function (order) { return order.id === guid(4); });
    preparing.status = "Preparing";
    h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), [guid(1), guid(2), guid(3), guid(5)]);
    assert.deepEqual(groupIds(h.host, "preparing"), [guid(4), guid(6), guid(7)]);

    body.orders.push({
      id: guid(8),
      displayNumber: "NEW",
      platform: "TrendyolYemek",
      status: "New",
      receivedAtUtc: "2026-09-23T07:55:00Z",
      deliveredAtUtc: null,
      customerName: "Incoming",
      totalAmount: 5,
      items: [{ productName: "Water", quantity: 1, notes: null }]
    });
    body.orders = body.orders.filter(function (order) { return order.id !== guid(3); });
    h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), [guid(8), guid(1), guid(2), guid(5)]);
    assert.deepEqual(groupIds(h.host, "preparing"), [guid(4), guid(6), guid(7)]);

    const boardNew = groupIds(h.host, "new");
    const boardPreparing = groupIds(h.host, "preparing");
    h.view("list");
    h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), boardNew);
    assert.deepEqual(groupIds(h.host, "preparing"), boardPreparing);
    h.view("focus");
    h.browser.renderSnapshot(body);
    assert.deepEqual(groupIds(h.host, "new"), boardNew);
    assert.deepEqual(groupIds(h.host, "preparing"), boardPreparing);
    const queue = h.host.querySelector(".wasla-live-focus__queue");
    queue.scrollTop = 48;
    const entry = h.host.querySelectorAll("[data-focus-select]").find(function (el) {
      return el.getAttribute("data-order-id") === guid(8);
    });
    h.browser.selectOrder(guid(8), true);
    const focused = body.orders.find(function (order) { return order.id === guid(8); });
    focused.status = "Accepted";
    body.orders = body.orders.filter(function (order) { return order !== focused; }).concat([focused]);
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.getSelectedOrderId(), guid(8));
    assert.deepEqual(groupIds(h.host, "new"), [guid(8), guid(1), guid(2), guid(5)]);
    assert.equal(entry.parentElement !== null, true);
    assert.equal(entry.getAttribute("data-order-status"), "Accepted");
    assert.equal(entry.parentElement.getAttribute("data-board-list"), "new");
    assert.equal(queue.scrollTop, 48);
  } finally { h.restore(); }
});

function wiredDetailClient(pending) {
  let client;
  return {
    loadPanel: function (container, orderId, consumer) {
      client = client || detailModal.createDetailClient({
        fetch: function (url, options) {
          return new Promise(function (resolve) {
            pending.push({ url: url, signal: options.signal, resolve: resolve });
          });
        },
        getMessage: function (key) { return key; },
        urlFor: function (id) { return "/panel/" + id; },
        document: document
      });
      return client.load(container, orderId, consumer);
    },
    cancelPanel: function (consumer) {
      if (client) client.cancel(consumer);
    }
  };
}

function attentionClock(start) {
  let now = start;
  let seq = 1;
  const timers = [];
  return {
    now: function () { return now; },
    Date: { now: function () { return now; } },
    setTimeout: function (fn, ms) {
      const id = seq++;
      timers.push({ id: id, fn: fn, at: now + ms, cleared: false, ran: false });
      return id;
    },
    clearTimeout: function (id) {
      timers.forEach(function (timer) {
        if (timer.id === id) timer.cleared = true;
      });
    },
    pending: function () {
      return timers.filter(function (timer) { return !timer.cleared && !timer.ran; }).length;
    },
    advance: async function (ms) {
      const target = now + ms;
      while (true) {
        const due = timers
          .filter(function (timer) { return !timer.cleared && !timer.ran && timer.at <= target; })
          .sort(function (a, b) { return a.at - b.at; });
        if (!due.length) break;
        now = due[0].at;
        due[0].ran = true;
        due[0].fn();
        await flush();
      }
      now = target;
    }
  };
}

function attentionHarness(options) {
  const previous = globalThis.document;
  const doc = fakeDocument();
  globalThis.document = doc;
  const host = doc.createElement("div");
  host.id = "ordersLiveScreenHost";
  doc.body.appendChild(host);
  let view = "list";
  const clock = options.clock;
  const vm = require("node:vm");
  const fs = require("node:fs");
  const source = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-table.js"), "utf8");
  const O = {
    opts: {
      displayCulture: "tr-TR",
      timeZoneId: "Europe/Istanbul",
      canManageOrders: true,
      pageMode: "liveDisplay"
    },
    state: {
      notificationSettings: {
        newOrderHighlightDurationSeconds: options.highlightSeconds,
        newOrderHighlightColor: options.highlightColor || "yellow",
        newOrderHighlightBehavior: options.highlightBehavior || "fade"
      }
    },
    liveView: { getView: function () { return view; } },
    getMessage: function (key) { return key; },
    liveDetailModal: options.liveDetailModal
  };
  vm.runInNewContext(source, {
    window: { WaslaOrders: O },
    document: doc,
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout,
    Date: clock.Date
  });
  clock.matchMedia = options.matchMedia || function () { return { matches: false }; };
  const browser = store.attachBrowser(O, clock, store);
  function row(id) {
    return host.querySelectorAll(".wasla-live-screen-card").find(function (card) {
      return card.getAttribute("data-order-id") === id;
    });
  }
  return {
    doc: doc,
    host: host,
    browser: browser,
    table: O.table,
    clock: clock,
    row: row,
    view: function (value) { view = value; },
    restore: function () { globalThis.document = previous; }
  };
}

function notedSnapshot(ids, notes) {
  const body = snapshot(ids);
  body.orders.forEach(function (order, index) {
    order.customerNote = notes[index].customerNote;
    order.customerAddress = "Kadıköy";
    order.items[0].notes = notes[index].itemNote;
  });
  return body;
}

function hasClass(el, name) {
  return !!el && el.className.split(/\s+/).indexOf(name) >= 0;
}

test("Board uses the shared highlight lifecycle without a second timer", async function () {
  const clock = attentionClock(4000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 10,
    highlightColor: "orange",
    highlightBehavior: "pulse"
  });
  try {
    const id = guid(21);
    const body = notedSnapshot([id], [{ customerNote: "Kapıya bırakın.", itemNote: "No onions" }]);
    h.view("board");
    h.browser.renderSnapshot(body);
    const baseline = h.row(id);
    assert.equal(baseline.getAttribute("data-live-layout"), "board");
    assert.equal(hasClass(baseline, "order-row-new"), false);
    assert.equal(baseline.querySelector("[data-order-note-text]").textContent, "Kapıya bırakın.");
    assert.equal(hasClass(baseline.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    h.table.markOrdersAsRecentlyNew([id]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    assert.equal(hasClass(h.row(id), "order-row-new"), true);
    assert.equal(hasClass(h.row(id), "color-orange"), true);
    assert.equal(hasClass(h.row(id), "behavior-pulse"), true);
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    const pending = h.clock.pending();
    const node = h.row(id);
    h.browser.renderSnapshot(body);
    assert.equal(h.row(id), node);
    assert.equal(hasClass(node, "order-row-new"), true);
    assert.equal(h.clock.pending(), pending);
    assert.equal(h.host.querySelectorAll("[data-order-id='" + id + "'].wasla-live-screen-card").length, 1);

    body.orders[0].status = "Preparing";
    h.browser.renderSnapshot(body);
    assert.equal(h.row(id), node, "a status move reuses the board card");
    assert.equal(h.row(id).getAttribute("data-order-status"), "Preparing");
    assert.equal(hasClass(node, "order-row-new"), true);
    assert.equal(hasClass(node, "behavior-pulse"), true);
    assert.equal(h.clock.pending(), pending);
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, 1);

    h.view("list");
    h.browser.renderSnapshot(body);
    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(hasClass(h.row(id), "order-row-new"), true);
    assert.equal(hasClass(h.row(id), "color-orange"), true);
    assert.equal(h.clock.pending(), pending, "switching views must not restart the highlight timer");
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    await clock.advance(10029);
    assert.equal(hasClass(h.row(id), "order-row-new"), true);
    await clock.advance(1);
    assert.equal(hasClass(h.row(id), "order-row-new"), false);
    assert.equal(hasClass(h.row(id), "color-orange"), false);

    h.view("list");
    h.browser.renderSnapshot(body);
    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(hasClass(h.row(id), "order-row-new"), false);
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card").length, 1);

    const css = require("node:fs").readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");
    assert.match(css, /\.wasla-live-screen-host--board \.wasla-live-screen-card\.order-row-new\.color-orange\s*\{[^}]*background-color:\s*#ffe4d1/);
    assert.match(css, /\.wasla-live-screen-host--board \.wasla-live-screen-card\.order-row-new\.behavior-pulse\s*\{[^}]*animation:\s*orderRowPulseBg/);
    assert.match(css, /@media \(prefers-reduced-motion:\s*reduce\)[\s\S]*\.wasla-live-screen-host--board \.wasla-live-screen-card\.order-row-new\.behavior-pulse[\s\S]*animation:\s*none !important/);
  } finally { h.restore(); }
});

test("a custom highlight color reaches the reused Board card", async function () {
  const clock = attentionClock(5000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 20,
    highlightColor: "#c47a3a",
    highlightBehavior: "borderGlow"
  });
  try {
    const id = guid(22);
    h.view("board");
    h.browser.renderSnapshot(snapshot([id]));
    h.table.markOrdersAsRecentlyNew([id]);
    h.table.applyNewOrderVisualState();
    const card = h.row(id);
    assert.equal(hasClass(card, "color-custom"), true);
    assert.equal(hasClass(card, "behavior-border-glow"), true);
    assert.equal(card.style.getPropertyValue("--new-order-highlight-bg"), "rgba(196,122,58,0.45)");
    assert.equal(card.style.getPropertyValue("--new-order-highlight-border"), "#c47a3a");
    h.browser.renderSnapshot(snapshot([id]));
    assert.equal(h.row(id), card);
    assert.equal(card.style.getPropertyValue("--new-order-highlight-border"), "#c47a3a");
  } finally { h.restore(); }
});

test("Board order note stays separate and pulses only after the shared card highlight", async function () {
  const clock = attentionClock(6000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 10,
    highlightColor: "orange",
    highlightBehavior: "pulse"
  });
  try {
    const noted = guid(31);
    const itemOnly = guid(32);
    const both = guid(33);
    const neither = guid(34);
    const fresh = guid(35);
    const body = notedSnapshot([noted, itemOnly, both, neither], [
      { customerNote: "Kapıya bırakın.", itemNote: null },
      { customerNote: "   ", itemNote: "No onions" },
      { customerNote: "Zili çalmayın.", itemNote: "No onions" },
      { customerNote: null, itemNote: null }
    ]);
    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(h.row(noted).querySelector("[data-order-note-text]").textContent, "Kapıya bırakın.");
    assert.equal(h.row(noted).querySelector(".wasla-live-screen-card__note"), null);
    assert.equal(h.row(itemOnly).querySelector("[data-order-note]"), null);
    assert.equal(h.row(itemOnly).querySelector(".wasla-live-screen-card__note").textContent, "No onions");
    const bothCard = h.row(both);
    const orderBlock = bothCard.querySelector("[data-order-note]");
    const itemBlock = bothCard.querySelector(".wasla-live-screen-card__note");
    assert.equal(orderBlock.querySelector("[data-order-note-text]").textContent, "Zili çalmayın.");
    assert.equal(itemBlock.textContent, "No onions");
    assert.equal(orderBlock.contains(itemBlock), false);
    assert.equal(itemBlock.contains(orderBlock), false);
    assert.equal(h.row(neither).querySelector("[data-order-note]"), null);
    assert.equal(h.row(neither).querySelector(".wasla-live-screen-card__note"), null);
    assert.equal(hasClass(orderBlock, "wasla-order-note-attention"), false);
    await clock.advance(60000);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle");

    body.orders.push(Object.assign({}, body.orders[0], {
      id: fresh,
      customerNote: "Uzun sipariş notu satır satır sarmalı ve kartı bozmamalı.",
      items: [{ productName: "Lahmacun", quantity: 1, notes: "No onions" }]
    }));
    h.browser.renderSnapshot(body);
    assert.equal(hasClass(h.row(fresh), "order-row-new"), false);
    h.table.markOrdersAsRecentlyNew([fresh]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    const freshNote = h.row(fresh).querySelector("[data-order-note]");
    assert.equal(hasClass(h.row(fresh), "order-row-new"), true);
    assert.equal(hasClass(h.row(fresh), "color-orange"), true);
    assert.equal(hasClass(freshNote, "wasla-order-note-attention"), false);
    assert.equal(h.row(fresh).querySelector(".wasla-live-screen-card__note").textContent, "No onions");

    const pending = h.clock.pending();
    const node = h.row(fresh);
    h.browser.renderSnapshot(body);
    assert.equal(h.row(fresh), node);
    assert.equal(hasClass(node.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.clock.pending(), pending);

    await clock.advance(10029);
    assert.equal(hasClass(h.row(fresh), "order-row-new"), true);
    assert.equal(hasClass(h.row(fresh).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(1);
    assert.equal(hasClass(h.row(fresh), "order-row-new"), false);
    assert.equal(hasClass(h.row(fresh).querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(fresh).endsAt - clock.now(), 10000);

    const duringPulse = h.clock.pending();
    body.orders[body.orders.length - 1].status = "Preparing";
    h.browser.renderSnapshot(body);
    assert.equal(h.row(fresh), node, "a column move reuses the pulsing card");
    assert.equal(h.row(fresh).getAttribute("data-order-status"), "Preparing");
    assert.equal(hasClass(node.querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    assert.equal(h.clock.pending(), duringPulse);
    h.browser.renderSnapshot(body);
    assert.equal(h.clock.pending(), duringPulse);

    await clock.advance(9999);
    assert.equal(hasClass(h.row(fresh).querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    await clock.advance(1);
    assert.equal(hasClass(h.row(fresh).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.row(fresh).querySelector("[data-order-note-text]").textContent.indexOf("Uzun sipariş notu") >= 0, true);
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");

    h.view("list");
    h.browser.renderSnapshot(body);
    h.view("board");
    h.browser.renderSnapshot(body);
    assert.equal(hasClass(h.row(fresh), "order-row-new"), false);
    assert.equal(hasClass(h.row(fresh).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");
    assert.equal(h.host.querySelectorAll(".wasla-live-screen-card[data-order-id='" + fresh + "']").length, 1);

    const css = require("node:fs").readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");
    assert.match(css, /\.wasla-live-board \.wasla-live-board-order-note\s*\{[^}]*background:\s*#fff7ef/);
    assert.match(css, /\.wasla-live-board \.wasla-live-board-order-note__text\s*\{[^}]*font-size:\s*\.6875rem/);
    assert.match(css, /\.wasla-live-board \.wasla-live-board-order-note__text\s*\{[^}]*-webkit-line-clamp:\s*2/);
    assert.match(css, /\.wasla-live-board \[data-order-note\]\.wasla-order-note-attention\s*\{[^}]*animation-name:\s*waslaOrderNoteAttention/);
    assert.equal(/\.wasla-live-board \.wasla-live-board-order-note\s*\{[^}]*(#0d6efd|#6ea8fe)/i.test(css), false);
  } finally { h.restore(); }
});

test("reduced motion keeps the Board order note static after the card highlight", async function () {
  const clock = attentionClock(7000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 2,
    matchMedia: function () { return { matches: true }; }
  });
  try {
    const id = guid(36);
    h.view("board");
    h.browser.renderSnapshot(notedSnapshot([id], [{ customerNote: "Temassız teslimat.", itemNote: null }]));
    h.table.markOrdersAsRecentlyNew([id]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    await clock.advance(2029);
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(1);
    const block = h.row(id).querySelector("[data-order-note]");
    assert.equal(hasClass(h.row(id), "order-row-new"), false);
    assert.equal(hasClass(block, "wasla-order-note-attention"), false);
    assert.equal(hasClass(block, "wasla-order-note-attention-static"), true);
    assert.equal(block.querySelector("[data-order-note-text]").textContent, "Temassız teslimat.");
    await clock.advance(10000);
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention-static"), false);
    assert.equal(h.row(id).querySelector("[data-order-note-text]").textContent, "Temassız teslimat.");
  } finally { h.restore(); }
});

test("saved highlight color, style, and duration reach a new list row only", async function () {
  const clock = attentionClock(3000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 12,
    highlightColor: "blue",
    highlightBehavior: "pulse"
  });
  try {
    const noted = guid(11);
    const plain = guid(12);
    const body = notedSnapshot([noted, plain], [
      { customerNote: "Kapıya bırakın.", itemNote: null },
      { customerNote: "  ", itemNote: "No onions" }
    ]);
    h.browser.renderSnapshot(body);
    assert.equal(hasClass(h.row(noted), "order-row-new"), false);
    assert.equal(hasClass(h.row(plain), "order-row-new"), false);

    h.table.markOrdersAsRecentlyNew([noted, plain]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    assert.equal(hasClass(h.row(noted), "order-row-new"), true);
    assert.equal(hasClass(h.row(noted), "color-blue"), true);
    assert.equal(hasClass(h.row(noted), "behavior-pulse"), true);
    assert.equal(hasClass(h.row(plain), "behavior-pulse"), true);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    const pending = h.clock.pending();
    const node = h.row(noted);
    h.browser.renderSnapshot(body);
    assert.equal(h.row(noted), node);
    assert.equal(hasClass(node, "order-row-new"), true);
    assert.equal(hasClass(node, "behavior-pulse"), true);
    assert.equal(h.clock.pending(), pending);

    await clock.advance(12029);
    assert.equal(hasClass(h.row(noted), "order-row-new"), true);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(1);
    assert.equal(hasClass(h.row(noted), "order-row-new"), false);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    assert.equal(h.row(plain).querySelector("[data-order-note]"), null);
    assert.equal(h.browser.orderNoteAttentionState(plain).phase, "skipped");
  } finally { h.restore(); }
});

test("order-note attention waits for the configured row highlight and then runs for 10 seconds", async function () {
  const clock = attentionClock(1000000);
  const h = attentionHarness({ clock: clock, highlightSeconds: 8 });
  try {
    const plain = guid(1);
    const noted = guid(2);
    const both = guid(3);
    const body = notedSnapshot([plain, noted, both], [
      { customerNote: "   ", itemNote: "No onions" },
      { customerNote: "Zili çalmayın, arayın.", itemNote: null },
      { customerNote: "Kapıya bırakabilirsiniz.", itemNote: "No onions" }
    ]);
    h.browser.renderSnapshot(body);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle");
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(60000);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle", "baseline rows do not start a note effect");

    h.table.markOrdersAsRecentlyNew([plain, noted, both]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    assert.equal(hasClass(h.row(noted), "order-row-new"), true);
    assert.equal(hasClass(h.row(plain), "order-row-new"), true);
    assert.equal(h.row(noted).querySelector("[data-order-note]") !== null, true);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    const rowNode = h.row(noted);
    const highlightClass = rowNode.className;
    const pendingHighlight = h.clock.pending();
    const duringHighlight = h.browser.renderSnapshot(body);
    assert.equal(duringHighlight.unchanged, 3);
    assert.equal(h.row(noted), rowNode);
    assert.equal(rowNode.className, highlightClass, "an unchanged poll must not restart the row highlight");
    assert.equal(hasClass(rowNode.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle");
    assert.equal(h.clock.pending(), pendingHighlight);

    rowNode.className = "wasla-live-screen-card wasla-live-list-row";
    h.browser.renderSnapshot(body);
    assert.equal(h.row(noted), rowNode, "reconciliation reuses the list row");
    assert.equal(hasClass(rowNode, "order-row-new"), true);
    assert.equal(hasClass(rowNode, "color-yellow"), true);
    assert.equal(hasClass(rowNode, "behavior-fade"), true);
    assert.equal(hasClass(rowNode.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle");
    assert.equal(h.clock.pending(), pendingHighlight, "restoring a stripped highlight must not add a timer");
    assert.equal(hasClass(h.row(both).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.row(plain).querySelector("[data-order-note]"), null);

    await clock.advance(8029);
    assert.equal(hasClass(h.row(noted), "order-row-new"), true);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);

    await clock.advance(1);
    assert.equal(hasClass(h.row(noted), "order-row-new"), false);
    assert.equal(hasClass(h.row(plain), "order-row-new"), false);
    assert.equal(h.browser.orderNoteAttentionState(plain).phase, "skipped");
    assert.equal(h.row(plain).querySelector("[data-order-note]"), null);
    const notedBlock = h.row(noted).querySelector("[data-order-note]");
    const bothBlock = h.row(both).querySelector("[data-order-note]");
    assert.equal(hasClass(notedBlock, "wasla-order-note-attention"), true);
    assert.equal(hasClass(bothBlock, "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(noted).endsAt - clock.now(), 10000);
    assert.equal(h.row(both).querySelector(".wasla-live-screen-card__items").contains(bothBlock), false);
    assert.equal(h.row(both).querySelector(".wasla-live-screen-card__item").contains(h.row(both).querySelector(".wasla-live-list-row__item-note")), true);
    assert.equal(hasClass(h.row(both).querySelector(".wasla-live-list-row__item-note"), "wasla-order-note-attention"), false);
    assert.equal(h.row(plain).querySelector(".wasla-live-list-row__item-note").textContent.indexOf("No onions") >= 0, true);

    const pendingDuring = h.clock.pending();
    const unchanged = h.browser.renderSnapshot(body);
    assert.equal(unchanged.changed, 0);
    assert.equal(h.row(noted).querySelector("[data-order-note]"), notedBlock);
    assert.equal(hasClass(notedBlock, "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(noted).endsAt - clock.now(), 10000);
    assert.equal(h.clock.pending(), pendingDuring);

    await clock.advance(9999);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    await clock.advance(1);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "done");
    assert.equal(notedBlock.querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");

    h.table.markOrdersAsRecentlyNew([noted]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    await clock.advance(8030);
    assert.equal(hasClass(h.row(noted).querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "done");
  } finally { h.restore(); }
});

test("view changes and removed orders do not restart or throw the order-note effect", async function () {
  const clock = attentionClock(5000000);
  const h = attentionHarness({ clock: clock, highlightSeconds: 4 });
  try {
    const id = guid(4);
    const body = notedSnapshot([id], [{ customerNote: "Sosları ayrı gönderin.", itemNote: "No onions" }]);
    h.browser.renderSnapshot(body);
    h.table.markOrdersAsRecentlyNew([id]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    await clock.advance(4030);
    const started = h.browser.orderNoteAttentionState(id).endsAt;
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention"), true);

    await clock.advance(2500);
    h.view("board");
    h.browser.renderSnapshot(body);
    const boardNote = h.host.querySelector("[data-order-note]");
    assert.equal(boardNote.querySelector("[data-order-note-text]").textContent, "Sosları ayrı gönderin.");
    assert.equal(hasClass(boardNote, "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(id).endsAt, started);
    assert.equal(h.host.querySelector(".wasla-live-screen-card__items").contains(boardNote), false);
    h.view("focus");
    h.browser.renderSnapshot(body);
    assert.equal(h.host.querySelector("[data-order-note]"), null);
    h.view("list");
    h.browser.renderSnapshot(body);
    const resumed = h.row(id).querySelector("[data-order-note]");
    assert.equal(hasClass(resumed, "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(id).endsAt, started);
    assert.equal(resumed.getAttribute("data-order-note-attention-delay").indexOf("-") === 0, true);
    assert.equal(h.row(id).querySelector(".wasla-live-list-row__item-note").textContent.indexOf("No onions") >= 0, true);

    body.orders = [];
    h.browser.renderSnapshot(body);
    await clock.advance(20000);
    assert.equal(h.host.querySelector("[data-order-note]"), null);
    assert.equal(h.browser.orderNoteAttentionState(id).phase, "done");
  } finally { h.restore(); }
});

test("reduced motion uses static order-note emphasis without the pulse class", async function () {
  const clock = attentionClock(8000000);
  const css = require("node:fs").readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");
  const pulseAt = css.indexOf("@keyframes waslaOrderNoteAttention {");
  const pulseEnd = css.indexOf("@keyframes waslaOrderNoteAttentionDark");
  const pulse = css.slice(pulseAt, pulseEnd);
  assert.equal(pulseAt >= 0, true);
  assert.equal(/visibility\s*:\s*hidden/.test(pulse), false);
  assert.equal(/opacity\s*:/.test(pulse), false);
  assert.equal(/color-mix\(/.test(pulse), false);
  assert.match(css, /\.wasla-live-list-row__order-note-text\s*\{[^}]*font-size:\s*\.75rem/);
  assert.equal(/#(?:0d6efd|6ea8fe|b6d4fe|cfe2ff|e8f2ff)/i.test(pulse), false);
  assert.match(css, /\.wasla-live-screen-host--list \.wasla-live-screen-card\.order-row-new\.color-yellow\s*\{[^}]*background-color:\s*#fff3cd/);
  assert.match(css, /\.wasla-live-screen-host--list \.wasla-live-screen-card\.order-row-new\.behavior-fade\s*\{[^}]*animation:\s*orderRowFadeBg/);
  assert.match(css, /@media \(prefers-reduced-motion:\s*reduce\)[\s\S]*\.wasla-live-screen-page \*[\s\S]*animation:\s*none !important/);
  assert.match(css, /@media \(prefers-reduced-motion:\s*reduce\)[\s\S]*\.wasla-live-screen-host--list \.wasla-live-screen-card\.order-row-new\.behavior-fade[\s\S]*animation:\s*none !important/);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 2,
    matchMedia: function () { return { matches: true }; }
  });
  try {
    const id = guid(5);
    const body = notedSnapshot([id], [{ customerNote: "Temassız teslimat rica ediyorum.", itemNote: null }]);
    h.browser.renderSnapshot(body);
    h.table.markOrdersAsRecentlyNew([id]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    await clock.advance(2030);
    const block = h.row(id).querySelector("[data-order-note]");
    assert.equal(hasClass(block, "wasla-order-note-attention"), false);
    assert.equal(hasClass(block, "wasla-order-note-attention-static"), true);
    assert.equal(block.querySelector("[data-order-note-text]").textContent, "Temassız teslimat rica ediyorum.");
    await clock.advance(10000);
    assert.equal(hasClass(h.row(id).querySelector("[data-order-note]"), "wasla-order-note-attention-static"), false);
  } finally { h.restore(); }
});

function paintFocusDetail(container, orderId) {
  while (container.firstChild) container.removeChild(container.firstChild);
  const wrap = document.createElement("div");
  wrap.className = "wasla-live-detail";
  wrap.setAttribute("data-order-id", orderId);
  const body = document.createElement("div");
  body.className = "wasla-live-detail__body";
  const products = document.createElement("section");
  products.className = "wasla-live-detail__products";
  const itemNote = document.createElement("p");
  itemNote.className = "wasla-live-detail__note";
  itemNote.textContent = "No onions";
  products.appendChild(itemNote);
  const facts = document.createElement("aside");
  facts.className = "wasla-live-detail__facts";
  const customer = document.createElement("p");
  customer.className = "wasla-live-detail__customer";
  customer.textContent = "Ada";
  const factsList = document.createElement("dl");
  factsList.className = "wasla-live-detail__fact-list";
  const payment = document.createElement("div");
  payment.setAttribute("data-payment-method", "");
  factsList.appendChild(payment);
  facts.appendChild(customer);
  facts.appendChild(factsList);
  body.appendChild(products);
  body.appendChild(facts);
  const actions = document.createElement("div");
  actions.className = "wasla-live-detail__actions";
  const button = document.createElement("button");
  button.setAttribute("data-order-action", "approve");
  actions.appendChild(button);
  wrap.appendChild(body);
  wrap.appendChild(actions);
  container.appendChild(wrap);
}

function focusDetailModal() {
  return {
    loadPanel: function (container, id) {
      paintFocusDetail(container, id);
      return Promise.resolve(true);
    },
    cancelPanel: function () {}
  };
}

function focusEntry(host, id) {
  const entries = host.querySelectorAll("[data-focus-select]");
  for (let i = 0; i < entries.length; i++) {
    if (entries[i].getAttribute("data-order-id") === id) return entries[i];
  }
  return null;
}

test("Focus renders the order note apart from the item note and omits an empty note", async function () {
  const clock = attentionClock(1000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 4,
    liveDetailModal: focusDetailModal()
  });
  try {
    const noted = guid(1);
    const plain = guid(2);
    const body = notedSnapshot([noted, plain], [
      { customerNote: "Zili çalmayın, arayın.", itemNote: "No onions" },
      { customerNote: "   ", itemNote: "No onions" }
    ]);
    h.view("focus");
    h.browser.renderSnapshot(body);
    await flush();
    const panel = h.host.querySelector(".wasla-live-detail");
    const orderNote = panel.querySelector("[data-order-note]");
    const itemNote = panel.querySelector(".wasla-live-detail__note");
    assert.equal(panel.getAttribute("data-order-id"), noted);
    assert.equal(orderNote.querySelector("[data-order-note-text]").textContent, "Zili çalmayın, arayın.");
    assert.equal(orderNote.querySelector(".wasla-live-focus-order-note__label").textContent, "orderNote");
    assert.equal(itemNote.textContent, "No onions");
    assert.equal(orderNote.contains(itemNote), false);
    assert.equal(itemNote.contains(orderNote), false);
    assert.equal(hasClass(orderNote, "wasla-live-detail__note"), false);
    const facts = panel.querySelector(".wasla-live-detail__facts");
    const paymentList = facts.querySelector(".wasla-live-detail__fact-list");
    assert.equal(facts.contains(orderNote), true);
    assert.equal(paymentList.nextElementSibling, orderNote);
    assert.equal(paymentList.querySelector("[data-payment-method]").nextElementSibling, null);
    assert.equal(panel.querySelector(".wasla-live-detail__products").contains(orderNote), false);
    assert.equal(panel.querySelectorAll("[data-order-note]").length, 1);
    assert.equal(panel.querySelector(".wasla-live-detail__actions").contains(orderNote), false);
    assert.equal(hasClass(panel, "order-row-new"), false);
    assert.equal(hasClass(orderNote, "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(noted).phase, "idle");

    h.browser.selectOrder(plain, true);
    await flush();
    const next = h.host.querySelector(".wasla-live-detail");
    assert.equal(next.getAttribute("data-order-id"), plain);
    assert.equal(next.querySelector("[data-order-note]"), null);
    assert.equal(next.querySelector(".wasla-live-detail__note") !== null, true);
    assert.equal(hasClass(next, "order-row-new"), false);
    assert.equal(h.browser.orderNoteAttentionState(plain).phase, "idle");
  } finally { h.restore(); }
});

test("Focus highlight finishes before the order-note pulse and selection does not restart it", async function () {
  const clock = attentionClock(2000000);
  const css = require("node:fs").readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");
  const focusPulse = css.match(/\.wasla-live-focus \[data-order-note\]\.wasla-order-note-attention\s*\{[^}]*\}/);
  assert.equal(focusPulse !== null, true);
  assert.equal(/!important/.test(focusPulse[0]), false);
  assert.match(focusPulse[0], /animation-name:\s*waslaOrderNoteAttention/);
  assert.match(css, /\.wasla-live-focus \.wasla-live-focus-order-note__text\s*\{[^}]*font-size:\s*\.875rem/);
  assert.match(css, /\.wasla-live-focus \.wasla-live-focus-order-note__text\s*\{[^}]*white-space:\s*normal/);
  assert.equal(/\.wasla-live-focus \.wasla-live-focus-order-note__text\s*\{[^}]*-webkit-line-clamp/.test(css), false);
  assert.match(css, /\.wasla-live-focus \.wasla-live-focus-order-note\s*\{[^}]*border-inline-start:\s*3px solid #e0b48a/);
  assert.match(css, /\.wasla-live-screen-host--focus \.wasla-live-focus-entry\.order-row-new\.color-orange\s*\{[^}]*background-color:\s*#ffe4d1/);
  assert.equal(css.includes(".wasla-live-detail.order-row-new"), false);
  assert.match(css, /@media \(prefers-reduced-motion:\s*reduce\)[\s\S]*\.wasla-live-screen-host--focus \.wasla-live-focus-entry\.order-row-new\.behavior-pulse[\s\S]*animation:\s*none !important/);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 4,
    highlightColor: "orange",
    highlightBehavior: "pulse",
    liveDetailModal: focusDetailModal()
  });
  try {
    const existing = guid(1);
    const fresh = guid(2);
    const baseline = notedSnapshot([existing], [{ customerNote: "Kapıya bırakın.", itemNote: null }]);
    h.view("focus");
    h.browser.renderSnapshot(baseline);
    await flush();
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(existing).phase, "idle");

    const body = notedSnapshot([existing, fresh], [
      { customerNote: "Kapıya bırakın.", itemNote: null },
      { customerNote: "Güvenliğe teslim.", itemNote: "Extra spicy" }
    ]);
    h.browser.renderSnapshot(body);
    h.table.markOrdersAsRecentlyNew([fresh]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    await flush();
    const queued = focusEntry(h.host, fresh);
    assert.equal(hasClass(queued, "order-row-new"), true);
    assert.equal(hasClass(queued, "color-orange"), true);
    assert.equal(hasClass(queued, "behavior-pulse"), true);
    assert.equal(queued.querySelector("[data-order-note]"), null);
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "idle");

    h.browser.selectOrder(fresh, true);
    await flush();
    const panel = h.host.querySelector(".wasla-live-detail");
    const note = panel.querySelector("[data-order-note]");
    assert.equal(panel.getAttribute("data-order-id"), fresh);
    assert.equal(hasClass(panel, "order-row-new"), false);
    assert.equal(hasClass(panel, "color-orange"), false);
    assert.equal(hasClass(panel, "behavior-pulse"), false);
    assert.equal(hasClass(focusEntry(h.host, fresh), "order-row-new"), true);
    assert.equal(hasClass(focusEntry(h.host, fresh), "color-orange"), true);
    h.browser.selectOrder(existing, true);
    await flush();
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(focusEntry(h.host, fresh), "order-row-new"), true);
    h.browser.selectOrder(fresh, true);
    await flush();
    assert.equal(note.querySelector("[data-order-note-text]").textContent, "Güvenliğe teslim.");
    assert.equal(panel.querySelector(".wasla-live-detail__note").contains(note), false);
    assert.equal(hasClass(note, "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "idle");

    await clock.advance(4029);
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(focusEntry(h.host, fresh), "order-row-new"), true);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(1);
    const started = h.browser.orderNoteAttentionState(fresh).endsAt;
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(focusEntry(h.host, fresh), "order-row-new"), false);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    assert.equal(started - clock.now(), 10000);

    h.browser.renderSnapshot(body);
    await flush();
    assert.equal(h.browser.orderNoteAttentionState(fresh).endsAt, started);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), true);

    body.orders[1].status = "Preparing";
    h.browser.renderSnapshot(body);
    await flush();
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "running");
    assert.equal(h.browser.orderNoteAttentionState(fresh).endsAt, started);
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail [data-order-note]"), "wasla-order-note-attention"), true);

    h.browser.selectOrder(existing, true);
    await flush();
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail [data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(existing).phase, "idle");
    assert.equal(h.browser.orderNoteAttentionState(fresh).endsAt, started);

    h.browser.selectOrder(fresh, true);
    await flush();
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    assert.equal(h.browser.orderNoteAttentionState(fresh).endsAt, started);

    await clock.advance(9999);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), true);
    await clock.advance(1);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");
    assert.equal(h.host.querySelector("[data-order-note-text]").textContent, "Güvenliğe teslim.");

    h.browser.renderSnapshot(body);
    await flush();
    h.browser.selectOrder(existing, true);
    await flush();
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail [data-order-note]"), "wasla-order-note-attention"), false);

    h.view("board");
    h.browser.renderSnapshot(body);
    h.view("list");
    h.browser.renderSnapshot(body);
    h.view("focus");
    h.browser.renderSnapshot(body);
    await flush();
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");
    assert.equal(h.browser.orderNoteAttentionState(existing).phase, "idle");
    assert.equal(h.host.querySelector(".wasla-live-detail [data-order-note].wasla-order-note-attention"), null);

    const gone = guid(3);
    const departing = notedSnapshot([existing, gone], [
      { customerNote: "Kapıya bırakın.", itemNote: null },
      { customerNote: "Sipariş notu silinecek.", itemNote: null }
    ]);
    h.browser.renderSnapshot(departing);
    h.table.markOrdersAsRecentlyNew([gone]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    h.browser.selectOrder(gone, true);
    await flush();
    await clock.advance(4030);
    assert.equal(h.browser.orderNoteAttentionState(gone).phase, "running");
    assert.equal(clock.pending(), 1);
    departing.orders = departing.orders.filter(function (order) { return order.id !== gone; });
    h.browser.renderSnapshot(departing);
    assert.equal(h.browser.orderNoteAttentionState(gone).phase, "done");
    assert.equal(clock.pending(), 0);
    assert.equal(h.host.querySelector('[data-order-id="' + gone + '"]'), null);
    await clock.advance(20000);
    assert.equal(h.browser.orderNoteAttentionState(gone).phase, "done");
  } finally { h.restore(); }
});

test("Focus reduced motion shows the order note without the pulse class", async function () {
  const clock = attentionClock(3000000);
  const h = attentionHarness({
    clock: clock,
    highlightSeconds: 2,
    liveDetailModal: focusDetailModal(),
    matchMedia: function (query) {
      return {
        matches: String(query).indexOf("prefers-reduced-motion") >= 0,
        addEventListener: function () {},
        addListener: function () {}
      };
    }
  });
  try {
    const existing = guid(8);
    const fresh = guid(9);
    const baseline = notedSnapshot([existing], [{ customerNote: "Kapıya bırakın.", itemNote: null }]);
    h.view("focus");
    h.browser.renderSnapshot(baseline);
    await flush();
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention-static"), false);

    const body = notedSnapshot([existing, fresh], [
      { customerNote: "Kapıya bırakın.", itemNote: null },
      { customerNote: "Temassız teslimat rica ediyorum.", itemNote: null }
    ]);
    h.browser.renderSnapshot(body);
    h.table.markOrdersAsRecentlyNew([fresh]);
    h.table.applyNewOrderVisualState();
    h.table.scheduleNewOrderHighlightCleanup();
    h.browser.selectOrder(fresh, true);
    await flush();
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(focusEntry(h.host, fresh), "order-row-new"), true);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention"), false);
    await clock.advance(2030);
    const block = h.host.querySelector("[data-order-note]");
    assert.equal(hasClass(h.host.querySelector(".wasla-live-detail"), "order-row-new"), false);
    assert.equal(hasClass(block, "wasla-order-note-attention"), false);
    assert.equal(hasClass(block, "wasla-order-note-attention-static"), true);
    assert.equal(block.querySelector("[data-order-note-text]").textContent, "Temassız teslimat rica ediyorum.");
    await clock.advance(10000);
    assert.equal(hasClass(h.host.querySelector("[data-order-note]"), "wasla-order-note-attention-static"), false);
    assert.equal(h.host.querySelector("[data-order-note-text]").textContent, "Temassız teslimat rica ediyorum.");
    assert.equal(h.browser.orderNoteAttentionState(fresh).phase, "done");
  } finally { h.restore(); }
});

test("Board, List, and Focus totals use the same TRY formatter", async function () {
  const h = boardHarness(true, {
    liveDetailModal: {
      loadPanel: function (container, id) {
        while (container.firstChild) container.removeChild(container.firstChild);
        const wrap = document.createElement("div");
        wrap.className = "wasla-live-detail";
        wrap.setAttribute("data-order-id", id);
        const grand = document.createElement("div");
        grand.className = "wasla-live-detail__grand";
        const amount = document.createElement("dd");
        amount.textContent = "0,00";
        grand.appendChild(amount);
        wrap.appendChild(grand);
        container.appendChild(wrap);
        return Promise.resolve(true);
      },
      cancelPanel: function () {}
    }
  });
  try {
    const amounts = [100, 1230.10, 98765.43];
    const ids = [guid(1), guid(2), guid(3)];
    const body = snapshot(ids);
    body.orders.forEach(function (order, index) { order.totalAmount = amounts[index]; });
    function cardTotal(id) {
      return h.host.querySelector('.wasla-live-screen-card[data-order-id="' + id + '"]')
        .querySelector("[data-total]").textContent;
    }
    h.view("list");
    h.browser.renderSnapshot(body);
    ids.forEach(function (id, index) {
      const text = cardTotal(id);
      assert.equal(text, store.formatAmount(amounts[index], "tr-TR"));
      assert.equal(text.indexOf("₺") >= 0, true);
      assert.equal(text.indexOf("TRY"), -1);
    });
    h.view("board");
    h.browser.renderSnapshot(body);
    ids.forEach(function (id, index) {
      const text = cardTotal(id);
      assert.equal(text.indexOf(store.formatAmount(amounts[index], "tr-TR")) >= 0, true);
      assert.equal(text.indexOf("₺") >= 0, true);
      assert.equal(text.indexOf("TRY"), -1);
      assert.equal(text.indexOf("$"), -1);
    });
    h.view("focus");
    h.browser.renderSnapshot(body);
    await flush();
    assert.equal(h.host.querySelector(".wasla-live-detail__grand dd").textContent, store.formatAmount(100, "tr-TR"));
    assert.equal(h.host.querySelector(".wasla-live-detail__grand dd").textContent.indexOf("₺") >= 0, true);
    assert.equal(h.host.querySelector(".wasla-live-detail__grand dd").textContent.indexOf("TRY"), -1);
    h.browser.selectOrder(guid(2), true);
    await flush();
    assert.equal(h.host.querySelector(".wasla-live-detail__grand dd").textContent, store.formatAmount(1230.10, "tr-TR"));
    h.browser.selectOrder(guid(3), true);
    await flush();
    assert.equal(h.host.querySelector(".wasla-live-detail__grand dd").textContent, store.formatAmount(98765.43, "tr-TR"));
  } finally { h.restore(); }
});

test("order detail money uses the shared TRY formatter without touching notes", async () => {
  const doc = fakeDocument();
  const container = doc.createElement("div");
  container.isConnected = true;
  function addMoney(amount, surcharge, className) {
    const el = doc.createElement(surcharge ? "span" : "dd");
    el.setAttribute("data-money", amount);
    if (surcharge) el.setAttribute("data-money-wrap", "surcharge");
    if (className) el.className = className;
    el.textContent = "plain";
    container.appendChild(el);
    return el;
  }
  const item = addMoney("286.70", false, "wasla-live-detail__amount");
  const cheese = addMoney("14.90", true);
  const sauce = addMoney("9.90", true);
  const delivery = addMoney("24.90", false);
  const service = addMoney("5.00", false);
  const total = addMoney("1504.00", false);
  const blank = doc.createElement("span");
  blank.setAttribute("data-money", "");
  blank.textContent = "plain";
  const orderNote = doc.createElement("p");
  orderNote.className = "wasla-live-detail-order-note__text";
  orderNote.textContent = "Siparişi kapının önüne bırakın. ".repeat(20);
  const itemNote = doc.createElement("p");
  itemNote.className = "wasla-live-detail__note";
  itemNote.textContent = "No onions";
  container.appendChild(blank);
  container.appendChild(orderNote);
  container.appendChild(itemNote);

  ["tr-TR", "en-US", "ar-SA", "ru-RU"].forEach(function (culture) {
    store.applyDetailCurrency(container, culture);
    [item, delivery, service, total].forEach(function (node) {
      const formatted = store.formatAmount(node.getAttribute("data-money"), culture);
      assert.equal(node.textContent, formatted);
      assert.equal(formatted.indexOf("$"), -1);
      assert.equal(formatted.indexOf("SAR"), -1);
      assert.equal(formatted.indexOf("₽"), -1);
      assert.equal(formatted.indexOf("RUB"), -1);
      assert.equal(formatted.indexOf("₺") >= 0, true);
      assert.equal(formatted.indexOf("TRY"), -1);
      assert.equal((formatted.match(/₺/g) || []).length, 1);
    });
    [cheese, sauce].forEach(function (node) {
      const formatted = store.formatAmount(node.getAttribute("data-money"), culture);
      assert.equal(node.textContent, "(+" + formatted + ")");
      assert.equal(node.textContent.indexOf(formatted), 2);
    });
  });
  store.applyDetailCurrency(container, "tr-TR");
  assert.equal(item.textContent, store.formatAmount("286.70", "tr-TR"));
  assert.equal((item.textContent.match(/₺/g) || []).length, 1);
  assert.equal((cheese.textContent.match(/₺/g) || []).length, 1);
  assert.equal(total.textContent, store.formatAmount("1504.00", "tr-TR"));
  assert.equal(blank.textContent, "plain");
  assert.equal(orderNote.textContent.length > 200, true);
  assert.equal(itemNote.textContent, "No onions");
  assert.equal(container.querySelectorAll(".wasla-live-detail__note").length, 1);
  assert.equal(container.querySelector(".wasla-live-detail-order-note__text"), orderNote);

  const loaded = [];
  const client = detailModal.createDetailClient({
    fetch: function () { return Promise.resolve({ ok: true, text: async () => "<p>panel</p>" }); },
    getMessage: function (key) { return key; },
    urlFor: function (id) { return "/panel/" + id; },
    document: doc,
    onLoaded: function (_node, consumer) { loaded.push(consumer); }
  });
  assert.equal(await client.load(doc.createElement("div"), guid(1), "modal"), true);
  assert.equal(await client.load(doc.createElement("div"), guid(2), "focus"), true);
  assert.deepEqual(loaded, ["modal", "focus"]);
});

// Order training while the tenant is in setup -------------------------------------------------------------

function notifyingHarness() {
  const time = clock();
  const http = deferredFetch();
  const accepted = [];
  const notified = [];
  const coordinator = store.createRefreshCoordinator({
    intervalMs: 10000,
    timeoutMs: 15000,
    maxBackoffMs: 30000,
    setTimeout: time.setTimeout,
    clearTimeout: time.clearTimeout,
    createAbortController: function () { return { abort: http.abort }; },
    fetchSnapshot: http.fetch,
    onStatus: function () {},
    onAccepted: function (body, meta) { accepted.push(meta); },
    onNotify: function (body, meta) { notified.push(meta.newIds.slice()); }
  });
  return { time: time, http: http, accepted: accepted, notified: notified, coordinator: coordinator };
}

const PRACTICE = guid(900);

/** A snapshot with the practice order and the given real orders; isolated: an isolated trainee's snapshot. */
function trainingSnapshot(realIds, isolated, realOrdersReceived) {
  const body = snapshot([PRACTICE].concat(realIds));
  body.orders[0].isDemo = true;
  body.training = isolated ? { isolated: true, realOrdersReceived: realOrdersReceived || 0 } : null;
  return body;
}

async function poll(h, body) {
  h.http.pending.resolve({ kind: "ok", snapshot: body });
  await flush();
  await flush();
}

test("isolated order training: real orders never reach the trainee, so nothing is announced while the count grows", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, trainingSnapshot([], true, 0));
  for (let received = 1; received <= 3; received++) {
    await h.time.advance(10000);
    await poll(h, trainingSnapshot([], true, received));
  }

  assert.deepEqual(h.notified, [], "no sound, browser notification or highlight");
  assert.equal(h.accepted.length, 4);
  assert.ok(h.accepted.every(function (meta) { return meta.newIds.length === 0 && meta.isolated === true; }));
});

test("the first snapshot after the tenant goes live is a silent baseline, then new orders notify normally", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, trainingSnapshot([], true, 2));

  // Completed or skipped in another tab: the same page now receives the real orders that arrived during training.
  await h.time.advance(10000);
  await poll(h, trainingSnapshot([guid(1), guid(2)], false));
  assert.equal(h.accepted[1].isBaseline, true);
  assert.deepEqual(h.accepted[1].newIds, []);
  assert.deepEqual(h.notified, [], "the revealed orders get no sound, notification or highlight");

  await h.time.advance(10000);
  await poll(h, trainingSnapshot([guid(1), guid(2), guid(3)], false));
  assert.equal(h.accepted[2].isBaseline, false);
  assert.deepEqual(h.notified, [[guid(3)]], "a genuinely new order behaves normally");
});

test("entering isolated order training is a silent baseline as well", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, snapshot([guid(1)]));
  await h.time.advance(10000);
  await poll(h, trainingSnapshot([], true, 1));
  await h.time.advance(10000);
  await poll(h, trainingSnapshot([], true, 2));

  assert.equal(h.accepted[1].isBaseline, true);
  assert.deepEqual(h.notified, []);
});

test("the practice order itself is never announced", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, snapshot([]));
  await h.time.advance(10000);
  const withPractice = snapshot([]);
  withPractice.orders = trainingSnapshot([], false).orders;
  await poll(h, withPractice);

  assert.deepEqual(h.notified, []);
});

test("a training section must carry a whole-number count, and only an isolated one counts as isolation", function () {
  assert.equal(store.validateSnapshot(trainingSnapshot([], true, 3)).ok, true);
  assert.equal(store.validateSnapshot(trainingSnapshot([], false)).ok, true);
  const negative = trainingSnapshot([], true, 1);
  negative.training.realOrdersReceived = -1;
  assert.equal(store.validateSnapshot(negative).reason, "training");
  const text = trainingSnapshot([], true, 1);
  text.training.realOrdersReceived = "2";
  assert.equal(store.validateSnapshot(text).reason, "training");

  assert.equal(store.isIsolatedSnapshot(trainingSnapshot([], true, 0)), true);
  assert.equal(store.isIsolatedSnapshot(trainingSnapshot([], false)), false);
  assert.equal(store.isIsolatedSnapshot({ training: { isolated: "true" } }), false);
  assert.equal(store.isIsolatedSnapshot(snapshot([])), false);
});

test("leaving isolated order training is exactly one silent baseline, in a setup or an already-live restaurant", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, trainingSnapshot([], true, 1));
  await h.time.advance(10000);
  await poll(h, trainingSnapshot([guid(1), guid(2)], false));
  for (let i = 0; i < 3; i++) {
    await h.time.advance(10000);
    await poll(h, trainingSnapshot([guid(1), guid(2)], false));
  }
  await h.time.advance(10000);
  await poll(h, trainingSnapshot([guid(1), guid(2), guid(7)], false));

  assert.deepEqual(h.accepted.map(function (meta) { return meta.isBaseline; }), [true, true, false, false, false, false]);
  assert.deepEqual(h.notified, [[guid(7)]]);
});

test("in the same restaurant a colleague's Live Screen announces a new order while the training Owner's does not", async function () {
  const trainee = notifyingHarness();
  const colleague = notifyingHarness();
  trainee.coordinator.start();
  colleague.coordinator.start();
  await flush();
  await poll(trainee, trainingSnapshot([], true, 0));
  await poll(colleague, snapshot([guid(1)]));

  // The same real order arrives for both.
  await trainee.time.advance(10000);
  await colleague.time.advance(10000);
  await poll(trainee, trainingSnapshot([], true, 1));
  await poll(colleague, snapshot([guid(1), guid(2)]));

  assert.deepEqual(trainee.notified, [], "no sound, browser notification or highlight for the trainee");
  assert.deepEqual(colleague.notified, [[guid(2)]], "the colleague keeps the normal new-order behaviour");
});

// Automation status in the snapshot ------------------------------------------------------------------------

/** The snapshot as a Manager or Owner receives it: the orders plus the automation section. */
function withAutomation(body, autoApprove, autoReceipt) {
  body.automation = { orderSync: "Active", autoApprove: autoApprove, autoReceipt: autoReceipt };
  return body;
}

test("Setup → Live changes only the automation section: same orders, no sound, notification, highlight or new ids", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, withAutomation(snapshot([guid(1), guid(2)]), "PendingSetup", "PendingSetup"));
  await h.time.advance(10000);
  await poll(h, withAutomation(snapshot([guid(1), guid(2)]), "Active", "Active"));
  await h.time.advance(10000);
  await poll(h, withAutomation(snapshot([guid(1), guid(2)]), "Active", "Active"));

  assert.equal(h.accepted.length, 3);
  assert.deepEqual(h.accepted.map(function (meta) { return meta.isBaseline; }), [true, false, false], "the baseline is not reset");
  assert.ok(h.accepted.every(function (meta) { return meta.newIds.length === 0; }));
  assert.deepEqual(h.notified, []);

  await h.time.advance(10000);
  await poll(h, withAutomation(snapshot([guid(1), guid(2), guid(3)]), "Active", "Active"));
  assert.deepEqual(h.notified, [[guid(3)]], "a genuinely new order still notifies normally");
});

test("Setup → Live together with the end of isolated training is one silent baseline; existing orders are not new", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, withAutomation(trainingSnapshot([], true, 2), "PendingSetup", "Off"));

  // The Owner completes in another tab: the same poll ends isolation and makes automatic approval active.
  await h.time.advance(10000);
  await poll(h, withAutomation(trainingSnapshot([guid(1), guid(2)], false), "Active", "Off"));

  assert.equal(h.accepted[1].isBaseline, true);
  assert.deepEqual(h.accepted[1].newIds, []);
  assert.deepEqual(h.notified, [], "the revealed orders and the status change get no sound, notification or highlight");
});

test("a transient status-read failure keeps polling the orders and announces nothing", async function () {
  const h = notifyingHarness();
  h.coordinator.start();
  await flush();
  await poll(h, withAutomation(snapshot([guid(1)]), "PendingSetup", "Off"));
  // The server could not read the status: the snapshot arrives without the section, orders as usual.
  await h.time.advance(10000);
  await poll(h, snapshot([guid(1)]));
  await h.time.advance(10000);
  await poll(h, withAutomation(snapshot([guid(1)]), "Active", "Off"));

  assert.equal(h.accepted.length, 3, "every poll was accepted");
  assert.deepEqual(h.accepted.map(function (meta) { return meta.isBaseline; }), [true, false, false]);
  assert.deepEqual(h.notified, []);

  await h.time.advance(10000);
  await poll(h, snapshot([guid(1), guid(2)]));
  assert.deepEqual(h.notified, [[guid(2)]], "polling and new-order detection continue normally");
});

test("the automation section never decides whether a snapshot is valid", function () {
  assert.equal(store.validateSnapshot(withAutomation(snapshot([guid(1)]), "PendingSetup", "Active")).ok, true);
  assert.equal(store.validateSnapshot(withAutomation(snapshot([guid(1)]), "Unknown", 7)).ok, true);
  const without = snapshot([guid(1)]);
  assert.equal(store.validateSnapshot(without).ok, true);
});

// Board card height -----------------------------------------------------------------------------------------------

function boardCss() {
  const fs = require("fs");
  const path = require("path");
  // Normalized so the contract does not depend on the checkout's line endings (CRLF on Windows with autocrlf).
  return fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8").replace(/\r\n/g, "\n");
}

/** The Board card sizing block: from its comment to the Board's own phone grid rule after it. */
function boardHeightBlock(css) {
  const start = css.indexOf("/* Board cards: one block size on desktop and tablet");
  const end = css.indexOf(".wasla-live-board { grid-template-columns: minmax(0, 1fr); }", start);
  assert.ok(start > 0 && end > start, "the Board card sizing block exists");
  return css.slice(start, end);
}

/** Every rule as { media, selector, body }, one level of @media deep (enough for wasla-theme.css). */
function cssRules(css) {
  const rules = [];
  const text = css.replace(/\/\*[\s\S]*?\*\//g, "");
  let i = 0;
  let media = "";
  let depth = 0;
  while (i < text.length) {
    const open = text.indexOf("{", i);
    const close = text.indexOf("}", i);
    if (close < 0) break;
    if (open >= 0 && open < close) {
      const head = text.slice(i, open).trim();
      if (head.startsWith("@")) {
        media = head;
        depth += 1;
        i = open + 1;
        continue;
      }
      const end = text.indexOf("}", open);
      rules.push({ media: depth ? media : "", selector: head, body: text.slice(open + 1, end) });
      i = end + 1;
    } else {
      if (depth) { depth -= 1; if (!depth) media = ""; }
      i = close + 1;
    }
  }
  return rules;
}

const CANONICAL_CARD = '.wasla-live-board .wasla-live-screen-card[data-live-layout="board"]';
const SIZE_PROPERTY = /(^|[;\s])(block-size|height|min-block-size|min-height|max-block-size|max-height)\s*:/;

test("Board cards use one canonical desktop and tablet block size", () => {
  const rules = cssRules(boardCss());
  const sizing = rules.filter(r => r.selector === CANONICAL_CARD && /(^|[;\s])block-size\s*:/.test(r.body));
  assert.equal(sizing.length, 1, "one fixed block-size rule");
  assert.equal(sizing[0].media, "@media (min-width: 768px)");
  assert.match(sizing[0].body, /block-size: var\(--wasla-live-board-card-block-size\);/);
  assert.match(sizing[0].body, /contain-intrinsic-size: auto var\(--wasla-live-board-card-block-size\);/);
  assert.match(sizing[0].body, /flex: none;/);
  const token = rules.filter(r => /--wasla-live-board-card-block-size\s*:/.test(r.body));
  assert.equal(token.length, 1, "the size is defined once");
  assert.equal(token[0].selector, ".wasla-live-board");
  assert.match(token[0].body, /--wasla-live-board-card-block-size: 20\.5rem;/);
});

test("every status uses the same Board card shell, and no status, demo or countdown rule resizes it", () => {
  const h = boardHarness();
  try {
    const body = snapshot([guid(1), guid(2), guid(3), guid(4), guid(5), guid(6), guid(7)]);
    ["New", "Accepted", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered"].forEach((status, i) => { body.orders[i].status = status; });
    body.orders[6].status = "ReadyForPickup";
    body.orders[6].isDemo = true;
    h.browser.renderSnapshot(body);
    const shells = h.host.querySelectorAll(".wasla-live-screen-card");
    assert.equal(shells.length, 7);
    for (const card of shells) {
      assert.equal(card.getAttribute("data-live-layout"), "board");
      for (const name of ["orders-card", "wasla-live-screen-card"]) assert.equal(card.classList.contains(name), true, name);
      const parts = card.querySelector(".orders-card-body").children.map(el => el.className.split(" ")[0]);
      assert.deepEqual(parts, ["wasla-live-card__identity", "wasla-live-board-items", "wasla-live-card__secondary", "orders-card-actions"], card.getAttribute("data-order-status"));
    }
  } finally { h.restore(); }

  // Only the canonical rules size a Board card; nothing keyed on status, muted, new-order, demo or countdown state.
  const isCard = selector => selector.split(",").some(part => {
    const tokens = part.trim().split(/\s+/);
    return /wasla-live-board|wasla-live-screen-host--board/.test(part)
      && /^\.(wasla-live-screen-card|orders-card)(--[\w-]+)?([.[:].*)?$/.test(tokens[tokens.length - 1]);
  });
  const existingBase = ".wasla-live-board .wasla-live-screen-card, .wasla-live-board .orders-card--kitchen";
  const offenders = cssRules(boardCss()).filter(r => isCard(r.selector) && SIZE_PROPERTY.test(r.body)
    && r.selector.replace(/\s+/g, " ") !== existingBase
    && !(r.selector === CANONICAL_CARD && (r.media === "@media (min-width: 768px)" || r.media === "@media (max-width: 767.98px)")));
  assert.deepEqual(offenders.map(r => (r.media + " " + r.selector).trim()), []);
  for (const rule of cssRules(boardCss()).filter(r => /wasla-live-board/.test(r.selector) && SIZE_PROPERTY.test(r.body)))
    assert.equal(/data-order-status|orders-card--muted|order-row-new|data-wasla-demo|:has\(/.test(rule.selector), false, rule.selector);
});

test("price and actions sit on the bottom edge; identity stays on top; only the product preview gives way", () => {
  const block = boardHeightBlock(boardCss());
  const rules = cssRules(block);
  const find = (suffix, media) => rules.find(r => r.selector === CANONICAL_CARD + suffix && r.media === media);
  const desktop = "@media (min-width: 768px)";
  assert.match(find(" > .orders-card-body", desktop).body, /flex: 1 1 auto;\s*min-block-size: 0;/);
  assert.match(find(" .wasla-live-card__secondary", desktop).body, /margin-block-start: auto;/);
  const items = find(" .wasla-live-board-items", desktop).body;
  for (const rule of ["display: flex;", "flex-direction: column;", "flex: 1 1 auto;", "min-block-size: 0;", "overflow: hidden;"])
    assert.ok(items.includes(rule), rule);
  assert.match(find(" .wasla-live-board-items__viewport", desktop).body, /flex: 0 1 auto;\s*min-block-size: 0;/);
  const fixed = rules.find(r => r.media === desktop && r.selector.includes(CANONICAL_CARD + " .wasla-live-card__identity"));
  for (const part of ["wasla-live-card__identity", "wasla-live-board-order-note", "wasla-live-card__secondary", "orders-card-actions", "wasla-live-board-items__more"])
    assert.ok(fixed.selector.includes(part), part + " keeps its size");
  assert.match(fixed.body, /flex: none;/);
  // The base card clips, so nothing can paint outside it; the existing preview cap and clamps stay.
  const css = boardCss();
  assert.match(css, /\.orders-card,\n\.wasla-orders-card \{[^}]*overflow: hidden;/);
  assert.match(css, /\.wasla-live-board \.wasla-live-board-items__viewport \{\s*max-height: 6\.75rem;\s*overflow: hidden;/);
  assert.match(css, /\.wasla-live-board \.wasla-live-board-order-note__text \{[^}]*-webkit-line-clamp: 2;/);
});

test("long names, notes and badges wrap inside the card, and Details stays available on every real order", () => {
  const block = boardHeightBlock(boardCss());
  assert.match(block, /\.wasla-live-screen-card__product,\n[^{]*\.wasla-live-screen-card__note \{\s*min-inline-size: 0;\s*overflow-wrap: anywhere;/);
  assert.match(block, /\.wasla-dash-status \{\s*max-inline-size: 100%;\s*white-space: normal;\s*overflow-wrap: anywhere;/);
  const h = boardHarness();
  try {
    const body = snapshot([guid(1), guid(2), guid(3), guid(4), guid(5)]);
    const long = "Izgara Tavuk Kanat Menü Büyük Boy Acılı Soslu Patates ve Ayranla ".repeat(3);
    ["New", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered"].forEach((status, i) => {
      body.orders[i].status = status;
      body.orders[i].customerNote = long;
      body.orders[i].items = Array.from({ length: 8 }, (_, n) => ({ productName: long + n, quantity: n + 1, notes: long }));
    });
    h.browser.renderSnapshot(body);
    for (const card of h.host.querySelectorAll(".wasla-live-screen-card")) {
      const details = card.querySelector("[data-order-detail]");
      assert.ok(details && !details.hidden, card.getAttribute("data-order-status") + " keeps Details");
      assert.equal(card.querySelectorAll(".wasla-live-screen-card__item").length, 8, "every product is still in the card, previewed by the cap");
    }
    const actions = id => h.card(id).querySelectorAll("[data-order-action]").map(b => b.getAttribute("data-order-action"));
    assert.deepEqual(actions(guid(1)), ["approve", "reject"]);
    assert.deepEqual(actions(guid(2)), ["mark-ready"]);
  } finally { h.restore(); }
});

test("the product preview's 'more in details' line follows its own size as room inside the fixed card changes", () => {
  const observed = new Set();
  let callback = null;
  const frames = [];
  function ResizeObserver(fn) { callback = fn; }
  ResizeObserver.prototype.observe = function (el) { observed.add(el); };
  ResizeObserver.prototype.unobserve = function (el) { observed.delete(el); };
  const h = boardHarness(true, { global: { ResizeObserver, requestAnimationFrame: fn => frames.push(fn) } });
  try {
    const body = snapshot([guid(1), guid(2)]);
    h.browser.renderSnapshot(body);
    const card = h.card(guid(1));
    const viewport = card.querySelector(".wasla-live-board-items__viewport");
    assert.equal(observed.has(viewport), true);
    assert.equal(card.querySelector(".wasla-live-board-items").classList.contains("has-item-overflow"), false);

    // The countdown appears above it: same card, smaller preview, now overflowing.
    viewport.scrollHeight = 60;
    viewport.clientHeight = 21;
    callback([{ target: viewport }, { target: viewport }]);
    assert.equal(frames.length, 1, "one re-check per frame");
    frames.shift()();
    assert.equal(card.querySelector(".wasla-live-board-items").classList.contains("has-item-overflow"), true);
    assert.equal(card.querySelector(".wasla-live-screen-card__details").classList.contains("has-item-overflow"), true);

    // It goes away again.
    viewport.clientHeight = 80;
    callback([{ target: viewport }]);
    frames.shift()();
    assert.equal(card.querySelector(".wasla-live-board-items").classList.contains("has-item-overflow"), false);

    // A card that leaves is no longer observed.
    h.browser.renderSnapshot(snapshot([guid(2)]));
    assert.equal(observed.has(viewport), false);
  } finally { h.restore(); }
});

test("the practice-order countdown fits inside the fixed card: no size of its own, no rule that grows the card", () => {
  const css = boardCss();
  const block = boardHeightBlock(css);
  assert.match(block, new RegExp(CANONICAL_CARD.replace(/[.[\]"=]/g, "\\$&") + " \\.wasla-demo-countdown \\{\\s*margin-block: 0;\\s*\\}"));
  const countdown = css.slice(css.indexOf("/* Practice-order countdown"));
  assert.equal(SIZE_PROPERTY.test(cssRules(countdown).filter(r => r.selector === ".wasla-demo-countdown").map(r => r.body).join(";")), false);
  assert.equal(/:has\(\[data-demo-countdown\]\)|:has\(\.wasla-demo-countdown\)/.test(css), false, "no card rule keyed on the countdown");
  // It sits in the card's top region (after the status row), so it takes room from the preview, not from the card.
  const fs = require("fs");
  const path = require("path");
  const script = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/wwwroot/js/orders/orders-demo-countdown.js"), "utf8");
  assert.match(script, /var badge = card\.querySelector\("\[data-status-badge\]"\);/);
});

test("List, Focus, the detail modal and phones are not given the Board's fixed height", () => {
  const rules = cssRules(boardCss());
  const fixed = rules.filter(r => /(^|[;\s])block-size: var\(--wasla-live-board-card-block-size\)/.test(r.body));
  assert.equal(fixed.length, 1);
  assert.equal(fixed[0].media, "@media (min-width: 768px)");
  for (const other of ["wasla-live-groups", "wasla-live-group", "wasla-live-list-row", "wasla-live-focus", "wasla-live-detail", "modal", "orders-card-grid"])
    assert.equal(fixed[0].selector.includes(other), false, other);
  const phone = rules.filter(r => r.selector === CANONICAL_CARD && r.media === "@media (max-width: 767.98px)");
  assert.equal(phone.length, 1);
  assert.match(phone[0].body, /min-block-size: var\(--wasla-live-board-card-min-block-size\);/);
  assert.equal(/(^|[;\s])block-size\s*:/.test(phone[0].body), false, "phones grow with their content");
  assert.ok(rules.some(r => r.selector === ".wasla-live-board" && /--wasla-live-board-card-min-block-size: 15rem;/.test(r.body)));

  const h = boardHarness(true, { liveDetailModal: { loadPanel: () => Promise.resolve(true) } });
  try {
    const body = snapshot([guid(1), guid(2)]);
    body.orders[1].isDemo = true;
    h.view("list");
    h.browser.renderSnapshot(body);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0);
    assert.ok(h.host.querySelectorAll(".wasla-live-screen-card").every(row => row.getAttribute("data-live-layout") === "list"));
    h.view("focus");
    h.browser.renderSnapshot(body);
    h.browser.selectOrder(guid(2), true);
    assert.equal(h.host.querySelectorAll(".wasla-live-board").length, 0, "the Focus detail card is outside any Board");
    assert.ok(h.host.querySelector(".wasla-demo-focus-card"));
  } finally { h.restore(); }
});

test("the Board sizing is logical (RTL-safe) and colour-free (dark mode keeps its tokens)", () => {
  const block = boardHeightBlock(boardCss());
  for (const physical of ["margin-left", "margin-right", "padding-left", "padding-right", "border-left", "border-right", "text-align: left", "text-align: right"])
    assert.equal(block.includes(physical), false, physical);
  assert.equal(/[\s{;](left|right|top|bottom|width|height|min-height|max-height)\s*:/.test(block.replace(/\/\*[\s\S]*?\*\//g, "")), false, "block-size and inline-size, not physical sizes");
  assert.equal(/#[0-9a-f]{3,8}\b|rgba?\(|color\s*:|background/.test(block.replace(/\/\*[\s\S]*?\*\//g, "")), false, "no colours: light and dark keep the existing tokens");
});
