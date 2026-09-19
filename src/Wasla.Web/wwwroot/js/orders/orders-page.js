// Orders page bootstrap: filters, debug badge, notification button, init.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.audio || !O.notificationSettings || !O.table || typeof O.table.initPolling !== "function") {
    return;
  }

  function initFilters() {
    const platformSelect = document.getElementById("platformSelect");
    if (platformSelect) {
      platformSelect.addEventListener("change", function () {
        const url = new URL(global.location.href);
        if (platformSelect.value) url.searchParams.set("platform", platformSelect.value);
        else url.searchParams.delete("platform");
        url.searchParams.set("page", "1");
        global.location.href = url.toString();
      });
    }

    const statusSelect = document.getElementById("statusSelect");
    if (statusSelect) {
      statusSelect.addEventListener("change", function () {
        const url = new URL(global.location.href);
        if (statusSelect.value) url.searchParams.set("status", statusSelect.value);
        else url.searchParams.delete("status");
        url.searchParams.set("page", "1");
        global.location.href = url.toString();
      });
    }
  }

  function initDebugBadge() {
    if (!O.isDebugEnabled()) return;
    const badge = document.createElement("div");
    badge.textContent = O.getMessage("debugBadgeEnabled");
    badge.style.position = "fixed";
    badge.style.bottom = "12px";
    badge.style.right = "12px";
    badge.style.zIndex = "1080";
    badge.style.padding = "6px 10px";
    badge.style.borderRadius = "999px";
    badge.style.fontSize = "12px";
    badge.style.background = "rgba(13, 110, 253, 0.95)";
    badge.style.color = "#fff";
    badge.style.boxShadow = "0 2px 10px rgba(0,0,0,.15)";
    document.body.appendChild(badge);
    O.debugLog("Orders debug mode enabled");
  }

  async function initOrdersPage() {
    initFilters();
    O.table.captureKnownOrderIdsFromContainer();
    if (O.audio && typeof O.audio.initAudioUnlock === "function") {
      O.audio.initAudioUnlock();
    }
    await O.notificationSettings.load();
    O.table.initPolling();
    initDebugBadge();
  }

  document.addEventListener("DOMContentLoaded", function () {
    initOrdersPage().catch(function (error) {
      O.showOrdersWarning("orders-init-failed", O.getMessage("pageInitFailed"));
      O.debugWarn("initOrdersPage failed", error);
    });
  });
})(window);
