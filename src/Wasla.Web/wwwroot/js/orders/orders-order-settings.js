// Tenant order operational settings: auto approve, receipt creation timing, copy count.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) return;

  const TIMING_ON_ACCEPTED = "OnAccepted";
  const TIMING_MANUAL = "Manual";
  const SELECTABLE_TIMINGS = [TIMING_ON_ACCEPTED, TIMING_MANUAL];

  let cachedSettings = {
    autoApproveNewOrders: false,
    autoPrintReceiptOnAutoApprove: false,
    receiptCreationTiming: TIMING_ON_ACCEPTED,
    receiptPrintCopyCount: 1
  };

  function getToken() {
    const f = document.getElementById("orderSettingsForm");
    if (!f) return null;
    const el = f.querySelector("input[name=\"__RequestVerificationToken\"]");
    return el ? el.value : null;
  }

  function normalizeTiming(value) {
    const code = String(value || "").trim();
    if (code === TIMING_ON_ACCEPTED || code === "onAccepted") return TIMING_ON_ACCEPTED;
    if (code === TIMING_MANUAL || code === "manual") return TIMING_MANUAL;
    return TIMING_ON_ACCEPTED;
  }

  function timingToAutoPrint(timing) {
    return normalizeTiming(timing) === TIMING_ON_ACCEPTED;
  }

  function resolveTimingFromSettings(settings) {
    if (settings && settings.receiptCreationTiming) {
      return normalizeTiming(settings.receiptCreationTiming);
    }
    return settings && settings.autoPrintReceiptOnAutoApprove ? TIMING_ON_ACCEPTED : TIMING_MANUAL;
  }

  function getSelectedTimingRadio() {
    const checked = document.querySelector("input[name=\"receiptCreationTiming\"]:checked");
    return checked ? checked : null;
  }

  function setTimingRadio(timing) {
    const normalized = normalizeTiming(timing);
    const safeTiming = SELECTABLE_TIMINGS.indexOf(normalized) >= 0 ? normalized : TIMING_ON_ACCEPTED;
    document.querySelectorAll("input[name=\"receiptCreationTiming\"]").forEach(function (el) {
      if (!el.disabled) {
        el.checked = el.value === safeTiming;
      }
    });
  }

  function setUi(settings) {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const autoApproveBadge = document.getElementById("autoApproveStatusBadge");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    const autoApproveEnabled = !!settings.autoApproveNewOrders;
    const timing = resolveTimingFromSettings(settings);
    const autoPrintEnabled = timing === TIMING_ON_ACCEPTED;
    const copyCount = settings.receiptPrintCopyCount || 1;

    cachedSettings = {
      autoApproveNewOrders: autoApproveEnabled,
      autoPrintReceiptOnAutoApprove: autoPrintEnabled,
      receiptCreationTiming: timing,
      receiptPrintCopyCount: copyCount
    };

    if (autoApproveToggle) autoApproveToggle.checked = autoApproveEnabled;
    if (document.getElementById("receiptCreationTimingGroup")) {
      setTimingRadio(timing);
    }
    if (copyCountSelect) copyCountSelect.value = String(copyCount);

    if (autoApproveBadge) {
      autoApproveBadge.textContent = autoApproveEnabled
        ? O.getMessage("ordersActive")
        : O.getMessage("ordersPassive");
      autoApproveBadge.classList.remove("text-bg-secondary", "text-bg-success");
      autoApproveBadge.classList.add(autoApproveEnabled ? "text-bg-success" : "text-bg-secondary");
    }

    // The controls show the saved configuration. Whether it operates yet is the server's answer ("effective"):
    // PendingSetup while the restaurant is still in setup. Only a server response carries it.
    if (settings.effective) {
      setPendingNote("autoApprovePendingSetup", settings.effective.autoApprove === "PendingSetup");
      setPendingNote("receiptAutomationPendingSetup", settings.effective.autoReceipt === "PendingSetup");
    }
  }

  function setPendingNote(id, pending) {
    const note = document.getElementById(id);
    if (note) note.hidden = !pending;
  }

  function readUi() {
    const autoApproveToggle = document.getElementById("autoApproveToggle");
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");
    const timingRadio = getSelectedTimingRadio();

    const next = {
      autoApproveNewOrders: cachedSettings.autoApproveNewOrders,
      autoPrintReceiptOnAutoApprove: cachedSettings.autoPrintReceiptOnAutoApprove,
      receiptCreationTiming: cachedSettings.receiptCreationTiming,
      receiptPrintCopyCount: cachedSettings.receiptPrintCopyCount
    };

    if (autoApproveToggle) next.autoApproveNewOrders = !!autoApproveToggle.checked;
    if (timingRadio && !timingRadio.disabled) {
      next.receiptCreationTiming = normalizeTiming(timingRadio.value);
      next.autoPrintReceiptOnAutoApprove = timingToAutoPrint(next.receiptCreationTiming);
    }
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
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");
    document.querySelectorAll("input[name=\"receiptCreationTiming\"]").forEach(function (el) {
      if (el.value === "OnPreparing") return;
      el.disabled = !!disabled;
    });
    [autoApproveToggle, copyCountSelect].forEach(function (el) {
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
    const copyCountSelect = document.getElementById("receiptPrintCopyCountSelect");

    function onChange() {
      const previous = Object.assign({}, cachedSettings);
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
    document.querySelectorAll("input[name=\"receiptCreationTiming\"]").forEach(function (el) {
      if (el.disabled) return;
      el.addEventListener("change", onChange);
    });
    if (copyCountSelect) copyCountSelect.addEventListener("change", onChange);
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadCurrent();
  });
})(window);
