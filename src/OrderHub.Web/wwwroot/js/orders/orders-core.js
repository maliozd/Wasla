// Orders page core: options, localized UI helpers, table state, debug.
(function (global) {
  "use strict";

  const defaults = {
    tableUrl: "/orders/table",
    notificationSettingsUrl: "/notification-settings",
    notificationSettingsJsonUrl: "/notification-settings/current",
    pollingIntervalMs: 10000,
    messages: {}
  };

  const opts = Object.assign({}, defaults, global.orderHubOrdersOptions || {});

  if (!opts.messages) {
    opts.messages = {};
  }

  const warningState = {};

  function getMessage(key) {
    const m = opts.messages;
    return (m && m[key]) || (m && m.genericError) || key;
  }

  function isDebugEnabled() {
    return localStorage.getItem("orderhub.ordersDebug") === "true";
  }

  function debugLog() {
    if (isDebugEnabled()) {
      const a = Array.prototype.slice.call(arguments);
      console.debug.apply(console, ["[OrderHub Orders]"].concat(a));
    }
  }

  function debugWarn() {
    if (isDebugEnabled()) {
      const a = Array.prototype.slice.call(arguments);
      console.warn.apply(console, ["[OrderHub Orders]"].concat(a));
    }
  }

  function escapeHtml(s) {
    return (s || "").replace(/[&<>"']/g, (c) => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;"
    }[c]));
  }

  function showOrdersWarning(key, message) {
    const now = Date.now();
    const lastShown = warningState[key] || 0;
    if (now - lastShown < 60000) return;
    warningState[key] = now;

    if (global.OrderHubToast && typeof global.OrderHubToast.warning === "function") {
      global.OrderHubToast.warning(String(message || ""));
      return;
    }

    const host = document.getElementById("ordersMessageHost");
    if (!host) {
      console.warn("[OrderHub Orders]", message);
      return;
    }

    const closeLabel = getMessage("alertClose");
    host.innerHTML =
      "<div class=\"alert alert-warning alert-dismissible fade show\" role=\"alert\">" +
      escapeHtml(message) +
      "<button type=\"button\" class=\"btn-close\" data-bs-dismiss=\"alert\" aria-label=\"" +
      escapeHtml(closeLabel) +
      "\"></button></div>";
  }

  function showMessage(message, type) {
    if (global.OrderHubToast) {
      const m = String(message || "");
      if (type === "success" && global.OrderHubToast.success) {
        global.OrderHubToast.success(m);
        return;
      }
      if (type === "error" && global.OrderHubToast.error) {
        global.OrderHubToast.error(m);
        return;
      }
      if (type === "warning" && global.OrderHubToast.warning) {
        global.OrderHubToast.warning(m);
        return;
      }
      if (global.OrderHubToast.info) {
        global.OrderHubToast.info(m);
        return;
      }
    }
    const host = document.getElementById("ordersMessageHost");
    if (!host) return;
    const cls = type === "error" ? "alert-danger" : (type === "warning" ? "alert-warning" : (type === "success" ? "alert-success" : "alert-info"));
    host.innerHTML = "<div class=\"alert " + cls + " py-2 mb-2\">" + escapeHtml(message) + "</div>";
    setTimeout(function () {
      if (host.innerHTML) host.innerHTML = "";
    }, 4000);
  }

  const SETTINGS_SAVE_TOAST_KEY = "settings-save";
  const SETTINGS_SAVE_TOAST_DURATION_MS = 3000;

  function showSettingsSaveSuccess(message) {
    const m = message || getMessage("orderSettingsSaved") || getMessage("settingsSaved");
    if (global.OrderHubToast && typeof global.OrderHubToast.success === "function") {
      global.OrderHubToast.success(m, { key: SETTINGS_SAVE_TOAST_KEY, durationMs: SETTINGS_SAVE_TOAST_DURATION_MS });
      return;
    }
    showMessage(m, "success");
  }

  function showSettingsSaveError(message) {
    const m = message || getMessage("orderSettingsUpdateFailed") || getMessage("settingsSaveFailed");
    if (global.OrderHubToast && typeof global.OrderHubToast.error === "function") {
      global.OrderHubToast.error(m, { key: SETTINGS_SAVE_TOAST_KEY, durationMs: SETTINGS_SAVE_TOAST_DURATION_MS });
      return;
    }
    showMessage(m, "error");
  }

  global.OrderHubOrders = {
    get opts() {
      return opts;
    },
    getMessage: getMessage,
    isDebugEnabled: isDebugEnabled,
    debugLog: debugLog,
    debugWarn: debugWarn,
    escapeHtml: escapeHtml,
    showOrdersWarning: showOrdersWarning,
    showMessage: showMessage,
    showSettingsSaveSuccess: showSettingsSaveSuccess,
    showSettingsSaveError: showSettingsSaveError,
    state: {
      notificationSettings: null
    },
    table: {
      knownOrderIds: new Set(),
      recentlyNewOrderIds: new Map(),
      hintShownForUnlock: false,
      NEW_ORDER_HIGHLIGHT_MS: 30000
    }
  };
})(window);
