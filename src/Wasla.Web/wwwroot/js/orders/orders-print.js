// Manual receipt printing. Posts to the existing Orders print endpoint, which queues a job on the
// Print Bridge pipeline. Success means "queued", never "printed on paper".
// Phase 2B5 temporary: the clicked button goes to queued immediately; the open UI does not
// receive later PrintJob transitions (Pending/Printing/Printed/Failed). Reopen the lazy Live
// modal for a fresh snapshot; the full details page requires reload. SignalR follow-up
// acceptance criteria: docs/orders-printjob-status-signalr.md.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  const inFlight = {};

  function localize(key) {
    return (typeof O.getMessage === "function" ? O.getMessage(key) : null) || key;
  }

  function getCsrfToken() {
    const input = document.querySelector("input[name=\"__RequestVerificationToken\"]");
    return input ? input.value : null;
  }

  function buildUrl(orderId) {
    const template = O.opts.orderPrintUrl || "/orders/{id}/print";
    return template.replace("{id}", encodeURIComponent(orderId));
  }

  function toast(message, type) {
    if (global.OrderHubToast) {
      const fn = global.OrderHubToast[type];
      if (typeof fn === "function") {
        fn(message, { key: "order-print" });
        return;
      }
    }
    if (typeof O.showMessage === "function") O.showMessage(message, type);
  }

  function setLabel(btn, text) {
    const span = btn.querySelector("[data-print-label]");
    if (span) span.textContent = text;
    else btn.textContent = text;
  }

  /** Server state decides the button; the client never guesses that a receipt was physically printed. */
  function applyQueuedState(btn) {
    btn.setAttribute("data-print-mode", "queued");
    btn.disabled = true;
    setLabel(btn, btn.getAttribute("data-label-queued") || localize("orderPrintInQueue"));
  }

  function restore(btn, mode) {
    btn.setAttribute("data-print-mode", mode);
    btn.disabled = false;
    setLabel(
      btn,
      mode === "reprint"
        ? (btn.getAttribute("data-label-reprint") || localize("orderPrintReprint"))
        : (btn.getAttribute("data-label-print") || localize("orderPrintAction")));
  }

  async function submit(btn, orderId) {
    const previousMode = btn.getAttribute("data-print-mode") || "print";

    if (previousMode === "reprint") {
      const question = btn.getAttribute("data-confirm-reprint") || localize("orderPrintConfirmReprint");
      if (!global.confirm(question)) return;
    }

    inFlight[orderId] = true;
    btn.disabled = true;

    try {
      const token = getCsrfToken();
      const headers = { "X-Requested-With": "XMLHttpRequest" };
      if (token) headers["RequestVerificationToken"] = token;

      const resp = await fetch(buildUrl(orderId), { method: "POST", headers: headers });

      let payload = null;
      try {
        payload = await resp.json();
      } catch (_) { /* non-JSON error page */ }

      const message = (payload && payload.message) ? String(payload.message) : null;

      if (resp.ok) {
        toast(message || localize("orderPrintQueued"), "success");
        applyQueuedState(btn);
        return;
      }

      // 409: a receipt is already Pending/Printing for this order; no second job was created.
      if (resp.status === 409) {
        toast(message || localize("orderPrintAlreadyQueued"), "warning");
        applyQueuedState(btn);
        return;
      }

      toast(message || localize("orderPrintFailed"), "error");
      restore(btn, previousMode);
    } catch (error) {
      toast(localize("orderPrintFailed"), "error");
      restore(btn, previousMode);
      if (O.isDebugEnabled()) O.debugWarn("manual order print failed", error);
    } finally {
      delete inFlight[orderId];
    }
  }

  function init() {
    // Registered once on document: detail markup is re-fetched on every modal open and the Live
    // Screen host re-renders on every poll, so per-button listeners would accumulate.
    document.addEventListener("click", function (ev) {
      const target = ev.target;
      const btn = target && target.closest ? target.closest("[data-order-print]") : null;
      if (!btn) return;

      const orderId = btn.getAttribute("data-order-print");
      if (!orderId) return;

      ev.preventDefault();

      if (btn.disabled || inFlight[orderId]) return;
      submit(btn, orderId);
    });
  }

  O.print = {
    submit: submit
  };

  document.addEventListener("DOMContentLoaded", init);
})(window);
