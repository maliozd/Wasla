// Tenant order operational settings: auto approve, receipt creation timing, copy count.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  const TIMING_ON_ACCEPTED = "onAccepted";
  const TIMING_MANUAL = "manual";

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

  function timingToAutoPrint(timing) {
    return timing === TIMING_ON_ACCEPTED;
  }

  function autoPrintToTiming(enabled) {
    return enabled ? TIMING_ON_ACCEPTED : TIMING_MANUAL;
  }

  function syncReceiptCreationHelp() {
    const timingSelect = document.getElementById("receiptCreationTimingSelect");
    const timingHelp = document.getElementById("receiptCreationTimingHelp");
    if (!timingSelect || !timingHelp) return;

    const timing = timingSelect.value;
    if (timing === TIMING_ON_ACCEPTED) {
      timingHelp.textContent = O.getMessage("receiptCreationOnAcceptedHelp");
      return;
    }
    if (timing === TIMING_MANUAL) {
      timingHelp.textContent = O.getMessage("receiptCreationManualHelp");
      return;
    }
    timingHelp.textContent = "";
  }

  function setUi(settings) {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const autoApproveBadge = document.getElementById("autoApproveStatusBadge");
    const timingSelect = document.getElementById("receiptCreationTimingSelect");
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
    if (timingSelect) timingSelect.value = autoPrintToTiming(autoPrintEnabled);
    if (copyCountSelect) copyCountSelect.value = String(copyCount);
    syncReceiptCreationHelp();

    if (autoApproveBadge) {
      autoApproveBadge.textContent = autoApproveEnabled
        ? O.getMessage("ordersActive")
        : O.getMessage("ordersPassive");
      autoApproveBadge.classList.remove("text-bg-secondary", "text-bg-success");
      autoApproveBadge.classList.add(autoApproveEnabled ? "text-bg-success" : "text-bg-secondary");
    }
  }

  function readUi() {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const timingSelect = document.getElementById("receiptCreationTimingSelect");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    const next = {
      autoApproveNewOrders: cachedSettings.autoApproveNewOrders,
      autoPrintReceiptOnAutoApprove: cachedSettings.autoPrintReceiptOnAutoApprove,
      receiptPrintCopyCount: cachedSettings.receiptPrintCopyCount
    };

    if (autoApproveToggle) next.autoApproveNewOrders = !!autoApproveToggle.checked;
    if (timingSelect) next.autoPrintReceiptOnAutoApprove = timingToAutoPrint(timingSelect.value);
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

  function setControlsDisabled(disabled) {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const timingSelect = document.getElementById("receiptCreationTimingSelect");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");
    [autoApproveToggle, timingSelect, copyCountSelect].forEach(function (el) {
      if (el) el.disabled = !!disabled;
    });
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
    O.showSettingsSaveSuccess();
  }

  function bind() {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const timingSelect = document.getElementById("receiptCreationTimingSelect");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    function onChange() {
      const previous = readUi();
      const next = readUi();
      setUi(next);
      setControlsDisabled(true);

      update(next).catch(function (e) {
        if (O.isDebugEnabled()) O.debugWarn("Order settings update failed", e);
        setUi(previous);
        O.showSettingsSaveError(e && e.message ? e.message : O.getMessage("orderSettingsUpdateFailed"));
      }).finally(function () {
        setControlsDisabled(false);
      });
    }

    if (autoApproveToggle) autoApproveToggle.addEventListener("change", onChange);
    if (timingSelect) {
      timingSelect.addEventListener("change", function () {
        syncReceiptCreationHelp();
        onChange();
      });
    }
    if (copyCountSelect) copyCountSelect.addEventListener("change", onChange);
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadCurrent();
  });
})(window);
