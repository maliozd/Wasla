// Open dedicated live display window from Orders page.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  function openLiveDisplay() {
    const url = new URL("/orders/live-display", global.location.origin).toString();
    const features = "popup=yes,width=1400,height=900,menubar=no,toolbar=no,location=no,status=no";
    const popup = global.open(url, "OrderHubLiveDisplay", features);

    if (!popup) {
      O.showOrdersWarning("orders-live-display-popup-blocked", O.getMessage("liveDisplayPopupBlocked"));
      global.location.href = url;
    }
  }

  function init() {
    const btn = document.getElementById("ordersOpenLiveDisplay");
    if (!btn) return;
    btn.addEventListener("click", openLiveDisplay);
  }

  document.addEventListener("DOMContentLoaded", init);
})(window);
