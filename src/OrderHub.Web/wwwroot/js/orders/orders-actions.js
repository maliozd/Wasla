// Orders table actions: approve/reject (MVP local update).
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.table || typeof O.table.refreshOrdersTable !== "function") return;

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

    const confirmTitleKey = action === "approve" ? "ordersApproveConfirmTitle" : "ordersRejectConfirmTitle";
    const confirmMessageKey = action === "approve" ? "ordersApproveConfirmMessage" : "ordersRejectConfirmMessage";
    const ok = global.confirm(localize(confirmTitleKey) + "\n\n" + localize(confirmMessageKey));
    if (!ok) return;

    btn.disabled = true;
    try {
      const url = "/orders/" + encodeURIComponent(orderId) + "/" + encodeURIComponent(action);
      const result = await postAction(url);

      if (!result.resp.ok) {
        const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : "ordersActionFailed";
        toastError(msgKey, "order-action-error");
        return;
      }

      const msgKey = (result.payload && result.payload.message) ? String(result.payload.message) : null;
      toastSuccess(msgKey, action === "approve" ? "ordersApproveSuccess" : "ordersRejectSuccess", "order-action-success");

      // Refresh the table using existing polling logic (safe + keeps new-order detection behavior).
      await O.table.refreshOrdersTable();
    } catch (e) {
      toastError("ordersActionFailed", "order-action-error");
      if (O.isDebugEnabled()) O.debugWarn("order action failed", e);
    } finally {
      btn.disabled = false;
    }
  }

  function initActionDelegation() {
    const host = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!host) return;
    host.addEventListener("click", function (ev) {
      const t = ev.target;
      const btn = t && t.closest ? t.closest("[data-order-action][data-order-id]") : null;
      if (!btn) return;
      ev.preventDefault();
      handleActionClick(btn);
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initActionDelegation();
  });
})(window);

