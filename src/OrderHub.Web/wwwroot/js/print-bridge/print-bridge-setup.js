// Print Bridge setup page: copy helpers for example config and PowerShell command.
(function () {
  "use strict";

  var cfg = window.OrderHubPrintBridgeSetup;
  if (!cfg) return;

  var messages = cfg.messages || {};

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function showMessage(text, type) {
    var host = document.getElementById("printBridgeSetupMessageHost");
    if (!host) return;
    var cls = type === "danger" ? "alert-danger" : (type === "success" ? "alert-success" : "alert-info");
    host.innerHTML = '<div class="alert ' + cls + ' alert-dismissible small py-2" role="alert">' +
      escapeHtml(text) +
      '<button type="button" class="btn-close btn-close-sm" data-bs-dismiss="alert" aria-label="Close"></button></div>';
  }

  function copyText(text, button) {
    if (!text) return Promise.reject(new Error("empty"));

    function onSuccess() {
      if (!button) return;
      var original = button.innerHTML;
      var originalClass = button.className;
      button.innerHTML = '<i class="bi bi-check2 me-1"></i>' + escapeHtml(messages.copied || "Copied");
      button.classList.add("btn-success");
      button.classList.remove("btn-outline-secondary");
      setTimeout(function () {
        button.innerHTML = original;
        button.className = originalClass;
      }, 1800);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(onSuccess);
    }

    var ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "absolute";
    ta.style.left = "-9999px";
    document.body.appendChild(ta);
    ta.select();
    try {
      document.execCommand("copy");
      onSuccess();
      return Promise.resolve();
    } catch (e) {
      return Promise.reject(e);
    } finally {
      document.body.removeChild(ta);
    }
  }

  function bindCopyButtons() {
    var copyConfigBtn = document.getElementById("pbSetupCopyConfigBtn");
    if (copyConfigBtn) {
      copyConfigBtn.addEventListener("click", function () {
        copyText(cfg.exampleConfig || "", copyConfigBtn).catch(function () {
          showMessage(messages.copyFailed || "Copy failed", "danger");
        });
      });
    }

    var copyPsBtn = document.getElementById("pbSetupCopyPsBtn");
    if (copyPsBtn) {
      copyPsBtn.addEventListener("click", function () {
        copyText(cfg.powerShellCommand || "", copyPsBtn).catch(function () {
          showMessage(messages.copyFailed || "Copy failed", "danger");
        });
      });
    }
  }

  document.addEventListener("DOMContentLoaded", bindCopyButtons);
})();
