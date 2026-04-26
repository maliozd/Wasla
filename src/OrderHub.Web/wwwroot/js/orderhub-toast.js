/**
 * OrderHub Notyf wrapper (modern toasts for Tenant UI).
 * Requires ~/lib/notyf/notyf.min.js
 */
(function (global) {
  "use strict";

  var DURATION = { success: 1400, error: 2800, warning: 2000, info: 2000 };
  var instance = null;

  function getNotyf() {
    if (instance) return instance;
    if (typeof global.Notyf !== "function") {
      return null;
    }
    instance = new global.Notyf({
      duration: DURATION.info,
      ripple: true,
      dismissible: true,
      position: { x: "right", y: "top" },
      types: [
        {
          type: "success",
          className: "orderhub-notyf orderhub-notyf--success notyf__toast--success",
          background: "#10b981",
          icon: false
        },
        {
          type: "error",
          className: "orderhub-notyf orderhub-notyf--error notyf__toast--error",
          background: "#ef4444",
          icon: false
        },
        {
          type: "warning",
          className: "orderhub-notyf orderhub-notyf--warning",
          background: "#f59e0b",
          icon: false
        },
        {
          type: "info",
          className: "orderhub-notyf orderhub-notyf--info",
          background: "#3b82f6",
          icon: false
        }
      ]
    });
    return instance;
  }

  function open(type, message, duration) {
    var n = getNotyf();
    if (!n) return;
    n.open({ type: type, message: String(message || ""), duration: duration || DURATION[type] || DURATION.info });
  }

  global.OrderHubToast = {
    success: function (message) {
      var n = getNotyf();
      if (!n) return;
      n.success({ message: String(message || ""), duration: DURATION.success });
    },
    error: function (message) {
      var n = getNotyf();
      if (!n) return;
      n.error({ message: String(message || ""), duration: DURATION.error });
    },
    warning: function (message) {
      open("warning", message, DURATION.warning);
    },
    info: function (message) {
      open("info", message, DURATION.info);
    }
  };
})(window);
