// Orders page core: options, localized UI helpers, table state, debug.
(function (global) {
  "use strict";

  const defaults = {
    tableUrl: "/orders/table",
    newOrdersCheckUrl: "/orders/new-orders/check",
    notificationSettingsUrl: "/notification-settings",
    notificationSettingsJsonUrl: "/notification-settings/current",
    pollingIntervalMs: 10000,
    latestReceivedAtUtc: null,
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
    const host = document.getElementById("ordersMessageHost");
    if (!host) return;
    const cls = type === "error" ? "alert-danger" : (type === "warning" ? "alert-warning" : "alert-info");
    host.innerHTML = "<div class=\"alert " + cls + " py-2 mb-2\">" + escapeHtml(message) + "</div>";
    setTimeout(function () {
      if (host.innerHTML) host.innerHTML = "";
    }, 4000);
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
    state: {
      notificationSettings: null
    },
    table: {
      knownOrderIds: new Set(),
      recentlyNewOrderIds: new Map(),
      recentlySoundPlayed: new Map(),
      SOUND_DEDUPE_MS: 120000,
      lastKnownLatestReceivedAtUtc: "",
      pendingFromCheck: null,
      hintShownForUnlock: false,
      NEW_ORDER_HIGHLIGHT_MS: 30000
    }
  };
})(window);
