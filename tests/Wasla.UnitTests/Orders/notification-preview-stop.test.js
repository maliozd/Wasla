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

function stopButton() {
  const classes = new Set(["d-none"]);
  return {
    hidden: true,
    disabled: true,
    classList: {
      add: function (name) { classes.add(name); },
      remove: function (name) { classes.delete(name); },
      contains: function (name) { return classes.has(name); }
    },
    setAttribute: function (name) { if (name === "hidden") this.hidden = true; },
    removeAttribute: function (name) { if (name === "hidden") this.hidden = false; }
  };
}

function idle(button) {
  return button.hidden === true && button.disabled === true;
}

function available(button) {
  return button.hidden === false && button.disabled === false && !button.classList.contains("d-none");
}

function load() {
  const calls = { plays: 0, pauses: 0, urls: [], warnings: [] };
  const live = [];
  const button = stopButton();
  const saved = {
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell3",
    newOrderSoundRepeatCount: 3,
    newOrderSoundVolumePercent: 100
  };
  const O = {
    state: { notificationSettings: saved },
    opts: {},
    debugLog: function () {},
    debugWarn: function () {},
    getMessage: function (key) { return key; },
    showMessage: function () {},
    showOrdersWarning: function (key) { calls.warnings.push(key); }
  };
  function Audio(url) {
    const handlers = {};
    const audio = this;
    this.src = url || "";
    this.volume = 1;
    this.muted = false;
    this.paused = true;
    this.currentTime = 1;
    this.readyState = 1;
    this.networkState = 1;
    this.error = null;
    this.addEventListener = function (name, fn) {
      handlers[name] = handlers[name] || [];
      handlers[name].push(fn);
    };
    this.pause = function () {
      calls.pauses += 1;
      this.paused = true;
      if (this._rejectPlay) {
        const reject = this._rejectPlay;
        this._rejectPlay = null;
        this._resolvePlay = null;
        const error = new Error("The play() request was interrupted by a call to pause().");
        error.name = "AbortError";
        reject(error);
      }
      (handlers.pause || []).slice().forEach(function (fn) { fn(); });
    };
    this.play = function () {
      calls.plays += 1;
      calls.urls.push(this.src);
      this.paused = false;
      live.push(this);
      return new Promise(function (resolve, reject) {
        audio._resolvePlay = resolve;
        audio._rejectPlay = reject;
      });
    };
    this.finish = function () {
      const resolve = this._resolvePlay;
      this._resolvePlay = null;
      this._rejectPlay = null;
      if (resolve) resolve();
      (handlers.ended || []).slice().forEach(function (fn) { fn(); });
    };
  }
  const context = {
    window: { WaslaOrders: O },
    document: {
      getElementById: function (id) {
        return id === "notificationPreviewStopSound" ? button : null;
      },
      addEventListener: function () {},
      removeEventListener: function () {}
    },
    Audio: Audio,
    localStorage: { setItem: function () {}, getItem: function () { return null; } },
    queueMicrotask: queueMicrotask
  };
  context.window.document = context.document;
  vm.runInNewContext(audioSource, context);
  return {
    O: O,
    calls: calls,
    button: button,
    saved: saved,
    finishLatest: function () {
      const audio = live[live.length - 1];
      if (audio && audio.finish) audio.finish();
    }
  };
}

async function flush() {
  for (let i = 0; i < 8; i++) await Promise.resolve();
}

const previewUrl = "/sounds/bell3.mp3";

test("stop during iteration 1 cancels the remaining repeats", async function () {
  const h = load();
  assert.equal(idle(h.button), true);
  const playback = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  assert.equal(h.calls.plays, 1);
  assert.equal(available(h.button), true);
  h.O.audio.stopTestPreview();
  await playback;
  await flush();
  assert.equal(h.calls.plays, 1);
  assert.equal(h.calls.urls[0], previewUrl);
  assert.equal(idle(h.button), true);
  assert.equal(h.calls.warnings.indexOf("audio-play-failed"), -1);
  assert.equal(h.saved.newOrderSoundEnabled, true);
  assert.equal(h.saved.newOrderSoundRepeatCount, 3);
  assert.equal(h.saved.newOrderSoundName, "bell3");
  assert.equal(h.saved.newOrderSoundVolumePercent, 100);
});

test("stop during a later iteration cancels the repeats that remain", async function () {
  const h = load();
  const playback = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  assert.equal(h.calls.plays, 1);
  h.finishLatest();
  await flush();
  assert.equal(h.calls.plays, 2);
  h.O.audio.stopTestPreview();
  await playback;
  await flush();
  assert.equal(h.calls.plays, 2);
  assert.equal(idle(h.button), true);
});

test("Test then Stop then Test plays the second preview", async function () {
  const h = load();
  const first = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  h.O.audio.stopTestPreview();
  await first;
  const again = {
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell1",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 50
  };
  const second = h.O.audio.playSoundNow(again, "/sounds/bell1.mp3", "test");
  assert.equal(h.calls.plays, 2);
  assert.equal(h.calls.urls[1], "/sounds/bell1.mp3");
  assert.equal(available(h.button), true);
  h.finishLatest();
  await second;
  assert.equal(idle(h.button), true);
  assert.equal(h.saved.newOrderSoundEnabled, true);
  assert.equal(h.saved.newOrderSoundName, "bell3");
});

test("starting Test again cancels the previous preview", async function () {
  const h = load();
  const first = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  const second = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  await first;
  await flush();
  assert.equal(h.calls.plays, 2);
  assert.equal(available(h.button), true);
  h.O.audio.stopTestPreview();
  await second;
  assert.equal(h.calls.plays, 2);
  assert.equal(idle(h.button), true);
});

test("stopping a preview does not disable future new-order sound", async function () {
  const h = load();
  const preview = h.O.audio.playSoundNow(h.saved, previewUrl, "test");
  h.O.audio.stopTestPreview();
  await preview;
  const production = h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: h.saved.newOrderSoundName,
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: h.saved.newOrderSoundVolumePercent
  });
  await flush();
  assert.equal(h.calls.plays, 2);
  assert.equal(idle(h.button), true);
  assert.equal(h.saved.newOrderSoundEnabled, true);
  assert.equal(h.saved.newOrderSoundRepeatCount, 3);
  h.finishLatest();
  await production;
  assert.equal(h.calls.plays, 2);
  assert.equal(h.saved.newOrderSoundEnabled, true);
});

test("Stop Sound is idle, available during preview, then idle again", async function () {
  const h = load();
  assert.equal(idle(h.button), true);
  const playback = h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell3",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 100
  }, previewUrl, "test");
  assert.equal(available(h.button), true);
  h.finishLatest();
  await playback;
  assert.equal(idle(h.button), true);
});

test("closing the modal stops the active preview", async function () {
  let closed = false;
  const calls = [];
  const listeners = {};
  function field(extra) {
    return Object.assign({
      value: "",
      checked: false,
      textContent: "",
      className: "",
      hidden: false,
      selectedIndex: 0,
      options: [],
      style: {},
      classList: { add: function () {}, remove: function () {}, contains: function () { return false; }, toggle: function () {} },
      addEventListener: function (type, fn) {
        listeners[type] = listeners[type] || [];
        listeners[type].push(fn);
      },
      getAttribute: function () { return null; }
    }, extra || {});
  }
  const modal = field();
  const ids = {
    notificationSettingsModal: modal,
    notificationSettingsModalBody: field(),
    notificationStatusBadge: field(),
    notificationStatusHelp: field(),
    notificationStatusWarning: field(),
    NewOrderSoundEnabled: field({ value: "true" }),
    newOrderSoundSelect: field({ value: "bell3", options: [{ value: "bell3", textContent: "Bell 3", getAttribute: function () { return "/sounds/bell3.mp3"; } }] }),
    NewOrderSoundName: field({ value: "bell3" }),
    NewOrderSoundRepeatCount: field({ value: "3" }),
    NewOrderSoundVolumePercent: field({ value: "100" }),
    NewOrderHighlightColor: field({ value: "yellow" }),
    testSelectedSoundBtn: field(),
    notificationPreviewStopSound: field()
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
      stopCurrentPreviewSound: function () { calls.push("stop-preview"); },
      stopTestPreview: function () { calls.push("stop-test-preview"); closed = true; },
      maybeRequestBrowserNotificationPermission: function () { return Promise.resolve(); },
      playSoundNow: function () { return Promise.resolve(); }
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
              newOrderSoundVolumePercent: 100,
              showBrowserNotification: false
            });
          }
        });
      }
      return Promise.resolve({ ok: true, status: 200, text: function () { return Promise.resolve("<div></div>"); } });
    },
    AbortController: AbortController
  };
  context.window.document = context.document;
  vm.runInNewContext(settingsSource, context);
  await O.notificationSettings.openNotificationSettingsModal();
  (listeners["hidden.bs.modal"] || []).forEach(function (fn) { fn(); });
  assert.equal(closed, true);
  assert.deepEqual(calls, ["stop-test-preview"]);
});
