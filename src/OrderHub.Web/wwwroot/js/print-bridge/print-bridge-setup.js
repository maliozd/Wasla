// Print Bridge setup page: copy helpers and receipt automation settings.
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

  function getAntiForgeryToken() {
    var form = document.getElementById("printBridgeSetupAntiForgery");
    if (!form) return null;
    var el = form.querySelector("input[name='__RequestVerificationToken']");
    return el ? el.value : null;
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

  function postForm(url, fields) {
    var token = getAntiForgeryToken();
    var headers = { "X-Requested-With": "fetch" };
    if (token) headers["RequestVerificationToken"] = token;

    var body = new URLSearchParams();
    Object.keys(fields || {}).forEach(function (key) {
      body.set(key, fields[key]);
    });

    return fetch(url, { method: "POST", headers: headers, body: body }).then(function (resp) {
      if (!resp.ok) {
        return resp.json().catch(function () { return {}; }).then(function (err) {
          throw new Error((err && err.message) || "request failed");
        });
      }
      return resp.json();
    });
  }

  function setOrderSettingsUi(settings) {
    var autoApprove = document.getElementById("pbSetupAutoApproveToggle");
    var autoApproveBadge = document.getElementById("pbSetupAutoApproveBadge");
    var autoPrint = document.getElementById("pbSetupAutoPrintToggle");
    var autoPrintBadge = document.getElementById("pbSetupAutoPrintBadge");
    var copyCount = document.getElementById("pbSetupReceiptCopyCountSelect");

    var autoApproveOn = !!settings.autoApproveNewOrders;
    var autoPrintOn = !!settings.autoPrintReceiptOnAutoApprove;
    var count = settings.receiptPrintCopyCount || 1;

    if (autoApprove) autoApprove.checked = autoApproveOn;
    if (autoPrint) autoPrint.checked = autoPrintOn;
    if (copyCount) copyCount.value = String(count);

    function setBadge(el, on) {
      if (!el) return;
      el.textContent = on ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
      el.classList.remove("text-bg-success", "text-bg-secondary");
      el.classList.add(on ? "text-bg-success" : "text-bg-secondary");
    }

    setBadge(autoApproveBadge, autoApproveOn);
    setBadge(autoPrintBadge, autoPrintOn);
  }

  function readOrderSettingsUi() {
    var autoApprove = document.getElementById("pbSetupAutoApproveToggle");
    var autoPrint = document.getElementById("pbSetupAutoPrintToggle");
    var copyCount = document.getElementById("pbSetupReceiptCopyCountSelect");

    return {
      autoApproveNewOrders: !!(autoApprove && autoApprove.checked),
      autoPrintReceiptOnAutoApprove: !!(autoPrint && autoPrint.checked),
      receiptPrintCopyCount: copyCount ? parseInt(copyCount.value, 10) || 1 : 1
    };
  }

  function bindOrderSettings() {
    var autoApprove = document.getElementById("pbSetupAutoApproveToggle");
    var autoPrint = document.getElementById("pbSetupAutoPrintToggle");
    var copyCount = document.getElementById("pbSetupReceiptCopyCountSelect");
    if (!autoApprove && !autoPrint && !copyCount) return;

    function onChange() {
      var previous = readOrderSettingsUi();
      var next = readOrderSettingsUi();
      setOrderSettingsUi(next);

      postForm(cfg.orderSettingsUrl, {
        autoApproveNewOrders: next.autoApproveNewOrders ? "true" : "false",
        autoPrintReceiptOnAutoApprove: next.autoPrintReceiptOnAutoApprove ? "true" : "false",
        receiptPrintCopyCount: String(next.receiptPrintCopyCount)
      })
        .then(function (data) {
          setOrderSettingsUi(data);
          showMessage(data.message || messages.orderSettingsSaved, "success");
        })
        .catch(function (e) {
          setOrderSettingsUi(previous);
          showMessage((e && e.message) || messages.orderSettingsUpdateFailed, "danger");
        });
    }

    if (autoApprove) autoApprove.addEventListener("change", onChange);
    if (autoPrint) autoPrint.addEventListener("change", onChange);
    if (copyCount) copyCount.addEventListener("change", onChange);
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

  document.addEventListener("DOMContentLoaded", function () {
    bindCopyButtons();
    bindOrderSettings();
  });
})();
