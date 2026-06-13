// Tenant order operational settings: auto approve, receipt print, copy count.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  let cachedSettings = {
    autoApproveNewOrders: false,
    autoPrintReceiptOnAutoApprove: false,
    receiptPrintCopyCount: 1
  };

  function getToken() {
    const f = document.getElementById("orderSettingsForm");
    if (!f) return null;
    const el = f.querySelector("input[name=\"__RequestVerificationToken\"]");
    return el ? el.value : null;
  }

  function setUi(settings) {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const autoApproveBadge = document.getElementById("autoApproveStatusBadge");
    const autoPrintToggle = document.getElementById("autoPrintReceiptToggle");
    const autoPrintBadge = document.getElementById("autoPrintReceiptStatusBadge");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    const autoApproveEnabled = !!settings.autoApproveNewOrders;
    const autoPrintEnabled = !!settings.autoPrintReceiptOnAutoApprove;
    const copyCount = settings.receiptPrintCopyCount || 1;

    cachedSettings = {
      autoApproveNewOrders: autoApproveEnabled,
      autoPrintReceiptOnAutoApprove: autoPrintEnabled,
      receiptPrintCopyCount: copyCount
    };

    if (autoApproveToggle) autoApproveToggle.checked = autoApproveEnabled;
    if (autoPrintToggle) autoPrintToggle.checked = autoPrintEnabled;
    if (copyCountSelect) copyCountSelect.value = String(copyCount);

    if (autoApproveBadge) {
      autoApproveBadge.textContent = autoApproveEnabled
        ? O.getMessage("ordersActive")
        : O.getMessage("ordersPassive");
      autoApproveBadge.classList.remove("text-bg-secondary", "text-bg-success");
      autoApproveBadge.classList.add(autoApproveEnabled ? "text-bg-success" : "text-bg-secondary");
    }

    if (autoPrintBadge) {
      autoPrintBadge.textContent = autoPrintEnabled
        ? O.getMessage("ordersActive")
        : O.getMessage("ordersPassive");
      autoPrintBadge.classList.remove("text-bg-secondary", "text-bg-success");
      autoPrintBadge.classList.add(autoPrintEnabled ? "text-bg-success" : "text-bg-secondary");
    }
  }

  function readUi() {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const autoPrintToggle = document.getElementById("autoPrintReceiptToggle");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    const next = {
      autoApproveNewOrders: cachedSettings.autoApproveNewOrders,
      autoPrintReceiptOnAutoApprove: cachedSettings.autoPrintReceiptOnAutoApprove,
      receiptPrintCopyCount: cachedSettings.receiptPrintCopyCount
    };

    if (autoApproveToggle) next.autoApproveNewOrders = !!autoApproveToggle.checked;
    if (autoPrintToggle) next.autoPrintReceiptOnAutoApprove = !!autoPrintToggle.checked;
    if (copyCountSelect) next.receiptPrintCopyCount = parseInt(copyCountSelect.value, 10) || 1;

    return next;
  }

  async function loadCurrent() {
    try {
      const url = O.opts.orderSettingsUrl || "/orders/order-settings";
      const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (!resp.ok) throw new Error("HTTP " + resp.status);
      const data = await resp.json();
      setUi(data);
    } catch (e) {
      if (O.isDebugEnabled()) O.debugWarn("Order settings loadCurrent failed", e);
      O.showOrdersWarning("order-settings-load-failed", O.getMessage("orderSettingsUpdateFailed"));
    }
  }

  async function update(settings) {
    const url = O.opts.orderSettingsUrl || "/orders/order-settings";
    const token = getToken();

    const headers = { "X-Requested-With": "fetch" };
    if (token) headers["RequestVerificationToken"] = token;

    const body = new URLSearchParams();
    body.set("autoApproveNewOrders", settings.autoApproveNewOrders ? "true" : "false");
    body.set("autoPrintReceiptOnAutoApprove", settings.autoPrintReceiptOnAutoApprove ? "true" : "false");
    body.set("receiptPrintCopyCount", String(settings.receiptPrintCopyCount));

    const resp = await fetch(url, { method: "POST", headers: headers, body: body });
    if (!resp.ok) {
      let message = O.getMessage("orderSettingsUpdateFailed");
      try {
        const err = await resp.json();
        if (err && err.message) message = err.message;
      } catch (_) { /* ignore */ }
      throw new Error(message);
    }

    const data = await resp.json();
    setUi(data);
    O.showMessage(O.getMessage("orderSettingsSaved"), "info");
  }

  function bind() {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const autoPrintToggle = document.getElementById("autoPrintReceiptToggle");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    function onChange() {
      const previous = readUi();
      const next = readUi();
      setUi(next);

      update(next).catch(function (e) {
        if (O.isDebugEnabled()) O.debugWarn("Order settings update failed", e);
        setUi(previous);
        O.showOrdersWarning(
          "order-settings-update-failed",
          e && e.message ? e.message : O.getMessage("orderSettingsUpdateFailed")
        );
      });
    }

    if (autoApproveToggle) autoApproveToggle.addEventListener("change", onChange);
    if (autoPrintToggle) autoPrintToggle.addEventListener("change", onChange);
    if (copyCountSelect) copyCountSelect.addEventListener("change", onChange);
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadCurrent();
  });
})(window);
