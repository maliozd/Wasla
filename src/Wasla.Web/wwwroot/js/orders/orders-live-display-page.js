// Live display page: polling, notification sound, and close/back.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
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

  async function initLiveDisplayPage() {
    initCloseButton();
    O.table.captureKnownOrderIdsFromContainer();
    if (O.audio && typeof O.audio.initAudioUnlock === "function") {
      O.audio.initAudioUnlock();
    }
    if (O.notificationSettings && typeof O.notificationSettings.load === "function") {
      try {
        await O.notificationSettings.load();
      } catch (error) {
        O.debugWarn("live display notification settings load failed", error);
      }
    }
    O.table.initPolling();
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (O.opts.pageMode !== "liveDisplay") return;
    initLiveDisplayPage().catch(function (error) {
      O.debugWarn("initLiveDisplayPage failed", error);
      O.table.initPolling();
    });
  });
})(window);
