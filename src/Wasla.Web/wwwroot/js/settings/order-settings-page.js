// Order automation settings page bootstrap.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.notificationSettings) return;

  function bindNotificationSettingsButton() {
    const btn = document.getElementById("notificationSettingsBtn");
    if (!btn) return;

    btn.addEventListener("click", function () {
      O.notificationSettings.show(btn);
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    bindNotificationSettingsButton();
    if (typeof O.notificationSettings.load === "function") {
      O.notificationSettings.load();
    }
  });
})(window);
