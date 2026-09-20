// Read-only automation status summary on the Orders page header.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  function setBadge(el, active, activeLabel, disabledLabel) {
    if (!el) return;
    el.hidden = false;
    el.removeAttribute("hidden");
    el.textContent = active ? activeLabel : disabledLabel;
    el.classList.remove("oh-orders-status-chip--active", "oh-orders-status-chip--muted");
    el.classList.add(active ? "oh-orders-status-chip--active" : "oh-orders-status-chip--muted");
  }

  function hideStatusGroup() {
    const group = document.getElementById("ordersAutomationStatusGroup");
    if (group) {
      group.hidden = true;
      group.setAttribute("hidden", "hidden");
      group.classList.add("d-none");
    }
  }

  async function loadStatus() {
    const group = document.getElementById("ordersAutomationStatusGroup");
    const syncEl = document.getElementById("automationStatusSync");
    const approveEl = document.getElementById("automationStatusAutoApprove");
    const receiptEl = document.getElementById("automationStatusReceipt");
    if (!group || (!syncEl && !approveEl && !receiptEl)) return;

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
      hideStatusGroup();
      if (O.isDebugEnabled()) O.debugWarn("Automation status load failed", e);
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    loadStatus();
  });
})(window);
