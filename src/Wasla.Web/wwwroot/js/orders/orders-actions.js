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

  async function postAction(url, demo) {
    const token = getCsrfToken();
    const headers = { "X-Requested-With": "XMLHttpRequest" };
    let body = undefined;
    if (token) headers["RequestVerificationToken"] = token;
    // Real order actions keep their header-only request; only the guided demo also posts the token as a form field.
    if (token && demo) {
      headers["Content-Type"] = "application/x-www-form-urlencoded";
      body = "__RequestVerificationToken=" + encodeURIComponent(token);
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
    if (!demo) {
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
      const result = await postAction(url, demo);

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
