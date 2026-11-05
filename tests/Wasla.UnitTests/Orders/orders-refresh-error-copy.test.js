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
