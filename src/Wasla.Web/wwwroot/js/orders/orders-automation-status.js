// Read-only automation status summary on the Orders page header.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) return;

  function setBadge(el, active, activeLabel, disabledLabel) {
    if (!el) return;
    el.textContent = active ? activeLabel : disabledLabel;
    el.classList.remove("wasla-orders-status-chip--active", "wasla-orders-status-chip--muted");
    el.classList.add(active ? "wasla-orders-status-chip--active" : "wasla-orders-status-chip--muted");
  }

  async function loadStatus() {
    const syncEl = document.getElementById("automationStatusSync");
    const approveEl = document.getElementById("automationStatusAutoApprove");
    const receiptEl = document.getElementById("automationStatusReceipt");
    if (!syncEl && !approveEl && !receiptEl) return;

    const syncActiveLabel = O.getMessage("automationStatusSyncActive");
    const syncDisabledLabel = O.getMessage("automationStatusSyncDisabled");
    const approveActiveLabel = O.getMessage("automationStatusAutoApproveActive");
    const approveDisabledLabel = O.getMessage("automationStatusAutoApproveDisabled");
    const receiptActiveLabel = O.getMessage("automationStatusReceiptPrintingActive");
    const receiptDisabledLabel = O.getMessage("automationStatusReceiptPrintingDisabled");

    try {
      const syncUrl = O.opts.orderSyncSettingsUrl || "/orders/sync-settings";
      const settingsUrl = O.opts.orderSettingsUrl || "/orders/order-settings";
      const headers = { "X-Requested-With": "XMLHttpRequest" };

      const [syncResp, settingsResp] = await Promise.all([
        fetch(syncUrl, { headers: headers }),
        fetch(settingsUrl, { headers: headers })
      ]);

      if (!syncResp.ok || !settingsResp.ok) {
        throw new Error("HTTP " + syncResp.status + " / " + settingsResp.status);
      }

      const syncData = await syncResp.json();
      const settingsData = await settingsResp.json();

      setBadge(syncEl, !!syncData.orderSyncEnabled, syncActiveLabel, syncDisabledLabel);
      setBadge(approveEl, !!settingsData.autoApproveNewOrders, approveActiveLabel, approveDisabledLabel);
      setBadge(
        receiptEl,
        !!settingsData.autoPrintReceiptOnAutoApprove,
        receiptActiveLabel,
        receiptDisabledLabel
      );
    } catch (e) {
      if (O.isDebugEnabled()) O.debugWarn("Automation status load failed", e);
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    loadStatus();
  });
})(window);
