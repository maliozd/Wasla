// Orders table actions: approve/reject and post-approval lifecycle.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.table || typeof O.table.refreshOrdersTable !== "function") return;

  /**
   * @type {Record<string, { routine: boolean, confirmTitle: string, confirmMessage: string, confirmAction: string,
   *   confirmClass: string, pending: string, requiredStatus: string, resultStatus: string, fallbackSuccess: string,
   *   toastKey: string }>}
   */
  // Approve starts preparation (WAS-185), so Mark ready is the only lifecycle action after it. Approve and Mark ready
  // follow the employee's confirmation preference (WAS-119); Reject always confirms.
  const actionSpecs = {
    approve: {
      routine: true,
      confirmTitle: "ordersApproveConfirmTitle",
      confirmMessage: "ordersApproveConfirmMessage",
      confirmAction: "ordersApproveConfirmAction",
      confirmClass: "btn-success",
      pending: "ordersApproving",
      requiredStatus: "New",
      resultStatus: "Preparing",
      fallbackSuccess: "ordersApproveSuccess",
      toastKey: "order-approve-success"
    },
    reject: {
      routine: false,
      confirmTitle: "ordersRejectConfirmTitle",
      confirmMessage: "ordersRejectPlainConfirmMessage",
      confirmAction: "ordersRejectConfirmAction",
      confirmClass: "btn-danger",
      pending: "ordersRejecting",
      requiredStatus: "New",
      resultStatus: "Cancelled",
      fallbackSuccess: "ordersRejectSuccess",
      toastKey: "order-reject-success"
    },
    "mark-ready": {
      routine: true,
      confirmTitle: "ordersMarkReadyConfirmTitle",
      confirmMessage: "ordersMarkReadyConfirmMessage",
      confirmAction: "ordersMarkReadyConfirmAction",
      confirmClass: "btn-primary",
      pending: "ordersMarkingReady",
      requiredStatus: "Preparing",
      resultStatus: "ReadyForPickup",
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

  function toastWarning(messageKey, toastKey) {
    const message = localize(messageKey);
    if (global.WaslaToast && typeof global.WaslaToast.warning === "function") {
      global.WaslaToast.warning(message, { key: toastKey });
    }
  }

  // Reject with a reason (Trendyol GO): the one dialog of the page shows the clicked order's platform group,
  // preselects nothing, and resolves with the chosen reason id, or null when the operator cancels. Platforms without a
  // group use the shared confirmation. The server checks the reason again against the stored order's platform.
  let rejectDialogOpen = false;

  // Orders with a dialog open or a request out. Every entry point for an order checks it (board, list, focus, detail),
  // so a card replaced by a refresh or a second entry point cannot start a second dialog or a second post.
  const busyOrders = new Set();

  function rejectReasonGroup(platform) {
    const dialog = document.querySelector("[data-order-reject-dialog]");
    if (!dialog || !platform || !global.WaslaConfirm || !global.bootstrap || !global.bootstrap.Modal) return null;
    const groups = dialog.querySelectorAll("[data-order-reject-reasons]");
    for (let i = 0; i < groups.length; i++) {
      if (groups[i].getAttribute("data-order-reject-reasons") === platform) return { dialog: dialog, group: groups[i] };
    }
    return null;
  }

  // Where focus goes when the action's own button is gone or inside a closed modal: the order's details button on the
  // Live Screen (the detail modal was closed for the dialog, or the card was re-rendered), otherwise the order host.
  function focusFallback(orderId) {
    const host = document.getElementById("ordersLiveScreenHost");
    const details = host ? host.querySelectorAll("[data-order-detail]") : [];
    for (let i = 0; i < details.length; i++) {
      if (details[i].getAttribute("data-order-detail") === String(orderId)) return details[i];
    }
    return host;
  }

  function focusAfterReject(btn, orderId) {
    const target = btn && btn.isConnected && !btn.closest(".modal") ? btn : focusFallback(orderId);
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  // The order the dialog is about: its code, total and a copy of the platform badge already on the page.
  function orderIdentity(btn) {
    const scope = btn.closest ? btn.closest("[data-order-id]:not([data-order-action])") : null;
    return {
      platform: scope ? scope.querySelector(".platform-badge") : null,
      primary: btn.getAttribute("data-order-code") || "",
      detail: btn.getAttribute("data-order-total") || ""
    };
  }

  async function chooseRejectReason(found, btn, orderId, spec) {
    // Bootstrap does not stack modals: a Reject inside the order detail modal closes it first, after it has finished
    // opening when it is still opening.
    if (!await global.WaslaConfirm.whenHidden(btn.closest(".modal"))) return null;

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
    global.WaslaConfirm.renderIdentity(dialog.querySelector("[data-order-reject-identity]"), orderIdentity(btn));

    const submitBtn = dialog.querySelector("[data-order-reject-confirm]");
    const unavailableNotice = dialog.querySelector("[data-order-reject-stale]");
    const context = dialog.querySelectorAll("[data-order-reject-context]");
    // Like the shared confirmation: when a refresh shows the order can no longer be rejected, the dialog says so and
    // only Cancel remains.
    function setUnavailable(text) {
      unavailableNotice.textContent = text || "";
      unavailableNotice.hidden = !text;
      submitBtn.hidden = !!text;
      group.hidden = !!text;
      group.disabled = !!text;
      for (let i = 0; i < context.length; i++) context[i].hidden = !!text;
    }
    setUnavailable(null);

    return new Promise(function (resolve) {
      const modal = global.bootstrap.Modal.getOrCreateInstance(dialog);
      let chosen = null;
      // Set by the first of Reject, Cancel or Escape: later clicks while the dialog fades in or out change nothing.
      let closing = false;
      let unavailable = false;

      function close(reasonId) {
        if (closing) return;
        closing = true;
        chosen = reasonId;
        // While the dialog is still opening, it closes as soon as it has opened.
        global.WaslaConfirm.whenHidden(dialog);
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
        if (closing || unavailable) return;
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
      function onShown() { if (!closing && !unavailable && inputs.length) inputs[0].focus(); }
      function onRendered(ev) {
        if (closing || unavailable) return;
        const text = staleOrderText(ev, orderId, spec);
        if (!text) return;
        unavailable = true;
        if (error) error.hidden = true;
        setUnavailable(text);
        const cancel = dialog.querySelector(".modal-footer [data-bs-dismiss=\"modal\"]");
        if (cancel) cancel.focus();
      }
      function onHidden() {
        form.removeEventListener("submit", onSubmit);
        dialog.removeEventListener("click", onClick);
        document.removeEventListener("keydown", onKeydown);
        dialog.removeEventListener("hide.bs.modal", onHide);
        group.removeEventListener("change", onChange);
        dialog.removeEventListener("shown.bs.modal", onShown);
        document.removeEventListener("wasla:live-rendered", onRendered);
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
      if (O.opts.pageMode === "liveDisplay") document.addEventListener("wasla:live-rendered", onRendered);
      modal.show();
    });
  }

  // On the Live Screen a refresh can change the order behind an open dialog. It never retargets the dialog: when the
  // action stops being possible, the dialog says so and only Close remains.
  function watchOrder(orderId, spec) {
    if (O.opts.pageMode !== "liveDisplay") return null;
    return function (dialog) {
      function onRendered(ev) {
        const text = staleOrderText(ev, orderId, spec);
        if (text) dialog.invalidate(text);
      }
      document.addEventListener("wasla:live-rendered", onRendered);
      return function () { document.removeEventListener("wasla:live-rendered", onRendered); };
    };
  }

  /** Why this refresh ended the action for the order, or null while the action is still possible. */
  function staleOrderText(ev, orderId, spec) {
    const orders = ev.detail && ev.detail.orders;
    if (!Array.isArray(orders)) return null;
    const order = orders.filter(function (o) { return String(o.id) === String(orderId); })[0];
    if (!order) return localize("ordersConfirmGone");
    if (order.status === spec.requiredStatus) return null;
    return localize("ordersConfirmStale").replace("{0}", localize("status" + order.status));
  }

  /** Resolves with what to send, or null when nothing should be sent. */
  async function resolveConfirmation(btn, orderId, action, spec, demo) {
    if (demo) return {};

    const reasons = action === "reject" ? rejectReasonGroup(btn.getAttribute("data-order-platform")) : null;
    if (reasons) {
      rejectDialogOpen = true;
      try {
        const reasonId = await chooseRejectReason(reasons, btn, orderId, spec);
        return reasonId ? { reasonId: reasonId } : null;
      } finally {
        rejectDialogOpen = false;
      }
    }

    if (spec.routine && O.actionPreference && !O.actionPreference.confirmsRoutineActions()) return {};
    if (!global.WaslaConfirm) return null;

    const confirmed = await global.WaslaConfirm.ask({
      title: localize(spec.confirmTitle),
      body: localize(spec.confirmMessage),
      hint: spec.routine ? localize("ordersConfirmHint") : "",
      identity: orderIdentity(btn),
      confirmLabel: localize(spec.confirmAction),
      confirmClass: spec.confirmClass,
      initialFocus: spec.routine ? "confirm" : "cancel",
      trigger: btn,
      focusFallback: function () { return focusFallback(orderId); },
      closeFirst: btn.closest(".modal"),
      watch: watchOrder(orderId, spec)
    });
    return confirmed ? {} : null;
  }

  function currentButtons(orderId, action) {
    return Array.prototype.slice.call(
      document.querySelectorAll("[data-order-action=\"" + action + "\"][data-order-id=\"" + orderId + "\"]"));
  }

  // Shows progress on every button for this action and order; a dialog may have outlived the button that opened it.
  function setPending(orderId, action, labelKey, code) {
    const label = localize(labelKey);
    if (global.WaslaConfirm) global.WaslaConfirm.announce(code ? code + ": " + label : label);
    const saved = currentButtons(orderId, action).map(function (el) {
      const previous = { el: el, html: el.innerHTML, disabled: el.disabled };
      const spinner = document.createElement("span");
      spinner.className = "spinner-border spinner-border-sm wasla-action-spinner me-1";
      spinner.setAttribute("aria-hidden", "true");
      el.textContent = "";
      el.appendChild(spinner);
      el.appendChild(document.createTextNode(label));
      el.disabled = true;
      el.setAttribute("aria-busy", "true");
      return previous;
    });
    return function clear() {
      saved.forEach(function (previous) {
        if (!previous.el.isConnected) return;
        previous.el.innerHTML = previous.html;
        previous.el.disabled = previous.disabled;
        previous.el.removeAttribute("aria-busy");
      });
    };
  }

  // No answer, a timeout or a server error is not proof of failure: the provider may have acted. Nothing is resent; the
  // current state is read and reported.
  // A redirect counts too: with an ended session the sign-in page answers 200 once fetch has followed it.
  function isUncertainResponse(resp) {
    return resp.redirected || resp.status >= 500 || resp.status === 408;
  }

  function nextSnapshot(startRead) {
    return new Promise(function (resolve) {
      let timer = null;
      function onRendered(ev) {
        clearTimeout(timer);
        document.removeEventListener("wasla:live-rendered", onRendered);
        resolve(ev.detail || null);
      }
      timer = setTimeout(function () {
        document.removeEventListener("wasla:live-rendered", onRendered);
        resolve(null);
      }, 6000);
      document.addEventListener("wasla:live-rendered", onRendered);
      startRead();
    });
  }

  // The order's status from a fresh read, or null when nothing could be read or the order is not in what was read: the
  // Live Screen leaves ended orders out, and the Orders list has filters and pages.
  async function readOrderStatus(orderId) {
    const liveStore = O.opts.pageMode === "liveDisplay" ? O.liveStore : null;
    if (liveStore && typeof liveStore.requestRefresh === "function") {
      const snapshot = await nextSnapshot(function () { liveStore.requestRefresh(); });
      const orders = snapshot && snapshot.orders;
      const order = Array.isArray(orders) ? orders.filter(function (o) { return String(o.id) === String(orderId); })[0] : null;
      return order ? order.status : null;
    }
    if (await O.table.refreshOrdersTable() === false) return null;
    const row = document.querySelector("[data-order-status][data-order-id=\"" + orderId + "\"]");
    return row ? row.getAttribute("data-order-status") : null;
  }

  function announceCompleted(orderId, action, demo) {
    // Lets an open detail panel re-read the new server status without duplicating lifecycle rules.
    document.dispatchEvent(new CustomEvent("wasla:order-action-completed", {
      detail: { orderId: orderId, action: action, demo: demo }
    }));
  }

  async function reconcileUncertain(orderId, action, spec) {
    toastWarning("ordersActionChecking", "order-action-error");
    const status = await readOrderStatus(orderId);
    if (status === spec.resultStatus) {
      toastSuccess(null, spec.fallbackSuccess, spec.toastKey);
      announceCompleted(orderId, action, false);
    } else if (status === spec.requiredStatus) {
      toastWarning("ordersActionStillWaiting", "order-action-error");
    } else if (status) {
      if (global.WaslaToast && typeof global.WaslaToast.warning === "function") {
        global.WaslaToast.warning(localize("ordersConfirmStale").replace("{0}", localize("status" + status)), { key: "order-action-error" });
      }
    } else {
      toastWarning("ordersActionUnverified", "order-action-error");
    }
  }

  async function submitAction(btn, orderId, action, spec, demo, reasonId) {
    const liveStore = O.opts.pageMode === "liveDisplay" ? O.liveStore : null;
    const endMutation = liveStore && typeof liveStore.beginMutation === "function"
      ? liveStore.beginMutation(btn)
      : null;
    const clearPending = demo ? null : setPending(orderId, action, spec.pending, btn.getAttribute("data-order-code"));
    btn.disabled = true;
    let uncertain = false;
    try {
      const url = (demo ? "/orders/demo/" : "/orders/")
        + encodeURIComponent(orderId) + "/" + encodeURIComponent(action);
      const result = await postAction(url, demo, reasonId);

      if (!result.resp.ok || result.resp.redirected) {
        if (!demo && isUncertainResponse(result.resp)) {
          uncertain = true;
        } else {
          const defaultErr =
            action === "approve" || action === "reject" ? "ordersActionFailed" : "ordersOrderActionFailed";
          const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : defaultErr;
          toastError(msgKey, "order-action-error");
        }
      } else {
        const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : null;
        toastSuccess(msgKey, spec.fallbackSuccess, spec.toastKey);
        announceCompleted(orderId, action, btn.hasAttribute("data-wasla-demo"));

        if (!liveStore) {
          try {
            await O.table.refreshOrdersTable();
          } catch (e2) {
            toastError("tableRefreshFailed", "orders-table-refresh-error");
            if (O.isDebugEnabled()) O.debugWarn("orders table refresh failed after order action", e2);
          }
        }
      }
    } catch (e) {
      if (demo) toastError(action === "approve" || action === "reject" ? "ordersActionFailed" : "ordersOrderActionFailed", "order-action-error");
      else uncertain = true;
      if (O.isDebugEnabled()) O.debugWarn("order action failed", e);
    } finally {
      if (typeof endMutation === "function") endMutation();
      if (typeof clearPending === "function") clearPending();
      btn.disabled = false;
    }

    // After the Live Screen's mutation guard ends: the read below must be allowed to run.
    if (uncertain) await reconcileUncertain(orderId, action, spec);
  }

  async function handleActionClick(btn) {
    const orderId = btn.getAttribute("data-order-id");
    const action = btn.getAttribute("data-order-action");
    if (!orderId || !action) return;
    const spec = actionSpecs[action];
    if (!spec) return;

    if (busyOrders.has(orderId) || rejectDialogOpen || (global.WaslaConfirm && global.WaslaConfirm.isOpen())) return;

    const demo = btn.hasAttribute("data-wasla-demo");
    busyOrders.add(orderId);
    try {
      const decision = await resolveConfirmation(btn, orderId, action, spec, demo);
      if (decision) await submitAction(btn, orderId, action, spec, demo, decision.reasonId || null);
    } finally {
      busyOrders.delete(orderId);
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
