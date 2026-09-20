// Orders table actions: approve/reject and post-approval lifecycle.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
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
    },
    "hand-to-courier": {
      confirmTitle: "ordersHandToCourierConfirmTitle",
      confirmMessage: "ordersHandToCourierConfirmMessage",
      fallbackSuccess: "ordersHandToCourierSuccess",
      toastKey: "order-hand-to-courier-success"
    },
    "mark-delivered": {
      confirmTitle: "ordersMarkDeliveredConfirmTitle",
      confirmMessage: "ordersMarkDeliveredConfirmMessage",
      fallbackSuccess: "ordersMarkDeliveredSuccess",
      toastKey: "order-mark-delivered-success"
    }
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

  async function postAction(url) {
    const token = getCsrfToken();
    const headers = { "X-Requested-With": "XMLHttpRequest" };
    if (token) headers["RequestVerificationToken"] = token;

    const resp = await fetch(url, { method: "POST", headers: headers });
    let payload = null;
    try {
      payload = await resp.json();
    } catch (_) { /* ignore */ }
    return { resp: resp, payload: payload };
  }

  function toastSuccess(key, fallbackMessageKey, toastKey) {
    const message = localize(key || fallbackMessageKey);
    if (global.OrderHubToast && typeof global.OrderHubToast.success === "function") {
      global.OrderHubToast.success(message, { key: toastKey });
    }
  }

  function toastError(messageKey, toastKey) {
    const message = localize(messageKey);
    if (global.OrderHubToast && typeof global.OrderHubToast.error === "function") {
      global.OrderHubToast.error(message, { key: toastKey });
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

    const ok = global.confirm(localize(confirmTitleKey) + "\n\n" + localize(confirmMessageKey));
    if (!ok) return;

    btn.disabled = true;
    try {
      const url = "/orders/" + encodeURIComponent(orderId) + "/" + encodeURIComponent(action);
      const result = await postAction(url);

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
        detail: { orderId: orderId, action: action }
      }));

      try {
        await O.table.refreshOrdersTable();
      } catch (e2) {
        toastError("tableRefreshFailed", "orders-table-refresh-error");
        if (O.isDebugEnabled()) O.debugWarn("orders table refresh failed after order action", e2);
      }
    } catch (e) {
      toastError(action === "approve" || action === "reject" ? "ordersActionFailed" : "ordersOrderActionFailed", "order-action-error");
      if (O.isDebugEnabled()) O.debugWarn("order action failed", e);
    } finally {
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
