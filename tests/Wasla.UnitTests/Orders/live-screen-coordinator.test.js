const test = require("node:test");
const assert = require("node:assert/strict");
const store = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js");

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

test("culture formats the amount without selecting a currency", function () {
  const tr = store.formatAmount(120.5, "tr-TR");
  const en = store.formatAmount(120.5, "en-US");
  const ar = store.formatAmount(120.5, "ar-SA");
  assert.equal(tr.indexOf("₺"), -1);
  assert.equal(en.indexOf("$"), -1);
  assert.equal(ar.toUpperCase().indexOf("SAR"), -1);
  assert.notEqual(tr, en);
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
      return {
        toggle: function (name, on) {
          const names = self.className.split(/\s+/).filter(Boolean);
          const has = names.indexOf(name) >= 0;
          const enable = on === undefined ? !has : !!on;
          const next = names.filter(function (item) { return item !== name; });
          if (enable) next.push(name);
          self.className = next.join(" ");
        }
      };
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
    appendChild: function (node) {
      if (node.parentElement && node.parentElement.removeChild) node.parentElement.removeChild(node);
      node.parentElement = this;
      this.childNodes.push(node);
      return node;
    },
    removeChild: function (node) {
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
    querySelector: function (selector) { return queryAll(this, selector)[0] || null; },
    querySelectorAll: function (selector) { return queryAll(this, selector); }
  };
  function matches(el, selector) {
    if (selector.charAt(0) === ".") return el.className.split(/\s+/).indexOf(selector.slice(1)) >= 0;
    if (selector.charAt(0) === "[") {
      const body = selector.slice(1, -1);
      const eq = body.indexOf("=");
      if (eq < 0) return el.getAttribute(body) !== null;
      let expected = body.slice(eq + 1).trim();
      if (expected.charAt(0) === '"' || expected.charAt(0) === "'") expected = expected.slice(1, -1);
      return el.getAttribute(body.slice(0, eq)) === expected;
    }
    return el.tagName === selector.toUpperCase();
  }
  function walk(node, visit) {
    node.childNodes.forEach(function (child) {
      if (child.nodeType !== 1) return;
      visit(child);
      walk(child, visit);
    });
  }
  function queryAll(root, selector) {
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
  return {
    createElement: function (tag) { return new El(tag); },
    createTextNode: function (text) { return { nodeType: 3, textContent: String(text), parentElement: null }; },
    getElementById: function (id) { return byId.get(id) || null; },
    documentElement: { lang: "tr-TR" }
  };
}
