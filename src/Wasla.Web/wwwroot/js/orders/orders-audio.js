// Sound playback, preview, unlock, and browser notifications (depends on WaslaOrders).
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) {
    return;
  }

  const SOUND_UNLOCK_KEY = "Wasla.soundUnlocked";
  const AUDIO_UNKNOWN = "unknown";
  const AUDIO_ALLOWED = "allowed";
  const AUDIO_BLOCKED = "blocked";

  let currentPreviewAudio = null;
  let currentAlertAudio = null;
  let sessionUnlocked = false;
  let audioState = AUDIO_UNKNOWN;
  let soundPreferenceEnabled = false;
  let unlockPromptShown = false;
  let unlockGesturesBound = false;
  let unlockHandler = null;
  let alertPlaybackToken = 0;
  let testPreviewActive = false;

  function persistUnlockFlag() {
    try {
      localStorage.setItem(SOUND_UNLOCK_KEY, "true");
    } catch (error) {
      /* ignore quota / private mode */
    }
  }

  function isSoundUnlocked() {
    return sessionUnlocked === true;
  }

  function isPermissionDenial(error) {
    return !!(error && error.name === "NotAllowedError");
  }

  function isSoundEnabled() {
    const settings = O.state && O.state.notificationSettings;
    return !!(settings && settings.newOrderSoundEnabled);
  }

  function markSoundUnlocked() {
    sessionUnlocked = true;
    audioState = AUDIO_ALLOWED;
    persistUnlockFlag();
    detachUnlockGestures();
  }

  function stopAudioElement(audio) {
    if (!audio) {
      return;
    }
    try {
      audio.pause();
      audio.currentTime = 0;
    } catch (error) {
      O.debugWarn("stopAudioElement failed", error);
    }
  }

  function stopCurrentPreviewSound() {
    stopAudioElement(currentPreviewAudio);
    currentPreviewAudio = null;
  }

  function stopCurrentAlertSound() {
    stopAudioElement(currentAlertAudio);
    currentAlertAudio = null;
  }

  function setElementVisible(el, visible) {
    if (!el) {
      return;
    }
    if (visible) {
      el.classList.remove("d-none");
      el.hidden = false;
      el.removeAttribute("hidden");
    } else {
      el.classList.add("d-none");
      el.hidden = true;
      el.setAttribute("hidden", "hidden");
    }
  }

  function setStopSoundVisible(visible) {
    setElementVisible(document.getElementById("ordersLiveDisplayStopSound"), visible);
  }

  function setPreviewStopVisible(visible) {
    const el = document.getElementById("notificationPreviewStopSound");
    setElementVisible(el, visible);
    if (el) {
      el.disabled = !visible;
    }
  }

  function stopCurrentSound() {
    alertPlaybackToken += 1;
    stopCurrentPreviewSound();
    stopCurrentAlertSound();
    setStopSoundVisible(false);
    testPreviewActive = false;
    setPreviewStopVisible(false);
  }

  function stopTestPreview() {
    if (!testPreviewActive) {
      stopCurrentPreviewSound();
      setPreviewStopVisible(false);
      return;
    }
    stopCurrentSound();
  }

  function syncSoundPreferenceStatus(enabled, settingsKnown) {
    const status = document.getElementById("ordersLiveDisplaySoundStatus");
    if (!status || !settingsKnown || typeof status.getAttribute !== "function") {
      return;
    }
    status.textContent = enabled
      ? (status.getAttribute("data-label-active") || "")
      : (status.getAttribute("data-label-disabled") || "");
    status.classList.toggle("wasla-orders-status-chip--active", enabled);
    status.classList.toggle("wasla-orders-status-chip--muted", !enabled);
  }

  function syncSoundEnableUi() {
    const enabled = isSoundEnabled();
    const settingsKnown = !!(O.state && O.state.notificationSettings);
    const showBanner = enabled && audioState === AUDIO_BLOCKED;
    const liveBanner = document.getElementById("ordersLiveDisplaySoundBanner");
    const liveActive = document.getElementById("ordersLiveDisplaySoundActive");
    const liveEnableBtn = document.getElementById("ordersLiveDisplayEnableNotificationSound");

    if (!enabled && soundPreferenceEnabled) {
      stopCurrentSound();
    }

    setElementVisible(liveBanner, showBanner);
    setElementVisible(liveEnableBtn, showBanner);
    syncSoundPreferenceStatus(enabled, settingsKnown);
    soundPreferenceEnabled = enabled;

    const unlocked = isSoundUnlocked();
    setElementVisible(liveActive, enabled && unlocked);

    if (enabled && unlocked && liveActive && !liveActive.dataset.fadeScheduled) {
      liveActive.dataset.fadeScheduled = "1";
      global.setTimeout(function () {
        if (isSoundUnlocked()) {
          setElementVisible(liveActive, false);
        }
      }, 4000);
    }

    if (enabled && audioState !== AUDIO_ALLOWED) {
      ensureUnlockGestures();
    } else {
      detachUnlockGestures();
    }
  }

  function showUnlockPromptOnce() {
    syncSoundEnableUi();
    if (unlockPromptShown) {
      return;
    }
    unlockPromptShown = true;
    const message = O.getMessage("enableNotificationSound") || O.getMessage("soundUnlockHint");
    if (global.WaslaToast && typeof global.WaslaToast.info === "function") {
      global.WaslaToast.info(message, { key: "sound-unlock-hint", durationMs: 8000 });
      return;
    }
    O.showMessage(message, "info");
  }

  function handlePlayRejection(error) {
    O.debugWarn("audio.play() rejected", error);
    if (isPermissionDenial(error)) {
      audioState = AUDIO_BLOCKED;
      showUnlockPromptOnce();
      O.showOrdersWarning("audio-not-allowed", O.getMessage("audioNotAllowed"));
      syncSoundEnableUi();
      return true;
    }
    O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
    syncSoundEnableUi();
    return false;
  }

  function getSoundUrlFromSettings(name) {
    const n = (name || "bell1").toLowerCase();
    const list = (O.state.notificationSettings && O.state.notificationSettings.availableSounds) || [];
    const found = list.find(function (x) {
      return (x.name || "").toLowerCase() === n;
    });
    if (found && found.url) {
      return found.url;
    }
    return "/sounds/" + n + ".mp3";
  }

  function readVolumePercentFromNotificationModal() {
    const el = document.getElementById("NewOrderSoundVolumePercent");
    const volPct = parseInt((el && el.value) || "100", 10);
    if (isNaN(volPct)) {
      return 100;
    }
    return volPct;
  }

  async function trySilentUnlock(url) {
    if (!isSoundEnabled() || audioState === AUDIO_ALLOWED) {
      return false;
    }
    const audio = new Audio(url || getSoundUrlFromSettings("bell1"));
    audio.volume = 0.01;
    try {
      await audio.play();
    } catch (error) {
      stopAudioElement(audio);
      O.debugWarn("silent audio unlock failed", error);
      return false;
    }
    stopAudioElement(audio);
    markSoundUnlocked();
    unlockPromptShown = true;
    syncSoundEnableUi();
    return true;
  }

  async function enableNotificationSoundFromControl() {
    const st = O.state.notificationSettings || {};
    const soundName = st.newOrderSoundName || "bell1";
    const url = getSoundUrlFromSettings(soundName);
    const pct = st.newOrderSoundVolumePercent != null
      ? st.newOrderSoundVolumePercent
      : Math.round((st.newOrderSoundVolume || 1) * 100);
    const vol = Math.max(0.15, Math.min(1, (pct || 100) / 100.0));

    stopCurrentSound();
    try {
      const audio = new Audio(url);
      audio.volume = vol;
      currentPreviewAudio = audio;
      await audio.play();
      markSoundUnlocked();
      unlockPromptShown = true;
      syncSoundEnableUi();
      if (O.notificationSettings && typeof O.notificationSettings.updateNotificationStatusUi === "function") {
        try {
          O.notificationSettings.updateNotificationStatusUi(st);
        } catch (error) {
          /* ignore */
        }
      }
      return true;
    } catch (error) {
      handlePlayRejection(error);
      return false;
    }
  }

  function wireSoundEnableControls() {
    const btn = document.getElementById("ordersLiveDisplayEnableNotificationSound");
    if (btn && btn.dataset.soundEnableWired !== "1") {
      btn.dataset.soundEnableWired = "1";
      btn.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        enableNotificationSoundFromControl().catch(function (error) {
          O.debugWarn("enableNotificationSoundFromControl failed", error);
        });
      });
    }

    const stopBtn = document.getElementById("ordersLiveDisplayStopSound");
    if (stopBtn && stopBtn.dataset.soundStopWired !== "1") {
      stopBtn.dataset.soundStopWired = "1";
      stopBtn.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        stopCurrentSound();
      });
    }
  }

  function detachUnlockGestures() {
    if (unlockHandler && typeof document !== "undefined" && typeof document.removeEventListener === "function") {
      ["pointerdown", "keydown", "touchstart"].forEach(function (evt) {
        document.removeEventListener(evt, unlockHandler, true);
      });
    }
    unlockHandler = null;
    unlockGesturesBound = false;
  }

  function ensureUnlockGestures() {
    if (unlockGesturesBound || typeof document === "undefined" || typeof document.addEventListener !== "function") {
      return;
    }
    if (!isSoundEnabled() || audioState === AUDIO_ALLOWED) {
      return;
    }
    unlockGesturesBound = true;

    unlockHandler = function (event) {
      if (event && event.target && event.target.closest && event.target.closest(".wasla-orders-sound-enable-btn, #ordersLiveDisplayStopSound, #notificationPreviewStopSound")) {
        return;
      }
      detachUnlockGestures();
      trySilentUnlock().then(function (ok) {
        if (ok && O.notificationSettings && typeof O.notificationSettings.updateNotificationStatusUi === "function") {
          try {
            O.notificationSettings.updateNotificationStatusUi(O.state.notificationSettings || {});
          } catch (error) {
            /* ignore */
          }
        }
      });
    };

    ["pointerdown", "keydown", "touchstart"].forEach(function (evt) {
      document.addEventListener(evt, unlockHandler, { capture: true, passive: true });
    });
  }

  function bindUnlockGestures() {
    ensureUnlockGestures();
  }

  async function playSoundNow(state, urlOverride, playbackDiag) {
    const testDiag = playbackDiag === "test";
    if (!testDiag && (!isSoundEnabled() || !state || !state.newOrderSoundEnabled)) {
      return;
    }
    if (!state) {
      if (testDiag) {
        O.debugWarn("test playback skipped", { reason: "missing-preview-state" });
      }
      return;
    }
    const repeat = Math.max(1, Math.min(3, state.newOrderSoundRepeatCount || 1));
    const pct = state.newOrderSoundVolumePercent;
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const url = urlOverride || getSoundUrlFromSettings(state.newOrderSoundName);
    stopCurrentSound();
    if (testDiag) {
      testPreviewActive = true;
    }
    const token = alertPlaybackToken;
    let denied = false;

    O.debugLog("Playing new order sound", {
      enabled: !!state.newOrderSoundEnabled,
      soundUnlocked: isSoundUnlocked(),
      soundName: state.newOrderSoundName,
      url: url,
      repeat: repeat,
      volume: vol
    });

    for (let i = 0; i < repeat; i++) {
      if (token !== alertPlaybackToken) {
        return;
      }
      if (testDiag) {
        setPreviewStopVisible(true);
      }
      try {
        const audio = new Audio(url);
        audio.volume = vol;
        currentAlertAudio = audio;
        setStopSoundVisible(true);
        audio.addEventListener("error", function () {
          O.debugWarn("Audio load/playback error", {
            url: audio.src,
            code: audio.error && audio.error.code,
            message: audio.error && audio.error.message
          });
          O.showOrdersWarning("audio-error", O.getMessage("audioFileError"));
        }, { once: true });

        await new Promise(function (resolve) {
          let settled = false;
          function done() {
            if (settled) {
              return;
            }
            settled = true;
            resolve();
          }

          audio.addEventListener("ended", done, { once: true });
          audio.addEventListener("error", done, { once: true });
          audio.addEventListener("pause", function () {
            if (token !== alertPlaybackToken) {
              done();
            }
          });
          const playResult = audio.play();
          if (playResult && typeof playResult.then === "function") {
            playResult.then(function () {
              if (token !== alertPlaybackToken) {
                return;
              }
              markSoundUnlocked();
              syncSoundEnableUi();
            }).catch(function (error) {
              if (token !== alertPlaybackToken) {
                done();
                return;
              }
              denied = handlePlayRejection(error);
              done();
            });
          }
        });

        if (denied || token !== alertPlaybackToken) {
          break;
        }

        if (currentAlertAudio === audio) {
          currentAlertAudio = null;
        }
      } catch (error) {
        O.debugWarn("playSoundNow failed", error);
        if (!isPermissionDenial(error)) {
          O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
        }
      }
    }

    if (token === alertPlaybackToken) {
      setStopSoundVisible(false);
      if (testDiag) {
        testPreviewActive = false;
        setPreviewStopVisible(false);
      }
    }
  }

  async function playSoundPreview(soundName, soundUrl) {
    stopCurrentSound();
    const pct = readVolumePercentFromNotificationModal();
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const url = soundUrl || getSoundUrlFromSettings(soundName);
    try {
      const audio = new Audio(url);
      audio.volume = vol;
      currentPreviewAudio = audio;
      audio.addEventListener("error", function () {
        O.debugWarn("Audio preview error", {
          url: audio.src,
          code: audio.error && audio.error.code,
          message: audio.error && audio.error.message
        });
        O.showOrdersWarning("audio-error", O.getMessage("audioFileError"));
      }, { once: true });
      await audio.play();
      markSoundUnlocked();
      syncSoundEnableUi();
      O.debugLog("Sound preview played; unlocked sound");
    } catch (error) {
      O.debugWarn("playSoundPreview failed", error);
      stopCurrentPreviewSound();
      handlePlayRejection(error);
      if ((!error || error.name !== "NotAllowedError") && global.WaslaToast) {
        global.WaslaToast.warning(O.getMessage("soundCouldNotPlay"));
      } else if ((!error || error.name !== "NotAllowedError") && O.notificationSettings && typeof O.notificationSettings.showModalWarning === "function") {
        O.notificationSettings.showModalWarning(O.getMessage("soundCouldNotPlay"));
      }
    }
  }

  async function maybeRequestBrowserNotificationPermission(state) {
    if (!state.showBrowserNotification) return;
    if (!("Notification" in global)) return;
    if (Notification.permission !== "default") return;
    try {
      await Notification.requestPermission();
    } catch (error) {
      O.debugWarn("Notification.requestPermission failed", error);
    }
  }

  function showBrowserNotificationIfAllowed() {
    try {
      if (!O.state.notificationSettings || !O.state.notificationSettings.showBrowserNotification) return;
      if (!("Notification" in global)) return;
      if (Notification.permission !== "granted") return;
      new Notification(O.getMessage("newOrderArrived"), { body: O.getMessage("checkOrdersPage") });
    } catch (error) {
      O.debugWarn("showBrowserNotificationIfAllowed failed", error);
    }
  }

  function initAudioUnlock() {
    wireSoundEnableControls();
    syncSoundEnableUi();
  }

  O.audio = {
    isSoundUnlocked: isSoundUnlocked,
    getAudioState: function () { return audioState; },
    markSoundUnlocked: markSoundUnlocked,
    stopCurrentPreviewSound: stopCurrentPreviewSound,
    stopCurrentSound: stopCurrentSound,
    stopTestPreview: stopTestPreview,
    getSoundUrlFromSettings: getSoundUrlFromSettings,
    playSoundNow: playSoundNow,
    playSoundPreview: playSoundPreview,
    maybeRequestBrowserNotificationPermission: maybeRequestBrowserNotificationPermission,
    showBrowserNotificationIfAllowed: showBrowserNotificationIfAllowed,
    initAudioUnlock: initAudioUnlock,
    bindUnlockGestures: bindUnlockGestures,
    showUnlockPromptOnce: showUnlockPromptOnce,
    syncSoundEnableUi: syncSoundEnableUi,
    enableNotificationSoundFromControl: enableNotificationSoundFromControl
  };
})(window);
