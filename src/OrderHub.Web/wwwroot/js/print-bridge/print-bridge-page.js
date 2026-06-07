// Print Bridge management page: devices, tokens, receipt settings, copy helpers.
(function () {
  "use strict";

  var cfg = window.OrderHubPrintBridge;
  if (!cfg) return;

  var messages = cfg.messages || {};
  var currentToken = null;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function getAntiForgeryToken() {
    var form = document.getElementById("printBridgeAntiForgery");
    if (!form) return null;
    var el = form.querySelector("input[name='__RequestVerificationToken']");
    return el ? el.value : null;
  }

  function showMessage(text, type) {
    var host = document.getElementById("printBridgeMessageHost");
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
      button.innerHTML = '<i class="bi bi-check2 me-1"></i>' + escapeHtml(messages.copied || "Copied");
      button.classList.add("btn-success");
      button.classList.remove("btn-outline-secondary", "btn-outline-dark");
      setTimeout(function () {
        button.innerHTML = original;
        button.classList.remove("btn-success");
        if (button.id === "printBridgeCopyTokenBtn") {
          button.classList.add("btn-outline-dark");
        } else {
          button.classList.add("btn-outline-secondary");
        }
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

  function showToken(token, tokenMessage) {
    currentToken = token || null;
    var box = document.getElementById("printBridgeTokenBox");
    var value = document.getElementById("printBridgeTokenValue");
    var msg = document.getElementById("printBridgeTokenMessage");
    if (!box || !value) return;

    if (!currentToken) {
      box.classList.add("d-none");
      value.textContent = "";
      if (msg) msg.textContent = "";
      return;
    }

    if (msg) msg.textContent = tokenMessage || messages.tokenCreatedCopyNow || "";
    value.textContent = currentToken;
    box.classList.remove("d-none");
    box.scrollIntoView({ behavior: "smooth", block: "nearest" });
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

  function formatLastSeen(iso) {
    if (!iso) return messages.emptyValue || "—";
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return messages.emptyValue || "—";
      return d.toLocaleString();
    } catch (_) {
      return messages.emptyValue || "—";
    }
  }

  function displayValue(value) {
    if (value == null || String(value).trim() === "") return messages.emptyValue || "—";
    return String(value);
  }

  function deviceStatusBadge(device) {
    if (!device.isActive) {
      return { cls: "text-bg-secondary", text: messages.deviceInactive || "Inactive" };
    }
    if (device.isConnected) {
      return { cls: "text-bg-success", text: messages.connected || "Connected" };
    }
    return { cls: "text-bg-warning", text: messages.notConnected || "Not connected" };
  }

  function renderOverview(devices, quota) {
    var body = document.getElementById("printBridgeOverviewBody");
    var badge = document.getElementById("printBridgeOverviewBadge");
    if (!body) return;

    devices = devices || [];
    var connectedCount = devices.filter(function (d) { return d.isActive && d.isConnected; }).length;

    if (badge) {
      if (devices.length === 0) {
        badge.classList.add("d-none");
      } else {
        badge.classList.remove("d-none");
        badge.textContent = connectedCount > 0
          ? (messages.connected || "Connected")
          : (messages.notConnected || "Not connected");
        badge.classList.remove("text-bg-success", "text-bg-secondary", "text-bg-warning");
        badge.classList.add(connectedCount > 0 ? "text-bg-success" : "text-bg-secondary");
      }
    }

    if (devices.length === 0) {
      body.innerHTML =
        '<div class="text-center py-3">' +
          '<p class="text-muted small mb-2">' + escapeHtml(messages.noDevicesYet || "") + '</p>' +
          '<p class="small mb-0">' + escapeHtml(messages.createFirstDevice || "") + '</p>' +
        '</div>';
      return;
    }

    var rows = devices.map(function (device) {
      var status = deviceStatusBadge(device);
      return (
        '<li class="list-group-item px-0 py-2 border-0 border-bottom">' +
          '<div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-1">' +
            '<span class="small fw-semibold">' + escapeHtml(device.name) + '</span>' +
            '<span class="badge ' + status.cls + '">' + escapeHtml(status.text) + '</span>' +
          '</div>' +
          '<div class="small text-muted">' +
            escapeHtml(messages.lastSeen || "Last seen") + ': ' + escapeHtml(formatLastSeen(device.lastSeenAtUtc)) +
            ' · ' + escapeHtml(messages.machineName || "Machine") + ': ' + escapeHtml(displayValue(device.machineName)) +
            ' · ' + escapeHtml(messages.printerName || "Printer") + ': ' + escapeHtml(displayValue(device.printerName)) +
            ' · ' + escapeHtml(messages.appVersion || "Version") + ': ' + escapeHtml(displayValue(device.appVersion)) +
          '</div>' +
        '</li>'
      );
    }).join("");

    body.innerHTML = '<ul class="list-group list-group-flush small mb-0">' + rows + '</ul>';
  }

  function renderDevicesTable(devices) {
    var panel = document.getElementById("printBridgeDevicesPanel");
    if (!panel) return;

    devices = devices || [];
    if (devices.length === 0) {
      panel.innerHTML = '<div class="text-muted small py-2">' + escapeHtml(messages.noDevicesYet || "") + '</div>';
      return;
    }

    var rows = devices.map(function (device) {
      var activeBadgeCls = device.isActive ? "text-bg-success" : "text-bg-secondary";
      var activeText = device.isActive ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
      return (
        '<tr data-device-id="' + escapeHtml(device.id) + '">' +
          '<td>' + escapeHtml(device.name) + '</td>' +
          '<td>' +
            '<div class="d-flex align-items-center gap-2">' +
              '<span class="badge ' + activeBadgeCls + ' pb-device-active-badge">' + escapeHtml(activeText) + '</span>' +
              '<div class="form-check form-switch m-0">' +
                '<input class="form-check-input pb-device-active-toggle" type="checkbox" role="switch" ' +
                  'data-device-id="' + escapeHtml(device.id) + '" ' + (device.isActive ? "checked" : "") + ' />' +
              '</div>' +
            '</div>' +
          '</td>' +
          '<td class="text-end">' +
            '<button type="button" class="btn btn-sm btn-outline-secondary pb-regenerate-token-btn" ' +
              'data-device-id="' + escapeHtml(device.id) + '" data-device-name="' + escapeHtml(device.name) + '">' +
              '<i class="bi bi-arrow-repeat me-1"></i>' + escapeHtml(messages.regenerateToken || "Regenerate") +
            '</button>' +
          '</td>' +
        '</tr>'
      );
    }).join("");

    panel.innerHTML =
      '<div class="table-responsive">' +
        '<table class="table table-sm align-middle mb-0">' +
          '<thead><tr>' +
            '<th>' + escapeHtml(messages.deviceName || "Device") + '</th>' +
            '<th>' + escapeHtml(messages.status || "Status") + '</th>' +
            '<th class="text-end">' + escapeHtml(messages.actions || "Actions") + '</th>' +
          '</tr></thead>' +
          '<tbody id="printBridgeDevicesTableBody">' + rows + '</tbody>' +
        '</table>' +
      '</div>';

    bindRegenerateButtons();
    bindActiveToggles();
  }

  function updateQuotaUi(quota) {
    quota = quota || {};
    var canCreate = !!quota.canCreateActiveDevice;
    var exceeds = !!quota.activeCountExceedsLimit;

    var createBtn = document.getElementById("printBridgeCreateDeviceBtn");
    var nameInput = document.getElementById("printBridgeDeviceName");
    var exceededAlert = document.getElementById("printBridgeLimitExceededAlert");
    var reachedAlert = document.getElementById("printBridgeLimitReachedAlert");

    if (createBtn) createBtn.disabled = !canCreate;
    if (nameInput) nameInput.disabled = !canCreate;

    if (exceededAlert) {
      exceededAlert.classList.toggle("d-none", !exceeds);
    }

    if (reachedAlert) {
      reachedAlert.classList.toggle("d-none", canCreate || exceeds);
    }
  }

  function renderAll(state) {
    if (!state) return;
    renderOverview(state.devices, state.quota);
    renderDevicesTable(state.devices);
    updateQuotaUi(state.quota);
  }

  function refreshDevices() {
    return fetch(cfg.devicesUrl || "/print-bridge/devices", {
      headers: { "X-Requested-With": "XMLHttpRequest" }
    })
      .then(function (resp) {
        if (!resp.ok) throw new Error("HTTP " + resp.status);
        return resp.json();
      })
      .then(function (data) {
        renderAll({ devices: data.devices || [], quota: data.quota || {} });
        return data;
      });
  }

  function applyMutationResponse(data) {
    if (data && (data.devices || data.quota)) {
      renderAll({ devices: data.devices || [], quota: data.quota || {} });
    } else {
      return refreshDevices();
    }
    return Promise.resolve(data);
  }

  function bindCreateDevice() {
    var btn = document.getElementById("printBridgeCreateDeviceBtn");
    var nameInput = document.getElementById("printBridgeDeviceName");
    if (!btn) return;

    btn.addEventListener("click", function () {
      if (btn.disabled) return;

      btn.disabled = true;
      var fields = {};
      if (nameInput && nameInput.value) fields.deviceName = nameInput.value;

      postForm(cfg.createDeviceUrl, fields)
        .then(function (data) {
          showToken(data.token, data.tokenMessage);
          if (nameInput) nameInput.value = "";
          showMessage(data.message || messages.deviceCreatedSuccessfully, "success");
          return applyMutationResponse(data);
        })
        .catch(function (e) {
          var text = (e && e.message) || messages.tokenGenerateFailed || "Failed";
          showMessage(text, "danger");
          return refreshDevices();
        });
    });
  }

  function bindRegenerateButtons() {
    document.querySelectorAll(".pb-regenerate-token-btn").forEach(function (btn) {
      if (btn.getAttribute("data-bound") === "1") return;
      btn.setAttribute("data-bound", "1");

      btn.addEventListener("click", function () {
        var deviceId = btn.getAttribute("data-device-id");
        var deviceName = btn.getAttribute("data-device-name") || "";
        if (!deviceId) return;

        var confirmText = (messages.regenerateConfirm || "Regenerate token?")
          .replace("{0}", deviceName);
        if (!window.confirm(confirmText)) return;

        btn.disabled = true;
        var url = (cfg.regenerateTokenUrlTemplate || "").replace("{id}", deviceId);

        postForm(url, {})
          .then(function (data) {
            showToken(data.token, data.tokenMessage);
            return applyMutationResponse(data);
          })
          .catch(function () {
            showMessage(messages.tokenRegenerateFailed || "Failed", "danger");
          })
          .finally(function () {
            btn.disabled = false;
          });
      });
    });
  }

  function bindActiveToggles() {
    document.querySelectorAll(".pb-device-active-toggle").forEach(function (toggle) {
      if (toggle.getAttribute("data-bound") === "1") return;
      toggle.setAttribute("data-bound", "1");

      toggle.addEventListener("change", function () {
        var deviceId = toggle.getAttribute("data-device-id");
        if (!deviceId) return;

        var row = toggle.closest("tr");
        var badge = row ? row.querySelector(".pb-device-active-badge") : null;
        var previous = !toggle.checked;
        var isActive = toggle.checked;

        if (badge) {
          badge.textContent = isActive ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
          badge.classList.remove("text-bg-success", "text-bg-secondary");
          badge.classList.add(isActive ? "text-bg-success" : "text-bg-secondary");
        }

        var url = (cfg.setActiveUrlTemplate || "").replace("{id}", deviceId);
        postForm(url, { isActive: isActive ? "true" : "false" })
          .then(function (data) {
            return applyMutationResponse(data);
          })
          .catch(function (e) {
            toggle.checked = previous;
            if (badge) {
              badge.textContent = previous ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
              badge.classList.remove("text-bg-success", "text-bg-secondary");
              badge.classList.add(previous ? "text-bg-success" : "text-bg-secondary");
            }
            var text = (e && e.message) || messages.deviceUpdateFailed || "Update failed";
            showMessage(text, "danger");
            return refreshDevices();
          });
      });
    });
  }

  function setOrderSettingsUi(settings) {
    var autoApprove = document.getElementById("pbAutoApproveToggle");
    var autoApproveBadge = document.getElementById("pbAutoApproveBadge");
    var autoPrint = document.getElementById("pbAutoPrintReceiptToggle");
    var autoPrintBadge = document.getElementById("pbAutoPrintReceiptBadge");
    var copyCount = document.getElementById("pbReceiptPrintCopyCountSelect");

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
    var autoApprove = document.getElementById("pbAutoApproveToggle");
    var autoPrint = document.getElementById("pbAutoPrintReceiptToggle");
    var copyCount = document.getElementById("pbReceiptPrintCopyCountSelect");

    return {
      autoApproveNewOrders: !!(autoApprove && autoApprove.checked),
      autoPrintReceiptOnAutoApprove: !!(autoPrint && autoPrint.checked),
      receiptPrintCopyCount: copyCount ? parseInt(copyCount.value, 10) || 1 : 1
    };
  }

  function bindOrderSettings() {
    var autoApprove = document.getElementById("pbAutoApproveToggle");
    var autoPrint = document.getElementById("pbAutoPrintReceiptToggle");
    var copyCount = document.getElementById("pbReceiptPrintCopyCountSelect");

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
          showMessage(data.message || messages.orderSettingsSaved, "info");
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
    var copyTokenBtn = document.getElementById("printBridgeCopyTokenBtn");
    if (copyTokenBtn) {
      copyTokenBtn.addEventListener("click", function () {
        if (!currentToken) {
          showMessage(messages.maskedToken || messages.tokenGenerateFailed, "danger");
          return;
        }
        copyText(currentToken, copyTokenBtn).catch(function () {
          showMessage(messages.copyFailed || "Copy failed", "danger");
        });
      });
    }

    var copyConfigBtn = document.getElementById("printBridgeCopyConfigBtn");
    if (copyConfigBtn) {
      copyConfigBtn.addEventListener("click", function () {
        copyText(cfg.exampleConfig || "", copyConfigBtn).catch(function () {
          showMessage(messages.copyFailed || "Copy failed", "danger");
        });
      });
    }

    var copyPsBtn = document.getElementById("printBridgeCopyPsBtn");
    if (copyPsBtn) {
      copyPsBtn.addEventListener("click", function () {
        copyText(cfg.powerShellCommand || "", copyPsBtn).catch(function () {
          showMessage(messages.copyFailed || "Copy failed", "danger");
        });
      });
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (cfg.initialState) {
      renderAll(cfg.initialState);
    }
    bindCreateDevice();
    bindOrderSettings();
    bindCopyButtons();
  });
})();
