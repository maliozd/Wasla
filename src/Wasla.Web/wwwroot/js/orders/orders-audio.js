// Sound playback, preview, unlock, and browser notifications (depends on OrderHubOrders).
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) {
    return;
  }

  const SOUND_UNLOCK_KEY = "Wasla.soundUnlocked";

  let currentPreviewAudio = null;
  let currentAlertAudio = null;
  let sessionUnlocked = false;
  let unlockPromptShown = false;
  let unlockGesturesBound = false;

  function isSoundUnlocked() {
    if (sessionUnlocked) {
      return true;
    }
    try {
      return localStorage.getItem(SOUND_UNLOCK_KEY) === "true";
    } catch (error) {
      return false;
    }
  }

  function markSoundUnlocked() {
    sessionUnlocked = true;
    try {
      localStorage.setItem(SOUND_UNLOCK_KEY, "true");
    } catch (error) {
      /* ignore quota / private mode */
    }
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

  function stopCurrentSound() {
    stopCurrentPreviewSound();
    stopCurrentAlertSound();
  }

  function resolveSoundName(name) {
    if (O.soundControl && typeof O.soundControl.getSelectedSoundName === "function") {
      return O.soundControl.getSelectedSoundName();
    }
    if (name) {
      return String(name).toLowerCase();
    }
    const st = O.state.notificationSettings || {};
    return String(st.newOrderSoundName || "bell1").toLowerCase();
  }

  function getSoundUrlFromSettings(name) {
    const n = resolveSoundName(name);
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

  function syncSoundEnableUi() {
    const unlocked = isSoundUnlocked();
    const ordersEnableBtn = document.getElementById("ordersEnableNotificationSound");
    const ordersActive = document.getElementById("ordersNotificationSoundActive");
    const liveBanner = document.getElementById("ordersLiveDisplaySoundBanner");
    const liveActive = document.getElementById("ordersLiveDisplaySoundActive");

    setElementVisible(ordersEnableBtn, !unlocked);
    setElementVisible(ordersActive, unlocked);
    setElementVisible(liveBanner, !unlocked);
    setElementVisible(liveActive, unlocked);

    if (unlocked && liveActive && !liveActive.dataset.fadeScheduled) {
      liveActive.dataset.fadeScheduled = "1";
      global.setTimeout(function () {
        setElementVisible(liveActive, false);
      }, 4000);
    }
  }

  function showUnlockPromptOnce() {
    if (unlockPromptShown) {
      syncSoundEnableUi();
      return;
    }
    unlockPromptShown = true;
    syncSoundEnableUi();
    const message = O.getMessage("enableNotificationSound") || O.getMessage("soundUnlockHint");
    if (global.OrderHubToast && typeof global.OrderHubToast.info === "function") {
      global.OrderHubToast.info(message, { key: "sound-unlock-hint", durationMs: 8000 });
      return;
    }
    O.showMessage(message, "info");
  }

  async function trySilentUnlock(url) {
    try {
      const audio = new Audio(url || getSoundUrlFromSettings("bell1"));
      audio.volume = 0.01;
      await audio.play();
      stopAudioElement(audio);
      markSoundUnlocked();
      syncSoundEnableUi();
      return true;
    } catch (error) {
      O.debugWarn("silent audio unlock failed", error);
      return false;
    }
  }

  async function enableNotificationSoundFromControl() {
    const st = O.state.notificationSettings || {};
    const soundName = resolveSoundName(st.newOrderSoundName);
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
      syncSoundEnableUi();
      return false;
    }
  }

  function wireSoundEnableControls() {
    const buttons = [
      document.getElementById("ordersEnableNotificationSound"),
      document.getElementById("ordersLiveDisplayEnableNotificationSound")
    ];

    buttons.forEach(function (btn) {
      if (!btn || btn.dataset.soundEnableWired === "1") {
        return;
      }
      btn.dataset.soundEnableWired = "1";
      btn.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        enableNotificationSoundFromControl().catch(function (error) {
          O.debugWarn("enableNotificationSoundFromControl failed", error);
        });
      });
    });
  }

  function bindUnlockGestures() {
    if (unlockGesturesBound || typeof document === "undefined") {
      return;
    }
    unlockGesturesBound = true;

    const onUserGesture = function (event) {
      if (event && event.target && event.target.closest && event.target.closest(".oh-orders-sound-enable-btn")) {
        return;
      }
      trySilentUnlock().then(function (ok) {
        if (ok) {
          unlockPromptShown = true;
          syncSoundEnableUi();
          if (O.notificationSettings && typeof O.notificationSettings.updateNotificationStatusUi === "function") {
            try {
              O.notificationSettings.updateNotificationStatusUi(
                O.state.notificationSettings || {}
              );
            } catch (error) {
              /* ignore */
            }
          }
        }
      });
    };

    ["pointerdown", "keydown", "touchstart"].forEach(function (evt) {
      document.addEventListener(evt, onUserGesture, { once: true, capture: true, passive: true });
    });
  }

  function handlePlayRejection(error) {
    O.debugWarn("audio.play() rejected", error);
    if (error && error.name === "NotAllowedError") {
      showUnlockPromptOnce();
      bindUnlockGestures();
      O.showOrdersWarning("audio-not-allowed", O.getMessage("audioNotAllowed"));
    } else {
      O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
    }
  }

  async function playSoundNow(state, urlOverride) {
    stopCurrentSound();

    const repeat = Math.max(1, Math.min(3, state.newOrderSoundRepeatCount || 1));
    const pct = state.newOrderSoundVolumePercent;
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const soundName = resolveSoundName(state && state.newOrderSoundName);
    const url = urlOverride || getSoundUrlFromSettings(soundName);

    O.debugLog("Playing new order sound", {
      enabled: !!state.newOrderSoundEnabled,
      soundUnlocked: isSoundUnlocked(),
      soundName: soundName,
      url: url,
      repeat: repeat,
      volume: vol
    });

    for (let i = 0; i < repeat; i++) {
      try {
        const audio = new Audio(url);
        audio.volume = vol;
        currentAlertAudio = audio;
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
          const playResult = audio.play();
          if (playResult && typeof playResult.then === "function") {
            playResult.then(function () {
              markSoundUnlocked();
              syncSoundEnableUi();
            }).catch(function (error) {
              handlePlayRejection(error);
              done();
            });
          }
        });

        if (currentAlertAudio === audio) {
          currentAlertAudio = null;
        }
      } catch (error) {
        O.debugWarn("playSoundNow failed", error);
        O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
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
      if ((!error || error.name !== "NotAllowedError") && O.notificationSettings && typeof O.notificationSettings.showModalWarning === "function") {
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
    bindUnlockGestures();
  }

  O.audio = {
    isSoundUnlocked: isSoundUnlocked,
    markSoundUnlocked: markSoundUnlocked,
    stopCurrentPreviewSound: stopCurrentPreviewSound,
    stopCurrentSound: stopCurrentSound,
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
