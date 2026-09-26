const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const audioSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-audio.js"),
  "utf8"
);
const settingsSource = fs.readFileSync(
  require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-notification-settings.js"),
  "utf8"
);

function element() {
  const classes = new Set();
  return {
    className: "",
    textContent: "",
    value: "",
    hidden: true,
    classList: {
      add: function (name) { classes.add(name); },
      remove: function (name) { classes.delete(name); },
      contains: function (name) { return classes.has(name); }
    }
  };
}

function load(playback) {
  const ids = {
    notificationStatusBadge: element(),
    notificationStatusHelp: element(),
    notificationStatusWarning: element()
  };
  const O = {
    state: {
      notificationSettings: {
        newOrderSoundEnabled: true,
        newOrderSoundName: "bell3",
        newOrderSoundRepeatCount: 1,
        newOrderSoundVolumePercent: 100
      }
    },
    opts: {},
    debugLog: function () {},
    debugWarn: function () {},
    getMessage: function (key) { return key; },
    showMessage: function () {},
    showOrdersWarning: function () {}
  };
  function Audio() {
    this.volume = 1;
    this.currentTime = 0;
    this.muted = false;
    this.src = "";
    this.paused = true;
    this.readyState = 0;
    this.networkState = 0;
    this.error = null;
    const handlers = {};
    this.addEventListener = function (name, fn) {
      handlers[name] = handlers[name] || [];
      handlers[name].push(fn);
    };
    this.pause = function () { this.paused = true; };
    this.play = function () {
      if (playback === "deny") {
        const error = new Error("not allowed");
        error.name = "NotAllowedError";
        return Promise.reject(error);
      }
      const self = this;
      return Promise.resolve().then(function () {
        self.paused = false;
        (handlers.ended || []).forEach(function (fn) { fn(); });
      });
    };
  }
  const context = {
    window: { WaslaOrders: O, setTimeout: function (fn) { fn(); return 0; } },
    document: {
      visibilityState: "visible",
      getElementById: function (id) { return ids[id] || null; },
      querySelectorAll: function () { return []; },
      addEventListener: function () {},
      removeEventListener: function () {}
    },
    Audio: Audio,
    localStorage: { setItem: function () {}, getItem: function () { return null; } },
    AbortController: AbortController,
    setTimeout: function (fn) { fn(); return 0; }
  };
  context.window.document = context.document;
  vm.runInNewContext(audioSource, context);
  vm.runInNewContext(settingsSource, context);
  return { O: O, badge: ids.notificationStatusBadge };
}

function readLabel(h) {
  h.O.notificationSettings.updateNotificationStatusUi(h.O.state.notificationSettings);
  return {
    text: h.badge.textContent,
    warning: h.badge.className.indexOf("text-bg-warning") >= 0,
    audioState: h.O.audio.getAudioState()
  };
}

test("unknown audio state does not show the browser-audio warning", function () {
  const h = load("ok");
  const label = readLabel(h);
  assert.equal(h.O.audio.isSoundUnlocked(), false);
  assert.equal(label.audioState, "unknown");
  assert.equal(label.warning, false);
  assert.equal(label.text, "notificationsOn");
});

test("allowed audio state keeps the browser-audio warning hidden", function () {
  const h = load("ok");
  h.O.audio.markSoundUnlocked();
  const label = readLabel(h);
  assert.equal(label.audioState, "allowed");
  assert.equal(label.warning, false);
  assert.equal(label.text, "notificationsOn");
});

test("blocked audio state shows the browser-audio warning", async function () {
  const h = load("deny");
  await h.O.audio.playSoundNow(h.O.state.notificationSettings, "/sounds/bell3.mp3", "test");
  const label = readLabel(h);
  assert.equal(label.audioState, "blocked");
  assert.equal(h.O.audio.isSoundUnlocked(), false);
  assert.equal(label.warning, true);
  assert.equal(label.text, "waitingForAudio");
});

test("disabled sound hides the browser-audio warning", async function () {
  const h = load("deny");
  await h.O.audio.playSoundNow(h.O.state.notificationSettings, "/sounds/bell3.mp3", "test");
  h.O.state.notificationSettings.newOrderSoundEnabled = false;
  const label = readLabel(h);
  assert.equal(h.O.audio.getAudioState(), "blocked");
  assert.equal(label.warning, false);
  assert.equal(label.text, "notificationsOff");
});

test("successful preview marks audio allowed and hides the warning", async function () {
  const h = load("ok");
  await h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell3",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 50
  }, "/sounds/bell3.mp3", "test");
  const label = readLabel(h);
  assert.equal(label.audioState, "allowed");
  assert.equal(label.warning, false);
  assert.equal(label.text, "notificationsOn");
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, true);
  assert.equal(h.O.state.notificationSettings.newOrderSoundVolumePercent, 100);
});

test("NotAllowedError marks audio blocked and shows the warning", async function () {
  const h = load("deny");
  assert.equal(readLabel(h).warning, false);
  await h.O.audio.playSoundNow(h.O.state.notificationSettings, "/sounds/bell3.mp3", "test");
  const label = readLabel(h);
  assert.equal(label.audioState, "blocked");
  assert.equal(label.warning, true);
  assert.equal(label.text, "waitingForAudio");
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, true);
});
