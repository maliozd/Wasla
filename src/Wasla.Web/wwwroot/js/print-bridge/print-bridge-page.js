// Print Bridge management page: devices, tokens, receipt settings, copy helpers.
(function () {
  "use strict";

  var cfg = window.OrderHubPrintBridge;
  if (!cfg) return;

  var messages = cfg.messages || {};
  var currentToken = null;
  var lastQuota = null;
  var reprintInFlight = {};
  var printJobsRefreshInFlight = false;
  var printJobsPollTimer = null;

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
    if (token) {
      body.set("__RequestVerificationToken", token);
    }
    Object.keys(fields || {}).forEach(function (key) {
      body.set(key, fields[key]);
    });

    return fetch(url, { method: "POST", headers: headers, body: body }).then(function (resp) {
      if (!resp.ok) {
        return resp.json().catch(function () { return {}; }).then(function (err) {
          var message = (err && err.message) || messages.reprintFailed || "request failed";
          throw new Error(message);
        });
      }
      return resp.json();
    });
  }

  function formatOptionalTime(iso) {
    if (!iso) return messages.emptyValue || "—";
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return messages.emptyValue || "—";
      return d.toLocaleString();
    } catch (_) {
      return messages.emptyValue || "—";
    }
  }

  function formatLastSeen(iso) {
    if (!iso) return messages.lastSeenNever || messages.emptyValue || "—";
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return messages.lastSeenNever || messages.emptyValue || "—";
      return d.toLocaleString();
    } catch (_) {
      return messages.lastSeenNever || messages.emptyValue || "—";
    }
  }

  function displayValue(value) {
    if (value == null || String(value).trim() === "") return messages.emptyValue || "—";
    return String(value);
  }

  function deviceStatusBadge(device) {
    if (!device.isActive) {
      return { cls: "text-bg-secondary", text: messages.statusUnknown || "Unknown" };
    }

    var status = device.connectionStatus || (device.isConnected ? "Connected" : "Disconnected");
    switch (status) {
      case "Connected":
        return { cls: "text-bg-success", text: messages.statusOnline || "Online" };
      case "RecentlySeen":
        return { cls: "text-bg-warning", text: messages.statusOffline || "Offline" };
      case "NeverConnected":
        return { cls: "text-bg-warning", text: messages.statusOffline || "Offline" };
      case "Disconnected":
        return { cls: "text-bg-warning", text: messages.statusOffline || "Offline" };
      case "Inactive":
        return { cls: "text-bg-secondary", text: messages.statusInactive || messages.statusUnknown || "Unknown" };
      default:
        return { cls: "text-bg-secondary", text: messages.statusUnknown || "Unknown" };
    }
  }

  function formatVersion(value) {
    if (value == null || String(value).trim() === "") {
      return messages.versionUnknown || messages.emptyValue || "Unknown";
    }
    return String(value);
  }

  function updateSummaryCards(devices, quota) {
    devices = devices || [];
    quota = quota || {};

    var activeCount = quota.activeDeviceCount != null ? quota.activeDeviceCount : 0;
    var allowedCount = quota.allowedActiveDeviceCount != null ? quota.allowedActiveDeviceCount : 1;
    var latestLastSeen = quota.latestLastSeenAtUtc || null;
    var lastName = quota.lastConnectedDeviceName || null;

    if (!lastName && devices.length > 0) {
      devices.forEach(function (d) {
        if (!d.lastSeenAtUtc) return;
        if (!latestLastSeen || new Date(d.lastSeenAtUtc) > new Date(latestLastSeen)) {
          latestLastSeen = d.lastSeenAtUtc;
          lastName = d.name;
        }
      });
    }

    var activeEl = document.getElementById("printBridgeSummaryActive");
    var includedEl = document.getElementById("printBridgeSummaryIncluded");
    var lastDeviceEl = document.getElementById("printBridgeSummaryLastDevice");
    var lastSeenEl = document.getElementById("printBridgeSummaryLastSeen");

    if (activeEl) activeEl.textContent = String(activeCount);
    if (includedEl) includedEl.textContent = String(allowedCount);
    if (lastDeviceEl) {
      lastDeviceEl.textContent = lastName || messages.lastConnectedNone || messages.emptyValue || "—";
    }
    if (lastSeenEl) {
      lastSeenEl.textContent = (messages.lastSeen || "Last seen") + ": " + formatLastSeen(latestLastSeen);
    }
  }

  function renderDevicesTable(devices) {
    var panel = document.getElementById("printBridgeDevicesPanel");
    if (!panel) return;

    devices = devices || [];
    if (devices.length === 0) {
      panel.innerHTML =
        '<div class="oh-print-bridge-empty text-center py-5">' +
          '<div class="oh-print-bridge-empty__icon text-muted mb-2" aria-hidden="true"><i class="bi bi-hdd-network fs-3"></i></div>' +
          '<div class="fw-semibold mb-1">' + escapeHtml(messages.noDevicesTitle || "") + '</div>' +
          '<p class="text-muted small mb-3">' + escapeHtml(messages.noDevicesDescription || "") + '</p>' +
          '<div class="d-flex flex-wrap justify-content-center gap-2">' +
            '<a class="btn btn-primary btn-sm" href="' + escapeHtml(cfg.setupUrl || "/print-bridge/setup") + '">' +
              '<i class="bi bi-plus-circle me-1"></i>' + escapeHtml(messages.setupNewDevice || messages.goToSetup || "Set up a new device") +
            '</a>' +
          '</div>' +
        '</div>';
      return;
    }

    var rows = devices.map(function (device) {
      var connection = deviceStatusBadge(device);
      var activeBadgeCls = device.isActive ? "text-bg-success" : "text-bg-secondary";
      var activeText = device.isActive ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
      var printerHint = displayValue(device.printerName);
      var machineHint = displayValue(device.machineName);
      var detailsUrl = device.detailsUrl || ("/print-bridge/devices/" + encodeURIComponent(device.id));
      return (
        '<tr data-device-id="' + escapeHtml(device.id) + '">' +
          '<td>' +
            '<div class="fw-semibold">' + escapeHtml(device.name) + '</div>' +
            (machineHint !== (messages.emptyValue || "—")
              ? '<div class="text-muted small">' + escapeHtml(messages.machineName || "Computer") + ': ' + escapeHtml(machineHint) + '</div>'
              : '') +
            (printerHint !== (messages.emptyValue || "—")
              ? '<div class="text-muted small">' + escapeHtml(messages.printerName || "Printer") + ': ' + escapeHtml(printerHint) + '</div>'
              : '') +
          '</td>' +
          '<td class="text-muted small">' + escapeHtml(displayValue(device.localAlias)) + '</td>' +
          '<td>' +
            '<div class="d-flex flex-wrap align-items-center gap-1">' +
              '<span class="badge ' + connection.cls + '">' + escapeHtml(connection.text) + '</span>' +
              '<span class="badge ' + activeBadgeCls + ' pb-device-active-badge">' + escapeHtml(activeText) + '</span>' +
            '</div>' +
          '</td>' +
          '<td class="text-muted small text-nowrap">' + escapeHtml(formatLastSeen(device.lastSeenAtUtc)) + '</td>' +
          '<td class="text-muted small">' + escapeHtml(formatVersion(device.appVersion)) + '</td>' +
          '<td>' +
            '<div class="d-flex flex-wrap align-items-center gap-2">' +
              '<div class="form-check form-switch m-0">' +
                '<input class="form-check-input pb-device-active-toggle" type="checkbox" role="switch" ' +
                  'data-device-id="' + escapeHtml(device.id) + '" ' +
                  'aria-label="' + escapeHtml(messages.deviceActive || "Active") + '" ' +
                  (device.isActive ? "checked" : "") + ' />' +
              '</div>' +
              '<a class="btn btn-sm btn-outline-primary" href="' + escapeHtml(detailsUrl) + '">' +
                '<i class="bi bi-info-circle me-1"></i>' + escapeHtml(messages.details || "Details") +
              '</a>' +
              '<button type="button" class="btn btn-sm btn-outline-secondary pb-regenerate-token-btn" ' +
                'data-device-id="' + escapeHtml(device.id) + '" data-device-name="' + escapeHtml(device.name) + '">' +
                '<i class="bi bi-arrow-repeat me-1"></i>' + escapeHtml(messages.regenerateToken || "Regenerate") +
              '</button>' +
            '</div>' +
          '</td>' +
        '</tr>'
      );
    }).join("");

    panel.innerHTML =
      '<div class="table-responsive">' +
        '<table class="table table-sm align-middle mb-0 oh-print-bridge-devices-table">' +
          '<thead><tr>' +
            '<th>' + escapeHtml(messages.deviceName || "Device") + '</th>' +
            '<th>' + escapeHtml(messages.localAlias || "Local alias") + '</th>' +
            '<th>' + escapeHtml(messages.status || "Status") + '</th>' +
            '<th>' + escapeHtml(messages.lastSeen || "Last seen") + '</th>' +
            '<th>' + escapeHtml(messages.appVersion || "Version") + '</th>' +
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

    var exceededAlert = document.getElementById("printBridgeLimitExceededAlert");
    var reachedAlert = document.getElementById("printBridgeLimitReachedAlert");

    if (exceededAlert) {
      exceededAlert.classList.toggle("d-none", !exceeds);
    }

    if (reachedAlert) {
      reachedAlert.classList.toggle("d-none", canCreate || exceeds);
    }
  }

  function renderAll(state) {
    if (!state) return;
    updateSummaryCards(state.devices, state.quota);
    renderDevicesTable(state.devices);
    updateQuotaUi(state.quota);
  }

  function refreshDevices() {
    return fetch(cfg.devicesUrl || "/print-bridge/devices/list", {
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

  function refreshPrintJobsPartial() {
    var panel = document.getElementById("printBridgeJobsPanel");
    if (!panel || printJobsRefreshInFlight) {
      return Promise.resolve();
    }

    printJobsRefreshInFlight = true;
    return fetch(cfg.printJobsUrl || "/print-bridge/print-jobs", {
      headers: { "X-Requested-With": "XMLHttpRequest" },
      credentials: "same-origin"
    })
      .then(function (resp) {
        if (!resp.ok) throw new Error("HTTP " + resp.status);
        return resp.text();
      })
      .then(function (html) {
        panel.innerHTML = html;
      })
      .finally(function () {
        printJobsRefreshInFlight = false;
      });
  }

  function startPrintJobsPolling() {
    var intervalMs = cfg.printJobsPollIntervalMs || 3000;
    if (printJobsPollTimer) {
      clearInterval(printJobsPollTimer);
    }

    printJobsPollTimer = setInterval(function () {
      refreshPrintJobsPartial().catch(function () {
        // Silent retry on next interval.
      });
    }, intervalMs);
  }

  function bindPrintJobReprintDelegation() {
    var panel = document.getElementById("printBridgeJobsPanel");
    if (!panel || panel.getAttribute("data-reprint-bound") === "1") return;
    panel.setAttribute("data-reprint-bound", "1");

    panel.addEventListener("click", function (event) {
      var btn = event.target.closest(".pb-reprint-job-btn");
      if (!btn || !panel.contains(btn)) return;

      var jobId = btn.getAttribute("data-job-id");
      var orderDisplay = btn.getAttribute("data-order-display") || "";
      if (!jobId || reprintInFlight[jobId]) return;

      var confirmText = formatMsg(messages.confirmReprint || "Reprint receipt for {0}?", orderDisplay);
      if (!window.confirm(confirmText)) return;

      reprintInFlight[jobId] = true;
      btn.disabled = true;

      var url = (cfg.reprintUrlTemplate || "").replace("{id}", jobId);
      postForm(url, {})
        .then(function (data) {
          showMessage(data.message || messages.reprintCreated, "success");
          return refreshPrintJobsPartial();
        })
        .catch(function (e) {
          var text = (e && e.message) || messages.reprintFailed || "Reprint failed";
          showMessage(text, "danger");
        })
        .finally(function () {
          delete reprintInFlight[jobId];
          btn.disabled = false;
        });
    });
  }

  function bindRefreshPrintJobs() {
    var btn = document.getElementById("printBridgeRefreshJobsBtn");
    if (!btn) return;

    btn.addEventListener("click", function () {
      if (printJobsRefreshInFlight) return;
      btn.disabled = true;
      refreshPrintJobsPartial()
        .finally(function () {
          btn.disabled = false;
        });
    });
  }

  function bindServerUrlCopy() {
    var btn = document.getElementById("printBridgeCopyServerUrlBtn");
    var valueEl = document.getElementById("printBridgeServerUrlValue");
    if (!btn || !valueEl) return;

    btn.addEventListener("click", function () {
      var text = (valueEl.textContent || "").trim();
      if (!text) {
        text = (cfg.serverUrl || "").trim();
      }
      if (!text) {
        showMessage(messages.copyFailed || "Copy failed", "danger");
        return;
      }
      copyText(text, btn, { successMessage: messages.copied || messages.tokenCopied })
        .catch(function () {
          showMessage(messages.copyFailed || messages.tokenCopyFailed, "danger");
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
    bindTokenActions();
    bindPrintJobReprintDelegation();
    bindRefreshPrintJobs();
    startPrintJobsPolling();
  });
})();
