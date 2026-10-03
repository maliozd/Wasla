// Live Screen automation status (settings menu): order synchronization, automatic approval and automatic receipts.
// Each Live Screen snapshot carries their effective state from the server (Active, Off, or PendingSetup while the
// tenant is still in setup); this script only shows it. Nothing is inferred from the page, and no request is sent.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && root.WaslaOrders && !root.WaslaAutomationStatus) {
    root.WaslaAutomationStatus = api;
    api.boot(root.document, root.WaslaOrders);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  var ACTIVE_CLASS = "wasla-orders-status-chip--active";
  var OFF_CLASS = "wasla-orders-status-chip--muted";
  var PENDING_CLASS = "wasla-orders-status-chip--pending";
  var HINT_ID = "automationPendingSetupHint";

  var INDICATORS = [
    { id: "automationStatusSync", key: "orderSync", active: "automationStatusSyncActive", off: "automationStatusSyncDisabled" },
    { id: "automationStatusAutoApprove", key: "autoApprove", active: "automationStatusAutoApproveActive", off: "automationStatusAutoApproveDisabled" },
    { id: "automationStatusReceipt", key: "autoReceipt", active: "automationStatusReceiptPrintingActive", off: "automationStatusReceiptPrintingDisabled" }
  ];

  /**
   * Text and style for one effective state; null for anything unknown (the indicator then keeps what it shows).
   * Each state has its own words, so the meaning never depends on color.
   */
  function stateView(state, labels) {
    if (state === "Active") return { text: labels.active, className: ACTIVE_CLASS, pending: false };
    if (state === "Off") return { text: labels.off, className: OFF_CLASS, pending: false };
    if (state === "PendingSetup") return { text: labels.pending, className: PENDING_CLASS, pending: true };
    return null;
  }

  /**
   * Updates the indicators from a snapshot's automation section. Absent only when the server could not read the
   * status: then nothing changes and the last shown state stays. A pending indicator is described by the visible
   * explanation line.
   */
  function apply(doc, automation, message) {
    if (!automation || typeof automation !== "object") return false;
    var hint = doc.getElementById(HINT_ID);
    var anyPending = false;

    for (var i = 0; i < INDICATORS.length; i++) {
      var indicator = INDICATORS[i];
      var el = doc.getElementById(indicator.id);
      var view = stateView(automation[indicator.key], {
        active: message(indicator.active),
        off: message(indicator.off),
        pending: message("automationStatusPendingSetup")
      });
      if (!el || !view) continue;

      if (el.textContent !== view.text) el.textContent = view.text;
      el.classList.remove(ACTIVE_CLASS, OFF_CLASS, PENDING_CLASS);
      el.classList.add(view.className);
      if (view.pending && hint) el.setAttribute("aria-describedby", HINT_ID);
      else el.removeAttribute("aria-describedby");
      anyPending = anyPending || view.pending;
    }

    if (hint) hint.hidden = !anyPending;
    return true;
  }

  function boot(doc, O) {
    doc.addEventListener("wasla:live-rendered", function (event) {
      var detail = event && event.detail ? event.detail : null;
      if (!detail) return;
      apply(doc, detail.automation, function (key) { return O.getMessage(key); });
    });
  }

  return {
    stateView: stateView,
    apply: apply,
    boot: boot
  };
});
