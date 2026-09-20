// Live display page: polling and close/back for dedicated operational cards.
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

  function initLiveDisplayPage() {
    document.body.classList.add("oh-orders-view-kitchen");
    initCloseButton();
    seedLiveScreenSummary();
    O.table.captureKnownOrderIdsFromContainer();
    O.table.applyNewOrderVisualState();
    O.table.initPolling();
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (O.opts.pageMode !== "liveDisplay") return;
    initLiveDisplayPage();
  });
})(window);
