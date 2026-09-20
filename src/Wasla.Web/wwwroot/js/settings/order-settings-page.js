// Order automation settings page bootstrap.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  async function openNotificationSettingsModal() {
    if (!O.notificationSettings || typeof O.notificationSettings.openNotificationSettingsModal !== "function") {
      return;
    }
    await O.notificationSettings.openNotificationSettingsModal();
    const modalEl = document.getElementById("notificationSettingsModal");
    if (!modalEl) return;
    const modal = global.bootstrap && global.bootstrap.Modal
      ? global.bootstrap.Modal.getOrCreateInstance(modalEl)
      : null;
    if (modal) modal.show();
  }

  function bindNotificationSettingsButton() {
    const btn = document.getElementById("notificationSettingsBtn");
    if (!btn) return;

    btn.addEventListener("click", function () {
      openNotificationSettingsModal().catch(function (error) {
        if (O.debugWarn) O.debugWarn("openNotificationSettingsModal failed", error);
      });
    });
  }

  function maybeOpenNotificationsFromHash() {
    const hash = (global.location.hash || "").toLowerCase();
    if (hash !== "#notifications") return;
    if (!document.getElementById("notificationSettingsBtn")) return;
    openNotificationSettingsModal().catch(function (error) {
      if (O.debugWarn) O.debugWarn("openNotificationSettingsModal from hash failed", error);
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    bindNotificationSettingsButton();
    maybeOpenNotificationsFromHash();
  });
})(window);
