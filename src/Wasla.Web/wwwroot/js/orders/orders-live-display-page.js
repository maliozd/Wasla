// Live display page: polling, close/back, and new-order sound ownership (Phase 2B1).
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.table) return;

  function closeLiveDisplay() {
    try {
      global.close();
    } catch (e) { /* ignore */ }

    setTimeout(function () {
      global.location.href = "/orders";
    }, 100);
  }

  function initCloseButton() {
    const btn = document.getElementById("ordersLiveDisplayClose");
    if (btn) btn.addEventListener("click", closeLiveDisplay);
  }

  // The settings menu and order training open the shared notification settings modal. Delegated, so a control
  // the page renders later (the training panel's step note) needs no binding of its own.
  function initNotificationSettingsTriggers() {
    document.addEventListener("click", function (event) {
      const trigger = event.target && event.target.closest
        ? event.target.closest("[data-notification-settings-open]")
        : null;
      if (!trigger || !O.notificationSettings || typeof O.notificationSettings.show !== "function") return;
      event.preventDefault();
      O.notificationSettings.show(trigger);
    });
  }

  async function initLiveDisplayPage() {
    document.body.classList.add("wasla-orders-view-kitchen");
    initCloseButton();
    initNotificationSettingsTriggers();
    if (O.audio && typeof O.audio.initAudioUnlock === "function") {
      O.audio.initAudioUnlock();
    }
    if (O.notificationSettings && typeof O.notificationSettings.load === "function") {
      await O.notificationSettings.load();
    }
    if (O.audio && typeof O.audio.syncSoundEnableUi === "function") {
      O.audio.syncSoundEnableUi();
    }
    // The first complete JSON snapshot is the notification baseline.
    if (O.liveStore && typeof O.liveStore.start === "function") {
      O.liveStore.start();
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (O.opts.pageMode !== "liveDisplay") return;
    initLiveDisplayPage().catch(function (error) {
      O.debugWarn("initLiveDisplayPage failed", error);
    });
  });
})(window);
