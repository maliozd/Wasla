// Orders table actions: approve/reject and post-approval lifecycle.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.table || typeof O.table.refreshOrdersTable !== "function") return;

  /** @type {Record<string, { confirmTitle: string, confirmMessage: string, fallbackSuccess: string, toastKey: string }>} */
  var lifecycleByAction = {
    "start-preparing": {
      confirmTitle: "ordersStartPreparingConfirmTitle",
      confirmMessage: "ordersStartPreparingConfirmMessage",
      fallbackSuccess: "ordersStartPreparingSuccess",
      toastKey: "order-start-preparing-success"
    },
    "mark-ready": {
      confirmTitle: "ordersMarkReadyConfirmTitle",
      confirmMessage: "ordersMarkReadyConfirmMessage",
      fallbackSuccess: "ordersMarkReadySuccess",
      toastKey: "order-mark-ready-success"
    }
    // No "hand-to-courier" or "mark-delivered": the platform courier reports OnTheWay and
    // Delivered through provider sync.
  };

  function getCsrfToken() {
    const tokenInput =
      document.querySelector("#notificationSettingsForm input[name=\"__RequestVerificationToken\"]") ||
      document.querySelector("input[name=\"__RequestVerificationToken\"]");
    return tokenInput ? tokenInput.value : null;
  }

  function localize(key) {
    return (O && typeof O.getMessage === "function" ? O.getMessage(key) : null) || key;
  }

  async function postAction(url, demo, reasonId) {
    const token = getCsrfToken();
    const headers = { "X-Requested-With": "XMLHttpRequest" };
    let body = undefined;
    if (token) headers["RequestVerificationToken"] = token;
    // Real order actions keep their header-only request; only the guided demo also posts the token as a form field,
    // and a reject with a reason posts the operator's reason.
    const fields = [];
    if (token && demo) fields.push("__RequestVerificationToken=" + encodeURIComponent(token));
    if (reasonId) fields.push("reasonId=" + encodeURIComponent(reasonId));
    if (fields.length) {
      headers["Content-Type"] = "application/x-www-form-urlencoded";
      body = fields.join("&");
    }

    const resp = await fetch(url, { method: "POST", headers: headers, body: body });
    let payload = null;
    try {
      payload = await resp.json();
    } catch (_) { /* ignore */ }
    return { resp: resp, payload: payload };
  }

  function toastSuccess(key, fallbackMessageKey, toastKey) {
    const message = localize(key || fallbackMessageKey);
    if (global.WaslaToast && typeof global.WaslaToast.success === "function") {
      global.WaslaToast.success(message, { key: toastKey });
    }
  }

  function toastError(messageKey, toastKey) {
    const message = localize(messageKey);
    if (global.WaslaToast && typeof global.WaslaToast.error === "function") {
      global.WaslaToast.error(message, { key: toastKey });
    }
  }

  // Reject with a reason (Trendyol GO): the one dialog of the page shows the clicked order's platform group,
  // preselects nothing, and resolves with the chosen reason id, or null when the operator cancels. Platforms without a
  // group keep the plain confirmation. The server checks the reason again against the stored order's platform.
  let rejectDialogOpen = false;

  function rejectReasonGroup(platform) {
    const dialog = document.querySelector("[data-order-reject-dialog]");
    if (!dialog || !platform || !global.bootstrap || !global.bootstrap.Modal) return null;
    const groups = dialog.querySelectorAll("[data-order-reject-reasons]");
    for (let i = 0; i < groups.length; i++) {
      if (groups[i].getAttribute("data-order-reject-reasons") === platform) return { dialog: dialog, group: groups[i] };
    }
    return null;
  }

  // Bootstrap ignores hide() (and show()) while a modal is still fading in or out, so the reject flow follows each
  // modal's own events instead of assuming a hide() takes effect. Listening on document, registered before any waiter
  // below, records the state before a waiter reacts to the same event, and sees show/hide only once they were allowed.
  const modalStates = new WeakMap();
  ["show", "shown", "hide", "hidden"].forEach(function (name) {
    document.addEventListener(name + ".bs.modal", function (ev) {
      if (!ev.defaultPrevented) modalStates.set(ev.target, name);
    });
  });

  function onceModalEvent(modalEl, name, callback) {
    function listener(ev) {
      if (ev.target !== modalEl) return;
      document.removeEventListener(name + ".bs.modal", listener);
      callback();
    }
    document.addEventListener(name + ".bs.modal", listener);
  }

  // Resolves true once the modal is hidden: one still opening finishes opening and is then hidden, one already closing
  // finishes closing. Resolves false when something kept it open.
  function whenHidden(modalEl) {
    return new Promise(function (resolve) {
      function hide() {
        global.bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        const state = modalStates.get(modalEl);
        if (state === "hide") onceModalEvent(modalEl, "hidden", function () { resolve(true); });
        else resolve(state === "hidden");
      }
      const state = modalEl ? modalStates.get(modalEl) : null;
      if (state === "show") onceModalEvent(modalEl, "shown", hide);
      else if (state === "shown") hide();
      else if (state === "hide") onceModalEvent(modalEl, "hidden", function () { resolve(true); });
      else resolve(true);
    });
  }

  // Where focus goes after the dialog: the Reject button when it is still on the page, otherwise the order's details
  // button on the Live Screen (the detail modal was closed for the dialog, or the card was re-rendered).
  function focusAfterReject(btn, orderId) {
    let target = btn && btn.isConnected && !btn.closest(".modal") ? btn : null;
    if (!target) {
      const host = document.getElementById("ordersLiveScreenHost");
      const details = host ? host.querySelectorAll("[data-order-detail]") : [];
      for (let i = 0; i < details.length && !target; i++) {
        if (details[i].getAttribute("data-order-detail") === String(orderId)) target = details[i];
      }
      if (!target) target = host;
    }
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  async function chooseRejectReason(found, btn, orderId) {
    // Bootstrap does not stack modals: a Reject inside the order detail modal closes it first, after it has finished
    // opening when it is still opening.
    if (!await whenHidden(btn.closest(".modal"))) return null;

    const dialog = found.dialog;
    const group = found.group;
    const form = dialog.querySelector("[data-order-reject-form]");
    const error = dialog.querySelector("[data-order-reject-error]");
    const groups = dialog.querySelectorAll("[data-order-reject-reasons]");
    for (let i = 0; i < groups.length; i++) {
      const active = groups[i] === group;
      groups[i].hidden = !active;
      groups[i].disabled = !active;
    }
    const inputs = group.querySelectorAll("input[name=\"reasonId\"]");
    for (let i = 0; i < inputs.length; i++) {
      inputs[i].checked = false;
      inputs[i].removeAttribute("aria-invalid");
    }
    if (error) error.hidden = true;

    return new Promise(function (resolve) {
      const modal = global.bootstrap.Modal.getOrCreateInstance(dialog);
      let chosen = null;
      // Set by the first of Reject, Cancel or Escape: later clicks while the dialog fades in or out change nothing.
      let closing = false;

      function close(reasonId) {
        if (closing) return;
        closing = true;
        chosen = reasonId;
        // While the dialog is still opening, it closes as soon as it has opened.
        whenHidden(dialog);
      }
      function setInvalid(invalid) {
        if (error) error.hidden = !invalid;
        for (let i = 0; i < inputs.length; i++) {
          if (invalid) inputs[i].setAttribute("aria-invalid", "true");
          else inputs[i].removeAttribute("aria-invalid");
        }
      }
      function onSubmit(ev) {
        ev.preventDefault();
        if (closing) return;
        const checked = group.querySelector("input[name=\"reasonId\"]:checked");
        if (!checked) {
          setInvalid(true);
          if (inputs.length) inputs[0].focus();
          return;
        }
        close(checked.value);
      }
      // Bootstrap's own Cancel handling ignores a click while the dialog is still opening.
      function onClick(ev) {
        if (ev.target.closest && ev.target.closest("[data-bs-dismiss=\"modal\"]")) close(null);
      }
      // Escape while the dialog is still opening never reaches Bootstrap (its focus trap and key handling start once
      // the dialog has opened), so it cancels here, like Cancel. Once open, Bootstrap's own Escape runs first.
      function onKeydown(ev) {
        if (ev.key !== "Escape" || closing) return;
        ev.preventDefault();
        close(null);
      }
      function onHide() { closing = true; }
      function onChange() { setInvalid(false); }
      function onShown() { if (!closing && inputs.length) inputs[0].focus(); }
      function onHidden() {
        form.removeEventListener("submit", onSubmit);
        dialog.removeEventListener("click", onClick);
        document.removeEventListener("keydown", onKeydown);
        dialog.removeEventListener("hide.bs.modal", onHide);
        group.removeEventListener("change", onChange);
        dialog.removeEventListener("shown.bs.modal", onShown);
        focusAfterReject(btn, orderId);
        resolve(chosen);
      }
      form.addEventListener("submit", onSubmit);
      dialog.addEventListener("click", onClick);
      document.addEventListener("keydown", onKeydown);
      dialog.addEventListener("hide.bs.modal", onHide);
      group.addEventListener("change", onChange);
      dialog.addEventListener("shown.bs.modal", onShown);
      dialog.addEventListener("hidden.bs.modal", onHidden, { once: true });
      modal.show();
    });
  }

  async function handleActionClick(btn) {
    const orderId = btn.getAttribute("data-order-id");
    const action = btn.getAttribute("data-order-action");
    if (!orderId || !action) return;

    var confirmTitleKey;
    var confirmMessageKey;
    var fallbackSuccess;
    var toastKey;

    if (action === "approve") {
      confirmTitleKey = "ordersApproveConfirmTitle";
      confirmMessageKey = "ordersApproveConfirmMessage";
      fallbackSuccess = "ordersApproveSuccess";
      toastKey = "order-approve-success";
    } else if (action === "reject") {
      confirmTitleKey = "ordersRejectConfirmTitle";
      confirmMessageKey = "ordersRejectConfirmMessage";
      fallbackSuccess = "ordersRejectSuccess";
      toastKey = "order-reject-success";
    } else {
      var lc = lifecycleByAction[action];
      if (!lc) return;
      confirmTitleKey = lc.confirmTitle;
      confirmMessageKey = lc.confirmMessage;
      fallbackSuccess = lc.fallbackSuccess;
      toastKey = lc.toastKey;
    }

    const demo = btn.hasAttribute("data-wasla-demo");
    let reasonId = null;
    const reasons = action === "reject" && !demo ? rejectReasonGroup(btn.getAttribute("data-order-platform")) : null;
    if (reasons) {
      if (rejectDialogOpen) return;
      rejectDialogOpen = true;
      try {
        reasonId = await chooseRejectReason(reasons, btn, orderId);
      } finally {
        rejectDialogOpen = false;
      }
      if (!reasonId) return;
    } else if (!demo) {
      const ok = global.confirm(localize(confirmTitleKey) + "\n\n" + localize(confirmMessageKey));
      if (!ok) return;
    }

    const liveStore = O.opts.pageMode === "liveDisplay" ? O.liveStore : null;
    const endMutation = liveStore && typeof liveStore.beginMutation === "function"
      ? liveStore.beginMutation(btn)
      : null;
    btn.disabled = true;
    try {
      const url = (demo ? "/orders/demo/" : "/orders/")
        + encodeURIComponent(orderId) + "/" + encodeURIComponent(action);
      const result = await postAction(url, demo, reasonId);

      if (!result.resp.ok) {
        var defaultErr =
          action === "approve" || action === "reject" ? "ordersActionFailed" : "ordersOrderActionFailed";
        const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : defaultErr;
        toastError(msgKey, "order-action-error");
        return;
      }

      const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : null;
      toastSuccess(msgKey, fallbackSuccess, toastKey);

      // Lets an open detail panel re-read the new server status without duplicating lifecycle rules.
      document.dispatchEvent(new CustomEvent("wasla:order-action-completed", {
        detail: { orderId: orderId, action: action, demo: btn.hasAttribute("data-wasla-demo") }
      }));

      if (!liveStore) {
        try {
          await O.table.refreshOrdersTable();
        } catch (e2) {
          toastError("tableRefreshFailed", "orders-table-refresh-error");
          if (O.isDebugEnabled()) O.debugWarn("orders table refresh failed after order action", e2);
        }
      }
    } catch (e) {
      toastError(action === "approve" || action === "reject" ? "ordersActionFailed" : "ordersOrderActionFailed", "order-action-error");
      if (O.isDebugEnabled()) O.debugWarn("order action failed", e);
    } finally {
      if (typeof endMutation === "function") endMutation();
      btn.disabled = false;
    }
  }

  function initActionDelegation() {
    document.addEventListener("click", function (ev) {
      const t = ev.target;
      const btn = t && t.closest ? t.closest("[data-order-action][data-order-id]") : null;
      if (!btn) return;
      if (!btn.closest("#ordersTableHost") && !btn.closest("#ordersCardsHost") && !btn.closest("#ordersLiveDisplayCardsHost") && !btn.closest("#ordersLiveScreenHost") && !btn.closest("#ordersLiveDetailModal")) return;
      ev.preventDefault();
      handleActionClick(btn);
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initActionDelegation();
  });
})(window);
