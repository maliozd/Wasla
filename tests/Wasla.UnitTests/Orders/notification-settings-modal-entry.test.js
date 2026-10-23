// WAS-13: the Live Screen settings menu and order training open the one shared notification settings modal, and
// browser permission, sound and highlight stay independent.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const settingsSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-notification-settings.js"),
  "utf8"
);
const livePageSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-display-page.js"),
  "utf8"
);

function element(extra) {
  const listeners = {};
  return Object.assign({
    value: "",
    checked: false,
    textContent: "",
    innerHTML: "",
    className: "",
    hidden: false,
    disabled: false,
    selectedIndex: 0,
    options: [],
    attributes: {},
    focused: 0,
    style: { setProperty: function () {}, removeProperty: function () {} },
    classList: { add: function () {}, remove: function () {}, contains: function () { return false; }, toggle: function () {} },
    addEventListener: function (type, fn) {
      (listeners[type] = listeners[type] || []).push(fn);
    },
    dispatch: function (type, event) {
      return Promise.all((listeners[type] || []).slice().map(function (fn) {
        return fn(Object.assign({ preventDefault: function () {}, target: this }, event || {}));
      }, this));
    },
    getAttribute: function (name) {
      return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
    },
    setAttribute: function (name, value) { this.attributes[name] = String(value); },
    focus: function () { this.focused += 1; },
    closest: function () { return null; }
  }, extra || {});
}

const SAVED = {
  newOrderSoundEnabled: false,
  newOrderSoundName: "bell2",
  newOrderSoundRepeatCount: 2,
  newOrderSoundVolumePercent: 50,
  showBrowserNotification: false,
  newOrderHighlightColor: "blue",
  newOrderHighlightBehavior: "pulse",
  newOrderHighlightDurationSeconds: 20
};

/** The shared modal script against a fake page: the modal shell, its loaded body and Bootstrap's Modal. */
function settingsHarness(options) {
  const opts = options || {};
  const calls = { permission: [], modalFetches: 0, shows: 0, posts: [], plays: 0 };
  const ids = {
    notificationSettingsModal: element(),
    notificationSettingsModalBody: element(),
    notificationSettingsForm: element({ action: "/notification-settings" }),
    NewOrderSoundEnabled: element({ value: "false" }),
    NewOrderSoundEnabledToggle: element(),
    newOrderSoundSelect: element({ value: "bell2", options: [{ value: "bell2", getAttribute: function () { return "/sounds/bell2.mp3"; } }] }),
    NewOrderSoundName: element({ value: "bell2" }),
    NewOrderSoundRepeatCount: element({ value: "2" }),
    NewOrderSoundVolumePercent: element({ value: "50" }),
    ShowBrowserNotification: element(),
    NewOrderHighlightColor: element({ value: "blue" }),
    NewOrderHighlightBehavior: element({ value: "pulse" }),
    NewOrderHighlightDurationSeconds: element({ value: "20" }),
    testSelectedSoundBtn: element(),
    saveNotificationsBtn: element(),
    browserNotificationPermissionHelp: element({
      attributes: {
        "data-permission-default": "asks once",
        "data-permission-granted": "allowed",
        "data-permission-denied": "blocked",
        "data-permission-unsupported": "unsupported"
      }
    })
  };
  const O = {
    state: { notificationSettings: null },
    opts: { notificationSettingsUrl: "/notification-settings", notificationSettingsJsonUrl: "/notification-settings/current" },
    debugLog: function () {},
    debugWarn: function () {},
    getMessage: function (key) { return key; },
    showOrdersWarning: function () {},
    audio: {
      syncSoundEnableUi: function () {},
      getAudioState: function () { return "unknown"; },
      stopCurrentPreviewSound: function () {},
      stopTestPreview: function () {},
      playSoundNow: function () { calls.plays += 1; return Promise.resolve(); },
      maybeRequestBrowserNotificationPermission: function (state) {
        calls.permission.push(state);
        if (opts.onPermission) opts.onPermission();
        return Promise.resolve();
      }
    }
  };
  const win = {
    WaslaOrders: O,
    Notification: { permission: opts.permission || "default" },
    bootstrap: {
      Modal: {
        getInstance: function () { return { hide: function () {} }; },
        getOrCreateInstance: function () { return { show: function () { calls.shows += 1; } }; }
      }
    }
  };
  const context = {
    window: win,
    document: {
      getElementById: function (id) { return ids[id] || null; },
      querySelectorAll: function () { return []; },
      contains: function () { return true; }
    },
    fetch: function (url, init) {
      if (init && init.method === "POST") {
        calls.posts.push(init.body);
        return Promise.resolve({
          ok: true,
          status: 200,
          headers: { get: function () { return "application/json"; } },
          json: function () { return Promise.resolve({ success: true }); }
        });
      }
      if (String(url).indexOf("/current") >= 0) {
        return Promise.resolve({ ok: true, status: 200, json: function () { return Promise.resolve(Object.assign({}, SAVED)); } });
      }
      calls.modalFetches += 1;
      if (opts.modalFails) return Promise.resolve({ ok: false, status: 500 });
      return Promise.resolve({ ok: true, status: 200, text: function () { return Promise.resolve("<div></div>"); } });
    },
    FormData: function () {
      const fields = {};
      this.set = function (k, v) { fields[k] = v; };
      this.fields = fields;
    },
    AbortController: AbortController,
    setTimeout: function (fn) { fn(); }
  };
  vm.runInNewContext(settingsSource, context);
  return { O: O, ids: ids, calls: calls, win: win };
}

/** A control inside the Live Screen settings dropdown, whose menu closes when the modal opens. */
function menuItem(toggle) {
  const dropdown = { querySelector: function (selector) { return selector.indexOf("dropdown") >= 0 ? toggle : null; } };
  const menu = { parentElement: dropdown };
  return element({ closest: function (selector) { return selector === ".dropdown-menu" ? menu : null; } });
}

test("show opens the existing modal once, and focus returns to the settings toggle when it closes", async function () {
  const h = settingsHarness();
  const toggle = element();
  const item = menuItem(toggle);

  await Promise.all([h.O.notificationSettings.show(item), h.O.notificationSettings.show(item)]);

  assert.equal(h.calls.modalFetches, 1, "a repeated click while it opens is ignored");
  assert.equal(h.calls.shows, 1);
  // Saved values from /notification-settings/current are put into the modal unchanged.
  assert.equal(h.ids.NewOrderSoundEnabled.value, "false");
  assert.equal(h.ids.NewOrderSoundRepeatCount.value, "2");
  assert.equal(h.ids.NewOrderHighlightBehavior.value, "pulse");

  await h.ids.notificationSettingsModal.dispatch("hidden.bs.modal");
  assert.equal(toggle.focused, 1);
  assert.equal(item.focused, 0);
});

test("a failed load leaves the modal closed", async function () {
  const h = settingsHarness({ modalFails: true });

  await h.O.notificationSettings.show(element());

  assert.equal(h.calls.shows, 0);
});

test("opening and reading never asks for browser permission; the help line shows the browser's state", async function () {
  const h = settingsHarness({ permission: "denied" });

  await h.O.notificationSettings.show(element());

  assert.equal(h.calls.permission.length, 0);
  assert.equal(h.ids.browserNotificationPermissionHelp.textContent, "blocked");
});

test("only turning browser notifications on asks for permission", async function () {
  const h = settingsHarness();
  await h.O.notificationSettings.show(element());
  const checkbox = h.ids.ShowBrowserNotification;

  checkbox.checked = false;
  await checkbox.dispatch("change");
  assert.equal(h.calls.permission.length, 0, "turning it off never asks");

  h.win.Notification.permission = "default";
  checkbox.checked = true;
  await checkbox.dispatch("change", { target: checkbox });
  assert.equal(h.calls.permission.length, 1);
  assert.equal(h.calls.permission[0].showBrowserNotification, true);

  // Sound and highlight are untouched by the browser-notification choice.
  assert.equal(h.ids.NewOrderSoundEnabled.value, "false");
  assert.equal(h.ids.NewOrderHighlightColor.value, "blue");
  assert.equal(h.ids.NewOrderHighlightBehavior.value, "pulse");
});

test("the help line follows the answer to the permission request", async function () {
  let h = null;
  h = settingsHarness({ onPermission: function () { h.win.Notification.permission = "granted"; } });
  await h.O.notificationSettings.show(element());
  assert.equal(h.ids.browserNotificationPermissionHelp.textContent, "asks once");

  h.ids.ShowBrowserNotification.checked = true;
  await h.ids.ShowBrowserNotification.dispatch("change");

  assert.equal(h.ids.browserNotificationPermissionHelp.textContent, "allowed");
});

test("testing a sound and saving sound or highlight never ask for browser permission", async function () {
  const h = settingsHarness();
  await h.O.notificationSettings.show(element());
  h.ids.ShowBrowserNotification.checked = true;

  await h.ids.testSelectedSoundBtn.dispatch("click");
  await h.O.notificationSettings.saveNotificationSettings();

  assert.equal(h.calls.plays, 1);
  assert.equal(h.calls.posts.length, 1);
  assert.equal(h.calls.permission.length, 0);
  const saved = h.calls.posts[0].fields;
  assert.equal(saved.NewOrderSoundEnabled, "false");
  assert.equal(saved.NewOrderHighlightColor, "blue");
  assert.equal(saved.NewOrderHighlightBehavior, "pulse");
});

test("the Live Screen opens notification settings from any marked control, including ones rendered later", async function () {
  const opened = [];
  const clickListeners = [];
  let ready = null;
  const O = {
    opts: { pageMode: "liveDisplay" },
    table: {},
    debugWarn: function () {},
    notificationSettings: {
      load: function () { return Promise.resolve(); },
      show: function (trigger) { opened.push(trigger); return Promise.resolve(); }
    }
  };
  const doc = {
    body: { classList: { add: function () {} } },
    getElementById: function () { return null; },
    addEventListener: function (type, fn) {
      if (type === "DOMContentLoaded") ready = fn;
      if (type === "click") clickListeners.push(fn);
    }
  };
  vm.runInNewContext(livePageSource, { window: Object.assign({ WaslaOrders: O }, { document: doc }), document: doc, setTimeout: setTimeout });
  ready();
  await Promise.resolve();

  const trigger = element();
  let prevented = 0;
  const inside = element({ closest: function (selector) { return selector === "[data-notification-settings-open]" ? trigger : null; } });
  clickListeners.forEach(function (fn) { fn({ target: inside, preventDefault: function () { prevented += 1; } }); });
  clickListeners.forEach(function (fn) { fn({ target: element(), preventDefault: function () { prevented += 1; } }); });

  assert.deepEqual(opened, [trigger]);
  assert.equal(prevented, 1);
});
