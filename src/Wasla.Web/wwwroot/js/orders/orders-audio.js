// Sound playback, preview, and browser notifications (depends on WaslaOrders).
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) {
    return;
  }

  let currentPreviewAudio = null;

  function isSoundUnlocked() {
    return localStorage.getItem("Wasla.soundUnlocked") === "true";
  }

  function stopCurrentPreviewSound() {
    try {
      if (!currentPreviewAudio) return;
      currentPreviewAudio.pause();
      currentPreviewAudio.currentTime = 0;
    } catch (error) {
      O.debugWarn("stopCurrentPreviewSound failed", error);
    }
    currentPreviewAudio = null;
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

  async function playSoundNow(state, urlOverride) {
    const repeat = Math.max(1, Math.min(3, state.newOrderSoundRepeatCount || 1));
    const pct = state.newOrderSoundVolumePercent;
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const url = urlOverride || getSoundUrlFromSettings(state.newOrderSoundName);

    O.debugLog("Playing new order sound", {
      enabled: !!state.newOrderSoundEnabled,
      soundUnlocked: isSoundUnlocked(),
      soundName: state.newOrderSoundName,
      url: url,
      repeat: repeat,
      volume: vol
    });

    for (let i = 0; i < repeat; i++) {
      try {
        const audio = new Audio(url);
        audio.volume = vol;
        audio.addEventListener("error", function () {
          O.debugWarn("Audio load/playback error", {
            url: audio.src,
            code: audio.error && audio.error.code,
            message: audio.error && audio.error.message
          });
          O.showOrdersWarning("audio-error", O.getMessage("audioFileError"));
        }, { once: true });

        await new Promise(function (resolve) {
          audio.addEventListener("ended", resolve, { once: true });
          audio.addEventListener("error", resolve, { once: true });
          audio.play().catch(function (error) {
            O.debugWarn("audio.play() rejected", error);
            if (error && error.name === "NotAllowedError") {
              O.showOrdersWarning("audio-not-allowed", O.getMessage("audioNotAllowed"));
            } else {
              O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
            }
            resolve();
          });
        });
      } catch (error) {
        O.debugWarn("playSoundNow failed", error);
        O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
      }
    }
  }

  async function playSoundPreview(soundName, soundUrl) {
    stopCurrentPreviewSound();
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
      localStorage.setItem("Wasla.soundUnlocked", "true");
      O.debugLog("Sound preview played; unlocked sound");
    } catch (error) {
      O.debugWarn("playSoundPreview failed", error);
      stopCurrentPreviewSound();
      if (global.WaslaToast) {
        if (error && error.name === "NotAllowedError") {
          global.WaslaToast.error(O.getMessage("audioNotAllowed"));
        } else {
          global.WaslaToast.warning(O.getMessage("soundCouldNotPlay"));
        }
      } else {
        if (O.notificationSettings && typeof O.notificationSettings.showModalWarning === "function") {
          O.notificationSettings.showModalWarning(O.getMessage("soundCouldNotPlay"));
        }
        if (error && error.name === "NotAllowedError") {
          O.showOrdersWarning("audio-not-allowed", O.getMessage("audioNotAllowed"));
        } else {
          O.showOrdersWarning("audio-play-failed", O.getMessage("audioPlayFailed"));
        }
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

  O.audio = {
    isSoundUnlocked: isSoundUnlocked,
    stopCurrentPreviewSound: stopCurrentPreviewSound,
    getSoundUrlFromSettings: getSoundUrlFromSettings,
    playSoundNow: playSoundNow,
    playSoundPreview: playSoundPreview,
    maybeRequestBrowserNotificationPermission: maybeRequestBrowserNotificationPermission,
    showBrowserNotificationIfAllowed: showBrowserNotificationIfAllowed
  };
})(window);
