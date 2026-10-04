// Print Bridge management page: devices, tokens, receipt settings, copy helpers.
(function () {
  "use strict";

  var cfg = window.WaslaPrintBridge;
  if (!cfg) return;

  var messages = cfg.messages || {};
  var lastQuota = null;
  var reprintInFlight = {};
  var printJobsRefreshInFlight = false;
  var printJobsPollTimer = null;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatMessage(template, value) {
    return String(template || "").replace(/\{0\}/g, value == null ? "" : String(value));
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

  // --- Times: UTC instants from the server, shown in the restaurant's time zone and the UI culture ---

  /** Only ISO times that state they are UTC ("Z") or carry an offset are trusted; anything else counts as missing. */
  function parseUtc(value) {
    if (typeof value !== "string" || !/(?:Z|[+-]\d{2}:\d{2})$/.test(value)) return null;
    var ms = Date.parse(value);
    return isNaN(ms) ? null : ms;
  }

  var dateTimeFormat = null;

  function displayCulture() {
    return cfg.displayCulture || (document.documentElement && document.documentElement.lang) || "tr-TR";
  }

  /** The same Intl approach as the Live Screen: the UI culture, in the configured restaurant time zone. */
  function formatInstant(ms) {
    if (!dateTimeFormat) {
      var options = { timeZone: cfg.timeZoneId || "Europe/Istanbul", dateStyle: "short", timeStyle: "medium" };
      try {
        dateTimeFormat = new Intl.DateTimeFormat(displayCulture(), options);
      } catch (_) {
        try {
          dateTimeFormat = new Intl.DateTimeFormat("tr-TR", options);
        } catch (__) {
          // No time-zone data at all: an unambiguous UTC value rather than the browser's own clock.
          dateTimeFormat = { format: function (date) { return date.toISOString(); } };
        }
      }
    }
    return dateTimeFormat.format(new Date(ms));
  }

  function formatLastSeen(value) {
    var ms = parseUtc(value);
    return ms === null ? (messages.lastSeenNever || messages.emptyValue || "—") : formatInstant(ms);
  }

  function displayValue(value) {
    if (value == null || String(value).trim() === "") return messages.emptyValue || "—";
    return String(value);
  }

  // The list shows "Online" only for Connected; RecentlySeen, Disconnected and NeverConnected all read "Offline".
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
      var latestMs = parseUtc(latestLastSeen);
      devices.forEach(function (d) {
        var seenMs = parseUtc(d.lastSeenAtUtc);
        if (seenMs === null) return;
        if (latestMs === null || seenMs > latestMs) {
          latestMs = seenMs;
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

  var lastDevicesHtml = null;

  /** The device control that has keyboard focus inside the table, so a re-render can give it back. */
  function focusedDeviceControl(panel) {
    var active = document.activeElement;
    if (!active || typeof panel.contains !== "function" || !panel.contains(active) || typeof active.closest !== "function") return null;
    var row = active.closest("tr[data-device-id]");
    if (!row) return null;
    return {
      id: row.getAttribute("data-device-id"),
      toggle: !!(active.classList && active.classList.contains("pb-device-active-toggle"))
    };
  }

  function restoreFocus(panel, key) {
    if (!key || typeof panel.querySelectorAll !== "function") return;
    var rows = panel.querySelectorAll("tr[data-device-id]");
    for (var i = 0; i < rows.length; i++) {
      if (rows[i].getAttribute("data-device-id") !== key.id) continue;
      var target = rows[i].querySelector(key.toggle ? ".pb-device-active-toggle" : "a");
      if (target && typeof target.focus === "function") target.focus();
      return;
    }
  }

  function renderDevicesTable(devices) {
    var panel = document.getElementById("printBridgeDevicesPanel");
    if (!panel) return;

    devices = devices || [];
    var html;
    if (devices.length === 0) {
      html =
        '<div class="wasla-print-bridge-empty text-center py-5">' +
          '<div class="wasla-print-bridge-empty__icon text-muted mb-2" aria-hidden="true"><i class="bi bi-hdd-network fs-3"></i></div>' +
          '<div class="fw-semibold mb-1">' + escapeHtml(messages.noDevicesTitle || "") + '</div>' +
          '<p class="text-muted small mb-3">' + escapeHtml(messages.noDevicesDescription || "") + '</p>' +
          '<div class="d-flex flex-wrap justify-content-center gap-2">' +
            '<a class="btn btn-primary btn-sm" href="' + escapeHtml(cfg.setupUrl || "/print-bridge/setup") + '">' +
              '<i class="bi bi-plus-circle me-1"></i>' + escapeHtml(messages.setupNewDevice || messages.goToSetup || "Set up a new device") +
            '</a>' +
          '</div>' +
        '</div>';
    } else {
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
            '<td>' +
              '<div class="d-flex flex-wrap align-items-center gap-1">' +
                '<span class="badge ' + connection.cls + '">' + escapeHtml(connection.text) + '</span>' +
                '<span class="badge ' + activeBadgeCls + ' pb-device-active-badge">' + escapeHtml(activeText) + '</span>' +
              '</div>' +
            '</td>' +
            '<td class="text-muted small text-nowrap">' + escapeHtml(formatLastSeen(device.lastSeenAtUtc)) + '</td>' +
            '<td class="text-muted small">' + escapeHtml(formatVersion(device.appVersion)) + '</td>' +
            '<td>' +
              '<div class="d-flex align-items-center justify-content-end gap-2 text-nowrap">' +
                '<div class="form-check form-switch m-0">' +
                  '<input class="form-check-input pb-device-active-toggle" type="checkbox" role="switch" ' +
                    'data-device-id="' + escapeHtml(device.id) + '" ' +
                    'aria-label="' + escapeHtml(messages.deviceActive || "Active") + '" ' +
                    (device.isActive ? "checked" : "") + ' />' +
                '</div>' +
                '<a class="btn btn-sm btn-outline-primary" href="' + escapeHtml(detailsUrl) + '">' +
                  '<i class="bi bi-info-circle me-1"></i>' + escapeHtml(messages.details || "Details") +
                '</a>' +
              '</div>' +
            '</td>' +
          '</tr>'
        );
      }).join("");

      html =
        '<div class="table-responsive">' +
          '<table class="table table-sm align-middle mb-0 wasla-print-bridge-devices-table">' +
            '<thead><tr>' +
              '<th>' + escapeHtml(messages.deviceName || "Device") + '</th>' +
              '<th>' + escapeHtml(messages.status || "Status") + '</th>' +
              '<th>' + escapeHtml(messages.lastSeen || "Last seen") + '</th>' +
              '<th>' + escapeHtml(messages.appVersion || "Version") + '</th>' +
              '<th class="text-end">' + escapeHtml(messages.actions || "Actions") + '</th>' +
            '</tr></thead>' +
            '<tbody id="printBridgeDevicesTableBody">' + rows + '</tbody>' +
          '</table>' +
        '</div>';
    }

    // Nothing changed: leave the table (and whatever has keyboard focus in it) alone.
    if (html === lastDevicesHtml) return;

    var focusKey = focusedDeviceControl(panel);
    panel.innerHTML = html;
    lastDevicesHtml = html;
    bindActiveToggles();
    restoreFocus(panel, focusKey);
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

  // --- Device snapshot: the one source of device state on this page ---
  //
  // Every update goes through applySnapshot: the initial state, the periodic refresh, the page becoming visible again,
  // device toggles and presence expiry. A future push channel (e.g. SignalR) only has to call
  // window.WaslaPrintBridge.refreshDevices(). Snapshots carry the server's time, so an older one never replaces a newer
  // one, and that time also corrects this browser's clock. Refreshing only reads: it never creates a device, token,
  // setup session or print job.

  var deviceState = null;
  var appliedServerMs = null;
  var serverOffsetMs = 0;
  var connectedMs = null;
  var recentlySeenMs = null;
  var expiryTimer = null;
  var devicesPollTimer = null;
  var devicesRefreshInFlight = null;
  var devicesRefreshAgain = false;
  var devicesAbort = null;
  var pendingMutations = 0;
  var refreshAfterMutation = false;
  var devicesStopped = false;

  /** Applies a server snapshot unless a newer one is already on screen. Returns whether it was applied. */
  function applySnapshot(data) {
    if (!data || !Array.isArray(data.devices)) return false;
    var serverMs = parseUtc(data.serverTimeUtc);
    if (appliedServerMs !== null && (serverMs === null || serverMs < appliedServerMs)) return false;

    if (serverMs !== null) {
      appliedServerMs = serverMs;
      serverOffsetMs = serverMs - Date.now();
    }
    if (data.connectedThresholdSeconds > 0) connectedMs = data.connectedThresholdSeconds * 1000;
    if (data.recentlySeenThresholdSeconds > 0) recentlySeenMs = data.recentlySeenThresholdSeconds * 1000;

    deviceState = {
      devices: data.devices.map(function (d) { return Object.assign({}, d); }),
      quota: Object.assign({}, data.quota || {})
    };
    renderAll(deviceState);
    schedulePresenceExpiry();
    return true;
  }

  function serverNow() {
    return Date.now() + serverOffsetMs;
  }

  /**
   * A Bridge that crashes, loses power or loses the internet sends nothing, so "connected" must also end by time. The
   * server's own threshold and clock decide when; the page then asks the server to confirm.
   */
  function schedulePresenceExpiry() {
    if (expiryTimer) {
      clearTimeout(expiryTimer);
      expiryTimer = null;
    }
    if (devicesStopped || !deviceState || connectedMs === null) return;

    var next = null;
    var now = serverNow();
    deviceState.devices.forEach(function (d) {
      if (d.connectionStatus !== "Connected") return;
      var seen = parseUtc(d.lastSeenAtUtc);
      if (seen === null) return;
      var due = seen + connectedMs - now;
      if (next === null || due < next) next = due;
    });
    if (next === null) return;

    // "Connected" holds up to and including the threshold, so check just after it.
    expiryTimer = setTimeout(expirePresence, Math.max(0, next) + 250);
  }

  function expirePresence() {
    expiryTimer = null;
    if (devicesStopped || !deviceState || connectedMs === null) return;

    var now = serverNow();
    var changed = false;
    deviceState.devices.forEach(function (d) {
      if (d.connectionStatus !== "Connected") return;
      var seen = parseUtc(d.lastSeenAtUtc);
      if (seen === null || now - seen <= connectedMs) return;
      // The server calculator's next states, with its own thresholds.
      d.connectionStatus = recentlySeenMs !== null && now - seen <= recentlySeenMs ? "RecentlySeen" : "Disconnected";
      d.isConnected = false;
      changed = true;
    });

    if (changed) {
      deviceState.quota.connectedDeviceCount = deviceState.devices.filter(function (d) {
        return d.connectionStatus === "Connected";
      }).length;
      renderAll(deviceState);
      requestDevicesRefresh();
    }
    schedulePresenceExpiry();
  }

  /**
   * The one device refresh. Only one request runs at a time; triggers that arrive meanwhile are folded into a single
   * follow-up. A failed request keeps what is on screen, and the next trigger recovers.
   */
  function requestDevicesRefresh() {
    if (devicesStopped) return Promise.resolve(false);
    if (devicesRefreshInFlight) {
      devicesRefreshAgain = true;
      return devicesRefreshInFlight;
    }

    devicesAbort = typeof AbortController === "function" ? new AbortController() : null;
    devicesRefreshInFlight = fetch(cfg.devicesUrl || "/print-bridge/devices/list", {
      headers: { "X-Requested-With": "XMLHttpRequest", "Accept": "application/json" },
      credentials: "same-origin",
      cache: "no-store",
      signal: devicesAbort ? devicesAbort.signal : undefined
    })
      .then(function (resp) {
        if (!resp.ok) throw new Error("HTTP " + resp.status);
        return resp.json();
      })
      .then(function (data) {
        if (devicesStopped) return false;
        // A toggle is on its way and its own response is newer; refresh again once it has landed.
        if (pendingMutations > 0) {
          refreshAfterMutation = true;
          return false;
        }
        return applySnapshot(data);
      })
      .catch(function () {
        return false;
      })
      .then(function (applied) {
        devicesRefreshInFlight = null;
        devicesAbort = null;
        if (devicesRefreshAgain && !devicesStopped) {
          devicesRefreshAgain = false;
          requestDevicesRefresh();
        }
        return applied;
      });
    return devicesRefreshInFlight;
  }

  function startDevicesPolling() {
    if (devicesPollTimer) clearInterval(devicesPollTimer);
    devicesPollTimer = setInterval(function () {
      // A hidden tab skips its turn; it refreshes as soon as it is shown again.
      if (document.visibilityState === "hidden") return;
      requestDevicesRefresh();
    }, cfg.devicesPollIntervalMs || 15000);
  }

  function stopDevicesRefresh() {
    devicesStopped = true;
    if (devicesPollTimer) {
      clearInterval(devicesPollTimer);
      devicesPollTimer = null;
    }
    if (expiryTimer) {
      clearTimeout(expiryTimer);
      expiryTimer = null;
    }
    if (devicesAbort) devicesAbort.abort();
  }

  function bindDevicesRefresh() {
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") requestDevicesRefresh();
    });
    window.addEventListener("pagehide", stopDevicesRefresh);
    window.addEventListener("pageshow", function (event) {
      if (!event || !event.persisted) return;
      devicesStopped = false;
      startDevicesPolling();
      requestDevicesRefresh();
    });
    startDevicesPolling();
  }

  function applyMutationResponse(data) {
    if (data && Array.isArray(data.devices)) {
      applySnapshot(data);
      return Promise.resolve(data);
    }
    return requestDevicesRefresh();
  }

  function finishMutation() {
    pendingMutations = Math.max(0, pendingMutations - 1);
    if (pendingMutations === 0 && refreshAfterMutation) {
      refreshAfterMutation = false;
      requestDevicesRefresh();
    }
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

        pendingMutations++;
        var url = (cfg.setActiveUrlTemplate || "").replace("{id}", deviceId);
        postForm(url, { isActive: isActive ? "true" : "false" })
          .then(function (data) {
            finishMutation();
            return applyMutationResponse(data);
          })
          .catch(function (e) {
            finishMutation();
            toggle.checked = previous;
            if (badge) {
              badge.textContent = previous ? (messages.ordersActive || "Active") : (messages.ordersPassive || "Passive");
              badge.classList.remove("text-bg-success", "text-bg-secondary");
              badge.classList.add(previous ? "text-bg-success" : "text-bg-secondary");
            }
            var text = (e && e.message) || messages.deviceUpdateFailed || "Update failed";
            showMessage(text, "danger");
            return requestDevicesRefresh();
          });
      });
    });
  }

  // A future push channel calls this; it is the same refresh the timer, the page and the toggles use.
  cfg.refreshDevices = requestDevicesRefresh;

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

      var confirmText = formatMessage(messages.confirmReprint || "Reprint receipt for {0}?", orderDisplay);
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

  document.addEventListener("DOMContentLoaded", function () {
    if (cfg.initialState) {
      applySnapshot(cfg.initialState);
    }
    bindDevicesRefresh();
    bindPrintJobReprintDelegation();
    bindRefreshPrintJobs();
    startPrintJobsPolling();
  });
})();
