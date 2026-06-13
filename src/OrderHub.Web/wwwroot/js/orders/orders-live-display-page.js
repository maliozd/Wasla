// Live display page: polling and close/back.
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

  function initLiveDisplayPage() {
    initCloseButton();
    O.table.captureKnownOrderIdsFromContainer();
    O.table.initPolling();
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (O.opts.pageMode !== "liveDisplay") return;
    initLiveDisplayPage();
  });
})(window);
