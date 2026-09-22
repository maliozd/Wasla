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

  function seedLiveScreenSummary() {
    const host = document.getElementById("ordersLiveScreenHost");
    if (!host) return;
    const meta = host.querySelector(".orders-live-screen-meta");
    if (!meta) return;

    [
      ["ordersLiveDisplayTodayCount", "data-total-count"],
      ["ordersLiveDisplayActiveCount", "data-active-count"],
      ["ordersLiveDisplayCancelledCount", "data-cancelled-count"]
    ].forEach(function (pair) {
      const el = document.getElementById(pair[0]);
      if (!el) return;
      const v = meta.getAttribute(pair[1]);
      if (v == null) return;
      const n = parseInt(String(v), 10);
      if (isNaN(n)) return;
      el.textContent = String(n);
    });
  }

  async function initLiveDisplayPage() {
    document.body.classList.add("wasla-orders-view-kitchen");
    initCloseButton();
    seedLiveScreenSummary();
    if (O.audio && typeof O.audio.initAudioUnlock === "function") {
      O.audio.initAudioUnlock();
    }
    // Baseline current IDs before polling — initial load must not notify or highlight.
    O.table.captureKnownOrderIdsFromContainer();
    O.table.applyNewOrderVisualState();
    if (O.notificationSettings && typeof O.notificationSettings.load === "function") {
      await O.notificationSettings.load();
    }
    if (O.audio && typeof O.audio.syncSoundEnableUi === "function") {
      O.audio.syncSoundEnableUi();
    }
    O.table.initPolling();
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (O.opts.pageMode !== "liveDisplay") return;
    initLiveDisplayPage().catch(function (error) {
      O.debugWarn("initLiveDisplayPage failed", error);
    });
  });
})(window);
