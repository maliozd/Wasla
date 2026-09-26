const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const settingsSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-notification-settings.js"),
  "utf8"
);
const pageSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/settings/order-settings-page.js"),
  "utf8"
);

function element(extra) {
  const listeners = {};
  const classes = new Set();
  return Object.assign({
    value: "",
    checked: false,
    textContent: "",
    className: "",
    hidden: false,
    selectedIndex: 0,
    options: [],
    style: { setProperty: function () {}, removeProperty: function () {} },
    classList: {
      add: function (name) { classes.add(name); },
      remove: function (name) { classes.delete(name); },
      contains: function (name) { return classes.has(name); },
      toggle: function (name, on) {
        if (on) classes.add(name);
        else classes.delete(name);
      }
    },
    addEventListener: function (type, fn) {
      listeners[type] = listeners[type] || [];
      listeners[type].push(fn);
    },
    click: function () {
      const pending = (listeners.click || []).map(function (fn) {
        return fn({ preventDefault: function () {}, target: {} });
      });
      return pending[0];
    },
    getAttribute: function () { return null; },
    setAttribute: function () {}
  }, extra || {});
}

test("settings page loads saved notification settings into the shared runtime", function () {
  let loads = 0;
  let ready = null;
  const O = {
    notificationSettings: {
      load: function () {
        loads += 1;
        return Promise.resolve();
      },
      openNotificationSettingsModal: function () { return Promise.resolve(); }
    }
  };
  vm.runInNewContext(pageSource, {
    window: { WaslaOrders: O },
    document: {
      getElementById: function () { return null; },
      addEventListener: function (name, fn) {
        if (name === "DOMContentLoaded") ready = fn;
      }
    }
  });
  assert.equal(loads, 0);
  ready();
  assert.equal(loads, 1);
});

test("opening settings without a loaded runtime uses the saved sound setting", async function () {
  const order = [];
  const calls = [];
  const enabled = element({ value: "false" });
  const sound = element({
    value: "bell3",
    selectedIndex: 0,
    options: [{
      value: "bell3",
      textContent: "Bell 3",
      getAttribute: function (name) {
        return name === "data-sound-url" ? "/sounds/bell3.mp3" : null;
      }
    }]
  });
  const repeat = element({ value: "3" });
  const volume = element({ value: "25" });
  const testButton = element();
  const ids = {
    NewOrderSoundEnabled: enabled,
    newOrderSoundSelect: sound,
    NewOrderSoundName: element({ value: "bell3" }),
    NewOrderSoundRepeatCount: repeat,
    NewOrderSoundVolumePercent: volume,
    testSelectedSoundBtn: testButton,
    notificationSettingsModalBody: element(),
    notificationStatusBadge: element(),
    notificationStatusHelp: element(),
    notificationStatusWarning: element(),
    NewOrderHighlightColor: element({ value: "yellow" })
  };
  const O = {
    state: { notificationSettings: null },
    opts: {
      notificationSettingsUrl: "/notification-settings",
      notificationSettingsJsonUrl: "/notification-settings/current"
    },
    debugLog: function () {},
    debugWarn: function () {},
    getMessage: function (key) { return key; },
    showOrdersWarning: function () {},
    audio: {
      syncSoundEnableUi: function () {},
      getAudioState: function () { return "unknown"; },
      isSoundUnlocked: function () { return false; },
      stopCurrentPreviewSound: function () { order.push("stop-preview"); },
      maybeRequestBrowserNotificationPermission: function () {
        order.push("permission");
        return Promise.resolve();
      },
      playSoundNow: function (state, url, kind) {
        order.push("play");
        calls.push({ state: state, url: url, kind: kind });
        return Promise.resolve();
      }
    }
  };
  const context = {
    window: { WaslaOrders: O },
    document: {
      visibilityState: "visible",
      getElementById: function (id) { return ids[id] || null; },
      querySelectorAll: function () { return []; }
    },
    fetch: function (url) {
      if (String(url).indexOf("/current") >= 0) {
        return Promise.resolve({
          ok: true,
          status: 200,
          json: function () {
            return Promise.resolve({
              newOrderSoundEnabled: true,
              newOrderSoundName: "bell3",
              newOrderSoundRepeatCount: 3,
              newOrderSoundVolumePercent: 25,
              showBrowserNotification: false
            });
          }
        });
      }
      return Promise.resolve({
        ok: true,
        status: 200,
        text: function () { return Promise.resolve("<div></div>"); }
      });
    },
    AbortController: AbortController
  };
  context.window.document = context.document;
  vm.runInNewContext(settingsSource, context);

  await O.notificationSettings.openNotificationSettingsModal();
  assert.equal(O.state.notificationSettings.newOrderSoundEnabled, true);
  assert.equal(O.state.notificationSettings.newOrderSoundName, "bell3");
  assert.equal(enabled.value, "true");
  assert.equal(repeat.value, "3");
  assert.equal(volume.value, "25");

  const click = testButton.click();
  await click;
  await Promise.resolve();
  assert.deepEqual(order.slice(0, 3), ["stop-preview", "play", "permission"]);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].kind, "test");
  assert.equal(calls[0].url, "/sounds/bell3.mp3");
  assert.equal(calls[0].state.newOrderSoundEnabled, true);
  assert.equal(calls[0].state.newOrderSoundName, "bell3");
  assert.equal(calls[0].state.newOrderSoundRepeatCount, 3);
  assert.equal(calls[0].state.newOrderSoundVolumePercent, 25);
  assert.equal(O.state.notificationSettings.newOrderSoundEnabled, true);
});
