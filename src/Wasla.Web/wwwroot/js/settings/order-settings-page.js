// Order automation settings page bootstrap.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.notificationSettings) return;

  function bindNotificationSettingsButton() {
    const btn = document.getElementById("notificationSettingsBtn");
    if (!btn) return;

    btn.addEventListener("click", async function () {
      await O.notificationSettings.openNotificationSettingsModal();
      const modalEl = document.getElementById("notificationSettingsModal");
      if (!modalEl) return;
      const modal = global.bootstrap && global.bootstrap.Modal
        ? global.bootstrap.Modal.getOrCreateInstance(modalEl)
        : null;
      if (modal) modal.show();
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    bindNotificationSettingsButton();
  });
})(window);
