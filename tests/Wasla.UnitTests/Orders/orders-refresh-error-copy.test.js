// WAS-129: a failed Orders refresh showed "… (HTTP 500)" and other failures told operators to "check the console". The
// operator sees the localized message only; the status stays in the debug log.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const root = "../../../src/Wasla.Web/wwwroot/js/orders/";

function load(file, context) {
  vm.runInNewContext(fs.readFileSync(require.resolve(root + file), "utf8"), context);
}

function orders(fetch, extras) {
  const shown = [];
  const debug = [];
  const O = Object.assign({
    opts: { pageMode: "orders", tableUrl: "/orders/table", notificationSettingsJsonUrl: "/notification-settings/current" },
    getMessage: function (key) { return "msg:" + key; },
    isDebugEnabled: function () { return false; },
    debugLog: function () {},
    debugWarn: function () { debug.push(Array.prototype.slice.call(arguments)); },
    showOrdersWarning: function (key, message) { shown.push(message); },
    state: { notificationSettings: null },
    audio: {}
  }, extras || {});
  const window = {
    WaslaOrders: O,
    location: { origin: "https://tenant.test", search: "" }
  };
  const context = {
    window: window,
    document: { getElementById: function () { return null; }, querySelectorAll: function () { return []; } },
    fetch: fetch,
    URL: URL,
    URLSearchParams: URLSearchParams,
    Date: Date,
    setTimeout: setTimeout,
    clearTimeout: clearTimeout,
    console: console
  };
  return { O: O, window: window, context: context, shown: shown, debug: debug };
}

function response(status, body, type) {
  return {
    ok: status >= 200 && status < 300,
    status: status,
    headers: { get: function () { return type || "text/html"; } },
    text: async function () { return body; },
    json: async function () { return JSON.parse(body); }
  };
}

test("a failed Orders refresh shows the localized message without the HTTP status, which goes to the debug log", async function () {
  const h = orders(async function () { return response(500, "error"); });
  load("orders-table.js", h.context);
  await h.O.table.refreshOrdersTable();
  assert.deepEqual(h.shown, ["msg:tableRefreshFailed"]);
  assert.deepEqual(h.debug, [["table refresh failed", 500]]);
});

test("an Orders refresh that cannot reach the server shows the localized exception message", async function () {
  const h = orders(async function () { throw new TypeError("Failed to fetch"); });
  load("orders-table.js", h.context);
  await h.O.table.refreshOrdersTable();
  assert.deepEqual(h.shown, ["msg:tableRefreshException"]);
});

test("rows without order ids are a debug-log diagnostic, not an operator message", async function () {
  const h = orders(async function () { return response(200, "<tr><td>row</td></tr>"); });
  h.context.document = {
    getElementById: function () { return null; },
    createElement: function () {
      return {
        set innerHTML(value) { this._html = value; },
        querySelectorAll: function (selector) { return selector === "tr" ? [{}] : []; }
      };
    },
    querySelectorAll: function () { return []; }
  };
  load("orders-table.js", h.context);
  await h.O.table.refreshOrdersTable();
  // The table host is missing in this fake page, which the operator is told in the same localized words.
  assert.deepEqual(h.shown, ["msg:tableRefreshException"]);
  assert.ok(h.debug.some(function (entry) { return entry[0] === "rows without data-order-id"; }));
});

test("notification settings that fail to load show the localized message without the HTTP status", async function () {
  const errors = [];
  const h = orders(async function () { return response(503, "unavailable"); });
  h.window.WaslaToast = { error: function (message) { errors.push(message); } };
  load("orders-notification-settings.js", h.context);
  await h.O.notificationSettings.load();
  assert.deepEqual(errors, ["msg:notificationSettingsLoadFailed"]);
  assert.deepEqual(h.debug, [["Notification settings request failed", 503]]);
});

// WAS-129: the Live Screen awaits this load before it starts reading orders, so a request (or body) that never finished
// kept it on Loading. The whole load has a deadline; after it the page goes on without settings, as after a failure.
function manualTimers() {
  const timers = [];
  return {
    setTimeout: function (fn, ms) { const t = { fn: fn, ms: ms, cleared: false }; timers.push(t); return t; },
    clearTimeout: function (t) { if (t) t.cleared = true; },
    fire: function () { timers.filter(t => !t.cleared && !t.fired).forEach(t => { t.fired = true; t.fn(); }); },
    pending: function () { return timers.filter(t => !t.cleared && !t.fired).map(t => t.ms); }
  };
}

function settingsHarness(fetch) {
  const errors = [];
  const timers = manualTimers();
  const h = orders(fetch);
  h.window.WaslaToast = { error: function (message) { errors.push(message); } };
  h.context.setTimeout = timers.setTimeout;
  h.context.clearTimeout = timers.clearTimeout;
  h.context.AbortController = AbortController;
  load("orders-notification-settings.js", h.context);
  return { O: h.O, errors: errors, timers: timers, debug: h.debug };
}

const flushAll = () => new Promise(resolve => setImmediate(resolve));
const lateSettings = { newOrderSoundEnabled: true, newOrderSoundName: "bell1", newOrderSoundRepeatCount: 3, newOrderSoundVolumePercent: 100, showBrowserNotification: true };

for (const mode of ["headers", "body"]) {
  test("a notification-settings load whose " + mode + " never finish ends at the 15 s deadline without settings, and the late answer is ignored", async function () {
    let finish;
    let signal;
    const h = settingsHarness(function (url, init) {
      signal = init.signal;
      if (mode === "headers") return new Promise(resolve => { finish = () => resolve(response(200, JSON.stringify(lateSettings), "application/json")); });
      return Promise.resolve({
        ok: true, status: 200, headers: { get: () => "application/json" },
        json: () => new Promise(resolve => { finish = () => resolve(lateSettings); })
      });
    });
    let done = false;
    const loading = h.O.notificationSettings.load().then(() => { done = true; });
    await flushAll();
    assert.equal(done, false, "the load is still waiting");
    assert.deepEqual(h.timers.pending(), [15000], "the same limit as a Live Screen snapshot read");
    h.timers.fire();
    await loading;
    assert.equal(signal.aborted, true, "the request is cancelled");
    assert.equal(h.O.state.notificationSettings, null, "no settings: no sound and no browser notification");
    assert.deepEqual(h.errors, ["msg:notificationSettingsLoadTimedOut"]);

    finish();
    await flushAll();
    assert.equal(h.O.state.notificationSettings, null, "the late answer does not turn sound or notifications on");
    assert.deepEqual(h.errors, ["msg:notificationSettingsLoadTimedOut"], "nothing more is reported");
  });
}

test("an older, slower notification-settings load never overwrites a newer one", async function () {
  const answers = [];
  const h = settingsHarness(function () { return new Promise(resolve => answers.push(resolve)); });
  const first = h.O.notificationSettings.load();
  const second = h.O.notificationSettings.load();
  await flushAll();
  answers[1](response(200, JSON.stringify(Object.assign({}, lateSettings, { newOrderSoundEnabled: false })), "application/json"));
  await second;
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, false);
  answers[0](response(200, JSON.stringify(lateSettings), "application/json"));
  await first;
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, false, "the newer settings stay");
  assert.deepEqual(h.errors, []);
  assert.deepEqual(h.timers.pending(), [], "both deadlines are cleared");
});

test("an expired session is still a failed load, never settings", async function () {
  const h = settingsHarness(async function () { return response(401, ""); });
  await h.O.notificationSettings.load();
  assert.equal(h.O.state.notificationSettings, null);
  assert.deepEqual(h.errors, ["msg:notificationSettingsLoadFailed"]);
  assert.deepEqual(h.timers.pending(), []);
});
