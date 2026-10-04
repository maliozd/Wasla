const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const audioSource = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-audio.js"), "utf8");
const viewSource = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-view.js"), "utf8");

function visible(el) {
  return !!el && el.hidden === false && !el.classList.contains("d-none");
}

function element() {
  const classes = new Set(["d-none"]);
  return {
    hidden: true,
    dataset: {},
    classList: {
      add: function (name) { classes.add(name); },
      remove: function (name) { classes.delete(name); },
      contains: function (name) { return classes.has(name); }
    },
    setAttribute: function (name) { if (name === "hidden") this.hidden = true; },
    removeAttribute: function (name) { if (name === "hidden") this.hidden = false; },
    addEventListener: function (type, fn) { this["on" + type] = fn; }
  };
}

function load(options) {
  const mode = options.playback || "ok";
  const calls = { created: 0, plays: 0, warnings: [], sources: [], volumes: [] };
  const listeners = [];
  const settings = Object.prototype.hasOwnProperty.call(options, "settings")
    ? options.settings
    : {
      newOrderSoundEnabled: true,
      newOrderSoundName: "bell1",
      newOrderSoundRepeatCount: 1,
      newOrderSoundVolumePercent: 80
    };
  const storage = options.storage || { Wasla: {} };
  const els = {
    ordersLiveDisplaySoundBanner: element(),
    ordersLiveDisplaySoundActive: element(),
    ordersLiveDisplayEnableNotificationSound: element(),
    ordersLiveDisplayStopSound: element()
  };
  const O = {
    state: { notificationSettings: settings },
    opts: {},
    debugWarn: function () {},
    debugLog: function () {},
    getMessage: function (key) { return key; },
    showMessage: function () {},
    showOrdersWarning: function (key) { calls.warnings.push(key); }
  };
  function Audio(url) {
    calls.created += 1;
    this.src = url || "";
    this.volume = 1;
    this.currentTime = 0;
    this.muted = false;
    this.error = null;
    const handlers = {};
    this.addEventListener = function (name, fn) {
      handlers[name] = handlers[name] || [];
      handlers[name].push(fn);
    };
    const self = this;
    let playSettled = false;
    this.pause = function () {
      calls.pauses = (calls.pauses || 0) + 1;
      if (mode === "race" && !playSettled) {
        const error = new Error("The play() request was interrupted by a call to pause().");
        error.name = "AbortError";
        if (self._rejectPlay) {
          self._rejectPlay(error);
        }
      }
    };
    this.play = function () {
      calls.plays += 1;
      calls.sources.push(this.src);
      calls.volumes.push(this.volume);
      if (mode === "deny") {
        const error = new Error("not allowed");
        error.name = "NotAllowedError";
        return Promise.reject(error);
      }
      if (mode === "missing" || mode === "abort") {
        const error = new Error(mode === "abort" ? "aborted" : "missing media");
        error.name = mode === "abort" ? "AbortError" : "NotSupportedError";
        return Promise.reject(error);
      }
      return new Promise(function (resolve, reject) {
        self._rejectPlay = reject;
        Promise.resolve().then(function () {
          if (playSettled) {
            return;
          }
          playSettled = true;
          (handlers.ended || []).forEach(function (fn) { fn(); });
          resolve();
        });
      });
    };
  }
  const document = {
    getElementById: function (id) { return els[id] || null; },
    addEventListener: function (type, fn, capture) {
      listeners.push({ type: type, fn: fn, capture: !!(capture && (capture === true || capture.capture)) });
    },
    removeEventListener: function (type, fn) {
      for (let i = listeners.length - 1; i >= 0; i--) {
        if (listeners[i].type === type && listeners[i].fn === fn) listeners.splice(i, 1);
      }
    }
  };
  const context = {
    window: { WaslaOrders: O },
    document: document,
    Audio: Audio,
    localStorage: {
      setItem: function (key, value) { storage.Wasla[key] = value; },
      getItem: function (key) { return Object.prototype.hasOwnProperty.call(storage.Wasla, key) ? storage.Wasla[key] : null; }
    },
    setTimeout: function (fn) { fn(); return 0; }
  };
  context.window.document = document;
  context.window.setTimeout = context.setTimeout;
  vm.runInNewContext(audioSource, context);
  return {
    O: O,
    els: els,
    calls: calls,
    storage: storage,
    fire: function (type) {
      listeners.slice().forEach(function (listener) {
        if (listener.type === type) {
          listener.fn({ type: type, target: { closest: function () { return null; } } });
        }
      });
    },
    listenerCount: function () { return listeners.length; }
  };
}

function playbackState() {
  return {
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell1",
    newOrderSoundRepeatCount: 2,
    newOrderSoundVolumePercent: 80
  };
}

test("sound disabled does not show the permission banner or play audio", async function () {
  const h = load({ settings: { newOrderSoundEnabled: false, newOrderSoundName: "bell1" } });
  h.O.audio.initAudioUnlock();
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  await h.O.audio.playSoundNow(playbackState());
  await h.O.audio.playSoundNow({ newOrderSoundEnabled: false, newOrderSoundName: "bell1" });
  assert.equal(h.calls.created, 0);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
});

test("sound enabled with unknown runtime state does not show the permission banner", function () {
  const h = load({});
  h.O.audio.initAudioUnlock();
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.O.audio.isSoundUnlocked(), false);
});

test("a new document starts unknown and ignores a previously stored unlock flag", function () {
  const storage = { Wasla: { "Wasla.soundUnlocked": "true" } };
  const first = load({ storage: storage });
  first.O.audio.initAudioUnlock();
  assert.equal(first.O.audio.getAudioState(), "unknown");
  assert.equal(visible(first.els.ordersLiveDisplaySoundBanner), false);

  const second = load({ storage: storage });
  second.O.audio.initAudioUnlock();
  assert.equal(second.O.audio.getAudioState(), "unknown");
  assert.equal(visible(second.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(second.O.audio.isSoundUnlocked(), false);
});

test("successful playback marks audio allowed and keeps the banner hidden", async function () {
  const h = load({ playback: "ok" });
  h.O.audio.initAudioUnlock();
  await h.O.audio.playSoundNow(playbackState());
  assert.equal(h.O.audio.getAudioState(), "allowed");
  assert.equal(h.O.audio.isSoundUnlocked(), true);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.calls.plays, 2);
});

test("NotAllowedError marks audio blocked and shows the permission banner", async function () {
  const h = load({ playback: "deny" });
  h.O.audio.initAudioUnlock();
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  await h.O.audio.playSoundNow(playbackState());
  assert.equal(h.O.audio.getAudioState(), "blocked");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), true);
  assert.equal(h.calls.plays, 1);
  assert.equal(h.calls.warnings.indexOf("audio-not-allowed") >= 0, true);
});

test("a non-permission playback failure does not show the permission banner", async function () {
  for (const mode of ["missing", "abort"]) {
    const h = load({ playback: mode });
    h.O.audio.initAudioUnlock();
    await h.O.audio.playSoundNow(playbackState());
    assert.equal(h.O.audio.getAudioState(), "unknown", mode);
    assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false, mode);
    assert.equal(h.calls.warnings.indexOf("audio-not-allowed"), -1, mode);
    assert.equal(h.calls.warnings.indexOf("audio-play-failed") >= 0, true, mode);
  }
});

test("the enable-sound click unlocks in the click path and hides the banner", async function () {
  const h = load({ playback: "deny" });
  h.O.audio.initAudioUnlock();
  await h.O.audio.playSoundNow(playbackState());
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), true);

  h.calls.plays = 0;
  const context = load({ playback: "ok", settings: h.O.state.notificationSettings });
  context.O.audio.initAudioUnlock();
  context.els.ordersLiveDisplayEnableNotificationSound.onclick({
    preventDefault: function () {},
    stopPropagation: function () {}
  });
  for (let i = 0; i < 5; i++) await Promise.resolve();
  assert.equal(context.O.audio.getAudioState(), "allowed");
  assert.equal(visible(context.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(context.calls.plays, 1);
});

test("repeated sync does not change runtime audio state the way a poll must not", function () {
  const h = load({});
  h.O.audio.initAudioUnlock();
  h.O.audio.syncSoundEnableUi();
  h.O.audio.syncSoundEnableUi();
  h.O.audio.syncSoundEnableUi();
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.calls.plays, 0);
});

test("Board List and Focus switching does not reset or retrigger audio permission", function () {
  let plays = 0;
  let stateReads = 0;
  let init;
  const O = {
    opts: { pageMode: "liveDisplay" },
    liveStore: { refreshView: function () {} },
    audio: {
      getAudioState: function () { stateReads += 1; return "allowed"; },
      playSoundNow: function () { plays += 1; },
      syncSoundEnableUi: function () { plays += 1; },
      initAudioUnlock: function () { plays += 1; }
    }
  };
  vm.runInNewContext(viewSource, {
    window: { WaslaOrders: O },
    document: {
      getElementById: function () { return null; },
      querySelectorAll: function () { return []; },
      addEventListener: function (name, fn) { if (name === "DOMContentLoaded") init = fn; }
    },
    localStorage: { getItem: function () { return "board"; }, setItem: function () {} }
  });
  init();
  O.liveView.setView("list");
  O.liveView.setView("focus");
  O.liveView.setView("board");
  assert.equal(plays, 0);
  assert.equal(stateReads, 0);
  assert.equal(O.liveView.getView(), "board");
});

test("turning sound off hides the permission banner", async function () {
  const h = load({ playback: "deny" });
  h.O.audio.initAudioUnlock();
  await h.O.audio.playSoundNow(playbackState());
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), true);
  h.O.state.notificationSettings.newOrderSoundEnabled = false;
  h.O.audio.syncSoundEnableUi();
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  const plays = h.calls.plays;
  await h.O.audio.playSoundNow(playbackState());
  assert.equal(h.calls.plays, plays);
});

test("turning sound on does not assume the browser has blocked audio", function () {
  const h = load({ settings: { newOrderSoundEnabled: false, newOrderSoundName: "bell1" } });
  h.O.audio.initAudioUnlock();
  h.O.state.notificationSettings.newOrderSoundEnabled = true;
  h.O.audio.syncSoundEnableUi();
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
});

test("a gesture that the browser still denies does not show the permission banner", async function () {
  const h = load({ playback: "deny" });
  h.O.audio.initAudioUnlock();
  h.fire("pointerdown");
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.listenerCount(), 0);
});

test("preview plays the modal selection when saved sound is disabled", async function () {
  const h = load({ settings: { newOrderSoundEnabled: false, newOrderSoundName: "bell1" } });
  const modal = {
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell3",
    newOrderSoundRepeatCount: 3,
    newOrderSoundVolumePercent: 25
  };
  await h.O.audio.playSoundNow(modal);
  assert.equal(h.calls.created, 0);
  await h.O.audio.playSoundNow(modal, "/sounds/bell3.mp3", "test");
  assert.equal(h.calls.plays, 3);
  assert.deepEqual(h.calls.sources, ["/sounds/bell3.mp3", "/sounds/bell3.mp3", "/sounds/bell3.mp3"]);
  assert.deepEqual(h.calls.volumes, [0.25, 0.25, 0.25]);
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, false);
  assert.equal(h.O.audio.getAudioState(), "allowed");
  assert.equal(h.O.audio.isSoundUnlocked(), true);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
});

test("preview plays when the settings page has not loaded runtime settings", async function () {
  const h = load({ settings: null });
  await h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell1",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 100
  });
  assert.equal(h.calls.created, 0);
  await h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell1",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 100
  }, "/sounds/bell1.mp3", "test");
  assert.equal(h.calls.plays, 1);
  assert.equal(h.calls.sources[0], "/sounds/bell1.mp3");
  assert.equal(h.calls.volumes[0], 1);
  assert.equal(h.O.audio.getAudioState(), "allowed");
  assert.equal(h.O.state.notificationSettings, null);
});

test("preview NotAllowedError stays blocked and does not enable saved sound", async function () {
  const h = load({
    playback: "deny",
    settings: { newOrderSoundEnabled: false, newOrderSoundName: "bell1" }
  });
  await h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell3",
    newOrderSoundRepeatCount: 3,
    newOrderSoundVolumePercent: 100
  }, "/sounds/bell3.mp3", "test");
  assert.equal(h.calls.plays, 1);
  assert.equal(h.O.audio.getAudioState(), "blocked");
  assert.equal(h.O.audio.isSoundUnlocked(), false);
  assert.equal(h.O.state.notificationSettings.newOrderSoundEnabled, false);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.calls.warnings.indexOf("audio-not-allowed") >= 0, true);
});

test("preview AbortError stays unknown and is not treated as disabled", async function () {
  const h = load({
    playback: "abort",
    settings: { newOrderSoundEnabled: false, newOrderSoundName: "bell1" }
  });
  await h.O.audio.playSoundNow({
    newOrderSoundEnabled: true,
    newOrderSoundName: "bell1",
    newOrderSoundRepeatCount: 1,
    newOrderSoundVolumePercent: 50
  }, "/sounds/bell1.mp3", "test");
  assert.equal(h.calls.plays, 1);
  assert.equal(h.O.audio.getAudioState(), "unknown");
  assert.equal(h.O.audio.isSoundUnlocked(), false);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.calls.warnings.indexOf("audio-not-allowed"), -1);
  assert.equal(h.calls.warnings.indexOf("audio-play-failed") >= 0, true);
});

test("gesture unlock waits for play() to settle before pause", async function () {
  const h = load({ playback: "race" });
  h.O.audio.initAudioUnlock();
  h.fire("pointerdown");
  for (let i = 0; i < 8; i++) await Promise.resolve();
  assert.equal(h.O.audio.getAudioState(), "allowed");
  assert.equal(h.calls.pauses >= 1, true);
  assert.equal(visible(h.els.ordersLiveDisplaySoundBanner), false);
  assert.equal(h.calls.warnings.indexOf("audio-not-allowed"), -1);
});
