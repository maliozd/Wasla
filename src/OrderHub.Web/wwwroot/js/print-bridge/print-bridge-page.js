// Print Bridge management page: devices, tokens, receipt settings, copy helpers.
(function () {
  "use strict";

  var cfg = window.OrderHubPrintBridge;
  if (!cfg) return;

  var messages = cfg.messages || {};
  var currentToken = null;
  var lastQuota = null;

  function formatMsg(template, value) {
    return String(template || "").replace("{0}", String(value == null ? "" : value));
  }

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

  function copyText(text, button, options) {
    options = options || {};
    if (!text) return Promise.reject(new Error("empty"));

    var successMessage = options.successMessage || messages.copied || "Copied";

    function onSuccess() {
      if (!button) return;
      var original = button.innerHTML;
      var originalClass = button.className;
      button.innerHTML = '<i class="bi bi-check2 me-1"></i>' + escapeHtml(successMessage);
      button.classList.add("btn-success");
      button.classList.remove("btn-outline-secondary", "btn-outline-dark", "btn-primary");
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

  function hideToken() {
    currentToken = null;
    var box = document.getElementById("printBridgeTokenBox");
    var value = document.getElementById("printBridgeTokenValue");
    var title = document.getElementById("printBridgeTokenTitle");
    var notice = document.getElementById("printBridgeTokenNotice");
    var warning = document.getElementById("printBridgeTokenWarning");
    if (!box || !value) return;

    box.classList.add("d-none");
    value.textContent = "";
    if (title) title.textContent = "";
    if (notice) notice.textContent = "";
    if (warning) {
      warning.textContent = "";
      warning.classList.add("d-none");
    }
  }

  function showToken(token, options) {
    options = options || {};
    currentToken = token || null;
    var box = document.getElementById("printBridgeTokenBox");
    var value = document.getElementById("printBridgeTokenValue");
    var title = document.getElementById("printBridgeTokenTitle");
    var notice = document.getElementById("printBridgeTokenNotice");
    var warning = document.getElementById("printBridgeTokenWarning");
    if (!box || !value) return;

    if (!currentToken) {
      hideToken();
      return;
    }

    var mode = options.mode || "create";
    var titleText = options.title
      || (mode === "regenerate" ? messages.tokenRegenerated : messages.tokenCreated)
      || "";
    var noticeText = options.notice || messages.tokenShownOnce || "";
    var warningText = options.warning || "";

    if (title) title.textContent = titleText;
    if (notice) notice.textContent = noticeText;
    if (warning) {
      if (warningText) {
        warning.textContent = warningText;
        warning.classList.remove("d-none");
      } else {
        warning.textContent = "";
        warning.classList.add("d-none");
      }
    }

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
      return { cls: "text-bg-secondary", text: messages.inactive || "Inactive" };
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
    quota = quota || {};

    var activeCount = quota.activeDeviceCount != null ? quota.activeDeviceCount : 0;
    var allowedCount = quota.allowedActiveDeviceCount != null ? quota.allowedActiveDeviceCount : 1;
    var connectedCount = quota.connectedDeviceCount != null
      ? quota.connectedDeviceCount
      : devices.filter(function (d) { return d.isActive && d.isConnected; }).length;
    var exceeds = !!quota.activeCountExceedsLimit;
    var latestLastSeen = quota.latestLastSeenAtUtc || null;

    if (!latestLastSeen && devices.length > 0) {
      devices.forEach(function (d) {
        if (!d.lastSeenAtUtc) return;
        if (!latestLastSeen || new Date(d.lastSeenAtUtc) > new Date(latestLastSeen)) {
          latestLastSeen = d.lastSeenAtUtc;
        }
      });
    }

    var overallConnected = connectedCount > 0;
    if (badge) {
      if (devices.length === 0) {
        badge.classList.add("d-none");
      } else {
        badge.classList.remove("d-none");
        badge.textContent = overallConnected
          ? (messages.connected || "Connected")
          : (messages.notConnected || "Not connected");
        badge.classList.remove("text-bg-success", "text-bg-secondary", "text-bg-warning");
        badge.classList.add(overallConnected ? "text-bg-success" : "text-bg-secondary");
      }
    }

    if (devices.length === 0) {
      body.innerHTML =
        '<div class="text-center py-4">' +
          '<i class="bi bi-printer text-muted fs-4 d-block mb-2" aria-hidden="true"></i>' +
          '<p class="text-muted small mb-1">' + escapeHtml(messages.noDevicesYet || "") + '</p>' +
          '<p class="small mb-0">' + escapeHtml(messages.createFirstDevice || "") + '</p>' +
        '</div>';
      return;
    }

    var html = "";

    if (exceeds) {
      html +=
        '<div class="alert alert-warning small py-2 mb-3" role="alert">' +
          '<div class="fw-semibold">' + escapeHtml(messages.deviceLimitExceededWarning || "") + '</div>' +
          '<div class="mt-1">' + escapeHtml(messages.deactivateExtraDevices || "") + '</div>' +
        '</div>';
    }

    html +=
      '<div class="border rounded p-2 mb-3 bg-body-tertiary">' +
        '<div class="small fw-semibold mb-2">' + escapeHtml(messages.overallConnectionStatus || "") + '</div>' +
        '<div class="row g-2 small">' +
          '<div class="col-6 col-md-3">' +
            '<div class="text-muted">' + escapeHtml(formatMsg(messages.activeDeviceCount, activeCount)) + '</div>' +
          '</div>' +
          '<div class="col-6 col-md-3">' +
            '<div class="text-muted">' + escapeHtml(formatMsg(messages.allowedDeviceCountLabel, allowedCount)) + '</div>' +
          '</div>' +
          '<div class="col-6 col-md-3">' +
            '<div class="text-muted">' + escapeHtml(formatMsg(messages.connectedDeviceCount, connectedCount)) + '</div>' +
          '</div>' +
          '<div class="col-6 col-md-3">' +
            '<div class="text-muted">' + escapeHtml(formatMsg(messages.overviewLastSeen, formatLastSeen(latestLastSeen))) + '</div>' +
          '</div>' +
        '</div>' +
      '</div>';

    html += '<div class="small text-muted text-uppercase mb-2">' + escapeHtml(messages.overviewDevicesTitle || "") + '</div>';

    html += '<ul class="list-group list-group-flush small mb-0">';
    devices.forEach(function (device) {
      var status = deviceStatusBadge(device);
      html +=
        '<li class="list-group-item px-0 py-2 border-0 border-bottom">' +
          '<div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-1">' +
            '<span class="fw-semibold">' + escapeHtml(device.name) + '</span>' +
            '<span class="badge ' + status.cls + '">' + escapeHtml(status.text) + '</span>' +
          '</div>' +
          '<div class="text-muted">' +
            escapeHtml(messages.lastSeen || "Last seen") + ': ' + escapeHtml(formatLastSeen(device.lastSeenAtUtc)) +
            ' · ' + escapeHtml(messages.machineName || "Machine") + ': ' + escapeHtml(displayValue(device.machineName)) +
            ' · ' + escapeHtml(messages.printerName || "Printer") + ': ' + escapeHtml(displayValue(device.printerName)) +
            ' · ' + escapeHtml(messages.appVersion || "Version") + ': ' + escapeHtml(displayValue(device.appVersion)) +
          '</div>' +
        '</li>';
    });
    html += '</ul>';

    body.innerHTML = html;
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
          '<td class="text-muted small">' + escapeHtml(messages.tokenNotAvailableRegenerate || "") + '</td>' +
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
            '<th>' + escapeHtml(messages.tokenColumn || "Token") + '</th>' +
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
    lastQuota = quota;
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
          showToken(data.token, {
            mode: data.tokenMode || "create",
            title: data.tokenTitle,
            notice: data.tokenNotice
          });
          if (nameInput) nameInput.value = "";
          showMessage(data.message || messages.deviceCreatedSuccessfully, "success");
          return applyMutationResponse(data);
        })
        .catch(function (e) {
          var text = (e && e.message) || messages.tokenGenerateFailed || "Failed";
          showMessage(text, "danger");
          return refreshDevices();
        })
        .finally(function () {
          if (lastQuota) updateQuotaUi(lastQuota);
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

        var confirmText = formatMsg(messages.confirmRegenerateToken || "Regenerate token for {0}?", deviceName);
        if (messages.tokenRegenerateWarning) {
          confirmText += "\n\n" + messages.tokenRegenerateWarning;
        }
        if (!window.confirm(confirmText)) return;

        btn.disabled = true;
        var url = (cfg.regenerateTokenUrlTemplate || "").replace("{id}", deviceId);

        postForm(url, {})
          .then(function (data) {
            showToken(data.token, {
              mode: data.tokenMode || "regenerate",
              title: data.tokenTitle,
              notice: data.tokenNotice,
              warning: data.tokenWarning
            });
            if (data.message) showMessage(data.message, "success");
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

  function bindTokenActions() {
    var copyTokenBtn = document.getElementById("printBridgeCopyTokenBtn");
    if (copyTokenBtn) {
      copyTokenBtn.addEventListener("click", function () {
        if (!currentToken) {
          showMessage(messages.tokenMasked || messages.tokenNotAvailableRegenerate, "danger");
          return;
        }
        copyText(currentToken, copyTokenBtn, { successMessage: messages.tokenCopied })
          .catch(function () {
            showMessage(messages.tokenCopyFailed, "danger");
          });
      });
    }

    var dismissTokenBtn = document.getElementById("printBridgeDismissTokenBtn");
    if (dismissTokenBtn) {
      dismissTokenBtn.addEventListener("click", function () {
        hideToken();
      });
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    hideToken();
    if (cfg.initialState) {
      renderAll(cfg.initialState);
    }
    bindCreateDevice();
    bindTokenActions();
  });
})();
