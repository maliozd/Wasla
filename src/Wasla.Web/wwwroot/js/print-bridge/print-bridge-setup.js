// Print Bridge setup page: automatic connection (open the app from the browser) and the manual connection with
// the Wasla Web Panel URL and a device token, the two values the app's Settings screen asks for.
(function () {
    "use strict";

    function readConfig() {
        var el = document.getElementById("printBridgeSetupConfig");
        if (!el) return {};
        try {
            return JSON.parse(el.textContent || "{}");
        } catch {
            return {};
        }
    }

    var cfg = readConfig();
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

    function postForm(url, fields) {
        var token = antiForgeryToken();
        var headers = { "X-Requested-With": "fetch" };
        if (token) headers["RequestVerificationToken"] = token;

        var body = new URLSearchParams();
        if (token) body.set("__RequestVerificationToken", token);
        Object.keys(fields || {}).forEach(function (key) {
            body.set(key, fields[key]);
        });

        return fetch(url, { method: "POST", headers: headers, body: body, credentials: "same-origin" })
            .then(function (resp) {
                return resp.json().catch(function () { return {}; }).then(function (json) {
                    if (!resp.ok) {
                        var err = new Error((json && json.message) || messages.manualTokenFailed || "Request failed");
                        err.response = json;
                        err.status = resp.status;
                        throw err;
                    }
                    return json;
                });
            });
    }

    /** One polite announcement per copy; the button itself shows the visible feedback. */
    function announceCopy(text) {
        var status = document.getElementById("pbManualCopyStatus");
        if (status) status.textContent = text || "";
    }

    function copyText(text, button, successMessage, announcement) {
        if (!text) return Promise.reject(new Error("empty"));

        function onSuccess() {
            announceCopy(announcement);
            if (!button) return;
            var original = button.innerHTML;
            var originalClass = button.className;
            button.innerHTML = '<i class="bi bi-check2 me-1" aria-hidden="true"></i>' + escapeHtml(successMessage || messages.copied || "Copied");
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
        var serverInput = document.getElementById("pbSetupServerUrl");
        var copyServerBtn = document.getElementById("pbSetupCopyServerUrlBtn");
        if (copyServerBtn && serverInput) {
            copyServerBtn.addEventListener("click", function () {
                copyText(serverInput.value, copyServerBtn, messages.copied, messages.webPanelUrlCopied).catch(function () {
                    showMessage(messages.copyFailed || "Copy failed", "danger");
                });
            });
        }

        var tokenInput = document.getElementById("pbManualTokenValue");
        var copyTokenBtn = document.getElementById("pbManualCopyTokenBtn");
        if (copyTokenBtn && tokenInput) {
            copyTokenBtn.addEventListener("click", function () {
                copyText(tokenInput.value, copyTokenBtn, messages.copied, messages.deviceTokenCopied).catch(function () {
                    showMessage(messages.copyFailed || "Copy failed", "danger");
                });
            });
        }
    }

    // --- Automatic setup (browser-to-application) ---

    var pollTimer = null;
    var fallbackTimer = null;

    function antiForgeryToken() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function setStatus(text) {
        var box = document.getElementById("pbAutoStatus");
        var label = document.getElementById("pbAutoStatusText");
        if (label) label.textContent = text || "";
        if (box) box.classList.remove("d-none");
    }

    function stopSpinner() {
        var spinner = document.getElementById("pbAutoSpinner");
        if (spinner) spinner.classList.add("d-none");
    }

    function showFallback() {
        var fb = document.getElementById("pbAutoFallback");
        if (fb) fb.classList.remove("d-none");
        // In the guided first install the manual connection opens only when the user chooses it (its own button, or
        // "Connect manually" in this fallback); the ordinary page keeps opening it here.
        if (!cfg.guidedFirstInstall) expandManualSetupSection(false);
    }

    function clearTimers() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
        if (fallbackTimer) { clearTimeout(fallbackTimer); fallbackTimer = null; }
    }

    function finish(text, type) {
        clearTimers();
        stopSpinner();
        setStatus(text);
        if (type) showMessage(text, type);
    }

    /**
     * The canonical "Print Bridge setup completed" notification of the automatic flow. Sent only for a verified
     * server success; a guided-setup panel then re-reads the device's readiness from the server (the same fact
     * as the setup checklist), never from this message.
     */
    function notifySetupCompleted(data) {
        if (!data || data.status !== "Completed" || !data.connectionVerified) return;
        document.dispatchEvent(new CustomEvent("wasla:print-bridge-setup-completed", {
            detail: { connectionVerified: true }
        }));
    }

    function pollStatus(statusUrl) {
        pollTimer = setInterval(function () {
            fetch(statusUrl, { headers: { "Accept": "application/json" }, credentials: "same-origin" })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (!data || !data.success) return;
                    switch (data.status) {
                        case "Completed":
                            finish(data.connectionVerified ? messages.connected : messages.savedUnverified,
                                data.connectionVerified ? "success" : "info");
                            notifySetupCompleted(data);
                            break;
                        case "Failed":
                            finish(data.message || messages.failed, "danger");
                            showFallback();
                            break;
                        case "Expired":
                            finish(messages.expired, "danger");
                            showFallback();
                            break;
                    }
                })
                .catch(function () { /* keep polling */ });
        }, 2000);
    }

    function getSelectedSetupMode() {
        var checked = document.querySelector('input[name="pbSetupMode"]:checked');
        return checked ? checked.value : "new";
    }

    function getSelectedReconnectDeviceId() {
        var select = document.getElementById("pbReconnectDeviceSelect");
        return select ? select.value : "";
    }

    function validateReconnectSelection() {
        if (getSelectedSetupMode() !== "reconnect") return true;
        if (getSelectedReconnectDeviceId()) return true;
        updatePrimaryCtaState();
        var guidance = document.getElementById("pbReconnectDeviceGuidance");
        if (guidance) guidance.scrollIntoView({ behavior: "smooth", block: "nearest" });
        return false;
    }

    function shouldConfirmAutomaticTokenReplacement() {
        if (getSelectedSetupMode() !== "reconnect") return true;
        return window.confirm(messages.reconnectConfirm || messages.reconnectTokenWarning || messages.autoReplaceTokenConfirm || "Continue with reconnect?");
    }

    function startAutomaticSetup() {
        if (!validateReconnectSelection()) return;
        if (!shouldConfirmAutomaticTokenReplacement()) return;

        clearTimers();
        stopSpinnerReset();
        setStatus(messages.starting || "Opening…");

        var form = new FormData();
        form.append("__RequestVerificationToken", antiForgeryToken());
        var mode = getSelectedSetupMode();
        form.append("setupMode", mode);
        if (mode === "reconnect") {
            form.append("deviceId", getSelectedReconnectDeviceId());
            form.append("confirmReplaceActiveToken", "true");
        }

        fetch(cfg.sessionCreateUrl, { method: "POST", body: form, credentials: "same-origin" })
            .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, body: j }; }); })
            .then(function (res) {
                if (!res.ok || !res.body || !res.body.success) {
                    finish((res.body && res.body.message) || messages.sessionError, "danger");
                    showFallback();
                    return;
                }

                setStatus(messages.waiting || "Waiting…");
                // Launch the desktop app via the custom protocol (no token in the URL).
                window.location.href = res.body.protocolUrl;

                pollStatus(res.body.statusUrl);
                // Browsers cannot confirm a protocol handler opened; offer fallback shortly.
                fallbackTimer = setTimeout(showFallback, 6000);
            })
            .catch(function () {
                finish(messages.sessionError, "danger");
                showFallback();
            });
    }

    function stopSpinnerReset() {
        var spinner = document.getElementById("pbAutoSpinner");
        if (spinner) spinner.classList.remove("d-none");
        var fb = document.getElementById("pbAutoFallback");
        if (fb) fb.classList.add("d-none");
    }

    function bindAutomaticSetup() {
        var openBtn = document.getElementById("pbAutoOpenBtn");
        var retryBtn = document.getElementById("pbAutoRetryBtn");
        if (openBtn) openBtn.addEventListener("click", startAutomaticSetup);
        if (retryBtn) retryBtn.addEventListener("click", startAutomaticSetup);
    }

    function getDeviceById(deviceId) {
        var devices = cfg.devices || [];
        for (var i = 0; i < devices.length; i++) {
            if (String(devices[i].id) === String(deviceId)) return devices[i];
        }
        return null;
    }

    function normalizeReconnectText(value) {
        return (value || "").trim();
    }

    function getReconnectDisplayName(device) {
        var name = normalizeReconnectText(device.name);
        if (name) return name;
        return normalizeReconnectText(device.machineName);
    }

    function shouldShowReconnectMachineName(device) {
        var name = normalizeReconnectText(device.name);
        var machine = normalizeReconnectText(device.machineName);
        if (!machine || !name) return false;
        return name.localeCompare(machine, undefined, { sensitivity: "accent" }) !== 0;
    }

    function formatReconnectLastSeen(iso) {
        if (!iso) return messages.lastSeenNever || "—";
        try {
            var d = new Date(iso);
            if (isNaN(d.getTime())) return messages.lastSeenNever || "—";
            var now = new Date();
            var sameDay = d.getFullYear() === now.getFullYear()
                && d.getMonth() === now.getMonth()
                && d.getDate() === now.getDate();
            var time = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
            if (sameDay) {
                return (messages.lastSeen || "Last seen") + ": " + (messages.today || "Today") + " " + time;
            }
            return (messages.lastSeen || "Last seen") + ": " + d.toLocaleString([], { dateStyle: "short", timeStyle: "short" });
        } catch (_) {
            return messages.lastSeenNever || "—";
        }
    }

    function updateReconnectDeviceDetails() {
        var details = document.getElementById("pbReconnectDeviceDetails");
        var titleEl = document.getElementById("pbReconnectDeviceTitle");
        var machineEl = document.getElementById("pbReconnectDeviceMachine");
        var badgesHost = document.getElementById("pbReconnectDeviceBadges");
        var lastSeenEl = document.getElementById("pbReconnectDeviceLastSeen");
        if (!details || !titleEl || !machineEl || !badgesHost || !lastSeenEl) return;

        var device = getDeviceById(getSelectedReconnectDeviceId());
        if (!device) {
            details.classList.add("d-none");
            titleEl.textContent = "";
            machineEl.textContent = "";
            machineEl.classList.add("d-none");
            badgesHost.innerHTML = "";
            lastSeenEl.textContent = "";
            return;
        }

        titleEl.textContent = getReconnectDisplayName(device);

        if (shouldShowReconnectMachineName(device)) {
            machineEl.textContent = (messages.reconnectMachineLabel || "Machine:") + " "
                + normalizeReconnectText(device.machineName);
            machineEl.classList.remove("d-none");
        } else {
            machineEl.textContent = "";
            machineEl.classList.add("d-none");
        }

        var activeBadge = device.isActive
            ? '<span class="badge rounded-pill text-bg-success">' + escapeHtml(messages.deviceActive || "Active") + '</span>'
            : '<span class="badge rounded-pill text-bg-secondary">' + escapeHtml(messages.devicePassive || "Passive") + '</span>';
        var statusBadge = '<span class="badge rounded-pill text-bg-light text-body border">' + escapeHtml(device.status || "") + '</span>';

        badgesHost.innerHTML = activeBadge + statusBadge;
        lastSeenEl.textContent = formatReconnectLastSeen(device.lastSeenAtUtc);
        details.classList.remove("d-none");
    }

    function updatePrimaryCtaLabel() {
        var label = document.getElementById("pbAutoOpenBtnText");
        if (!label) return;
        var reconnect = getSelectedSetupMode() === "reconnect";
        label.textContent = reconnect
            ? (messages.reconnectOpenButton || messages.openButton || "Open")
            : (messages.openButton || "Open");
    }

    function updatePrimaryCtaState() {
        var openBtn = document.getElementById("pbAutoOpenBtn");
        var guidance = document.getElementById("pbReconnectDeviceGuidance");
        if (!openBtn) return;

        var reconnect = getSelectedSetupMode() === "reconnect";
        var hasDevice = !!getSelectedReconnectDeviceId();
        var disabled = reconnect && !hasDevice;

        openBtn.disabled = disabled;
        openBtn.setAttribute("aria-disabled", disabled ? "true" : "false");
        if (guidance) guidance.classList.toggle("d-none", !reconnect || hasDevice);
        updatePrimaryCtaLabel();
        updateReconnectDeviceDetails();
    }

    function updateSetupModeUi() {
        var reconnect = getSelectedSetupMode() === "reconnect";
        var section = document.getElementById("pbReconnectDeviceSection");
        if (section) section.classList.toggle("d-none", !reconnect);
        var manualTokenText = document.getElementById("pbManualTokenActionText");
        if (manualTokenText) {
            manualTokenText.textContent = reconnect
                ? (messages.createReconnectToken || "Create a new token for the selected device")
                : (messages.createNewDeviceToken || "Create a device token");
        }
        updatePrimaryCtaState();
    }

    function bindSetupMode() {
        var radios = document.querySelectorAll('input[name="pbSetupMode"]');
        radios.forEach(function (radio) {
            radio.addEventListener("change", updateSetupModeUi);
        });
        var reconnectSelect = document.getElementById("pbReconnectDeviceSelect");
        if (reconnectSelect) {
            reconnectSelect.addEventListener("change", updatePrimaryCtaState);
        }
        updateSetupModeUi();
    }

    function expandManualSetupSection(scrollIntoView) {
        var anchor = document.getElementById("pbManualSetup");
        // The guided first install keeps it hidden until the user tried to open the app or asked for it.
        if (anchor) anchor.hidden = false;
        var collapse = document.getElementById("pbManualSetupCollapse");
        if (collapse && window.bootstrap && window.bootstrap.Collapse) {
            bootstrap.Collapse.getOrCreateInstance(collapse).show();
        }
        if (scrollIntoView && anchor) {
            anchor.scrollIntoView({ behavior: "smooth", block: "start" });
            // Asked for explicitly: keyboard users land on the section they just opened.
            var header = cfg.guidedFirstInstall && typeof anchor.querySelector === "function"
                ? anchor.querySelector(".accordion-button")
                : null;
            if (header && typeof header.focus === "function") header.focus();
        }
    }

    // --- Guided first install (download, install, open and connect) ---

    /** Print Bridge is a Windows desktop app: phones, tablets and other systems get a notice instead. */
    function isWindowsDesktop(nav) {
        if (!nav) return false;
        var data = nav.userAgentData;
        var ua = nav.userAgent || "";
        if ((data && data.mobile) || /Android|iPhone|iPad|iPod|Mobile/i.test(ua)) return false;
        var platform = (data && data.platform) || nav.platform || "";
        return /^win/i.test(platform) || /Windows NT/i.test(ua);
    }

    /** Shows hidden steps; their numbers are fixed in the markup, so they never shift. */
    function revealSteps(ids) {
        ids.forEach(function (id) {
            var step = document.getElementById(id);
            if (step) step.hidden = false;
        });
    }

    function focusStep(titleId) {
        var title = document.getElementById(titleId);
        if (title && typeof title.focus === "function") title.focus();
    }

    /** Step 3 (choose automatic or manual) and step 4 (printer test), after the user says the app is ready. */
    function showConnectionChoice() {
        revealSteps(["pbStepConnect", "pbStepPrinter"]);
        focusStep("pbStepConnectTitle");
    }

    /**
     * Nothing here runs on load except the Windows notice. Each step appears after an explicit click, and focus
     * follows only that click. The download is the link's own navigation: no session, device or token is created,
     * and starting a download says nothing about installing. None of these buttons creates a session either: only
     * "Open Print Bridge and connect" does (the automatic flow above), and only "Connect manually" opens the manual
     * section.
     */
    function bindFirstInstall() {
        if (!cfg.guidedFirstInstall) return;

        var notice = document.getElementById("pbNotWindowsNotice");
        if (notice && !isWindowsDesktop(window.navigator)) notice.classList.remove("d-none");

        var download = document.getElementById("pbDownloadBtn");
        if (download) {
            download.addEventListener("click", function () {
                var status = document.getElementById("pbDownloadStatus");
                if (status) status.textContent = messages.downloadStarted || "";
                revealSteps(["pbStepPrepare"]);
                focusStep("pbStepPrepareTitle");
            });
        }

        // The browser cannot tell when the ZIP is extracted or the app has started, so the user confirms it.
        var prepared = document.getElementById("pbPreparedBtn");
        if (prepared) prepared.addEventListener("click", showConnectionChoice);

        var installed = document.getElementById("pbAlreadyInstalledBtn");
        if (installed) installed.addEventListener("click", showConnectionChoice);

        var manual = document.getElementById("pbChooseManualBtn");
        if (manual) {
            manual.addEventListener("click", function () {
                expandManualSetupSection(true);
            });
        }
    }

    function bindManualSectionLinks() {
        document.querySelectorAll('a[href="#pbManualSetup"]').forEach(function (link) {
            link.addEventListener("click", function (e) {
                e.preventDefault();
                expandManualSetupSection(true);
            });
        });

        // The first install shows no connection controls before step 3, whatever the address says.
        if (!cfg.guidedFirstInstall && window.location.hash === "#pbManualSetup") {
            expandManualSetupSection(true);
        }
    }

    // --- Manual connection (Wasla Web Panel URL + device token, pasted into the app's Settings) ---

    var manualTokenIssued = false;

    function formatMessage(template, value) {
        return String(template || "").replace("{0}", value == null ? "" : String(value));
    }

    /**
     * Masks or reveals the token field. Only the field's type changes: the value stays where it is, the toggle's
     * own name says what it will do next ("Show token" / "Hide token"), and Copy works either way.
     */
    function setTokenMasked(masked) {
        var value = document.getElementById("pbManualTokenValue");
        var text = document.getElementById("pbManualTokenToggleText");
        var icon = document.getElementById("pbManualTokenToggleIcon");
        if (value) value.type = masked ? "password" : "text";
        if (text) text.textContent = masked ? (messages.showToken || "Show token") : (messages.hideToken || "Hide token");
        if (icon) icon.className = masked ? "bi bi-eye" : "bi bi-eye-slash";
    }

    function bindTokenToggle() {
        var toggle = document.getElementById("pbManualTokenToggleBtn");
        var value = document.getElementById("pbManualTokenValue");
        if (!toggle || !value) return;
        toggle.addEventListener("click", function () {
            setTokenMasked(value.type !== "password");
        });
    }

    function setManualStatus(text) {
        var status = document.getElementById("pbManualConnectionStatus");
        if (status && status.textContent !== (text || "")) status.textContent = text || "";
    }

    /**
     * Shows the token the server just issued. Its only copy on this page is the read-only field's value for this
     * page view: it is not stored, logged or put in any URL, and a reload cannot show it again. Issuing it says
     * nothing about the connection; only the guided panel's server readiness check can report that.
     */
    function showManualToken(data, mode) {
        var result = document.getElementById("pbManualTokenResult");
        var value = document.getElementById("pbManualTokenValue");
        if (!result || !value) return;

        var title = document.getElementById("pbManualTokenTitle");
        var warning = document.getElementById("pbManualTokenWarning");
        var manage = document.getElementById("pbManualManageDeviceLink");
        var actionBtn = document.getElementById("pbManualTokenActionBtn");

        // Every new result starts masked, whatever the previous one showed.
        setTokenMasked(true);
        value.value = data.token;
        if (title) title.textContent = formatMessage(messages.tokenCreatedFor, data.deviceName);
        if (warning) {
            var replaced = mode === "reconnect";
            warning.textContent = replaced ? (messages.oldTokenInvalidAfterRegenerate || "") : "";
            warning.classList.toggle("d-none", !replaced);
        }
        if (manage && data.deviceId) {
            manage.href = data.detailsUrl || ("/print-bridge/devices/" + encodeURIComponent(data.deviceId));
            manage.classList.remove("d-none");
        }
        // One token per page view: another click must never silently replace it or add another device.
        if (actionBtn) actionBtn.classList.add("d-none");
        result.classList.remove("d-none");
        setManualStatus(messages.manualNotConnectedYet);
        if (title && typeof title.focus === "function") title.focus();

        // Lets a guided-setup panel start reading the server's readiness. It carries no token and no state.
        document.dispatchEvent(new CustomEvent("wasla:print-bridge-manual-setup-started"));
    }

    function bindManualToken() {
        var actionBtn = document.getElementById("pbManualTokenActionBtn");
        if (!actionBtn) return;

        actionBtn.addEventListener("click", function () {
            if (manualTokenIssued || actionBtn.disabled) return;

            var mode = getSelectedSetupMode();
            var url = cfg.manualDeviceCreateUrl;
            if (mode === "reconnect") {
                if (!validateReconnectSelection()) return;
                var deviceId = getSelectedReconnectDeviceId();
                var device = getDeviceById(deviceId);
                // Replacing a device's token stops the old one at once, so the user confirms it for that device.
                if (!window.confirm(formatMessage(messages.regenerateConfirm, device ? getReconnectDisplayName(device) : ""))) return;
                url = String(cfg.regenerateTokenUrlTemplate || "").replace("{id}", encodeURIComponent(deviceId));
            }
            if (!url) return;

            actionBtn.disabled = true;
            postForm(url, {})
                .then(function (data) {
                    if (!data || !data.success || !data.token) throw new Error(messages.manualTokenFailed);
                    manualTokenIssued = true;
                    showManualToken(data, mode);
                })
                .catch(function (err) {
                    showMessage((err && err.message) || messages.manualTokenFailed, "danger");
                })
                .finally(function () {
                    actionBtn.disabled = false;
                });
        });
    }

    /** The guided panel reports the server's verdict; this line only mirrors it next to the token. */
    function bindManualConnectionStatus() {
        document.addEventListener("wasla:guided-setup-state-changed", function (event) {
            var detail = event && event.detail;
            if (!manualTokenIssued || !detail || detail.section !== "print-bridge") return;
            setManualStatus(detail.state === "ready" ? messages.manualVerified : messages.manualNotConnectedYet);
        });
        document.addEventListener("wasla:guided-setup-watch-ended", function (event) {
            var detail = event && event.detail;
            if (!manualTokenIssued || !detail || detail.section !== "print-bridge" || detail.state === "ready") return;
            setManualStatus(messages.manualStillWaiting);
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        bindCopyButtons();
        bindSetupMode();
        bindAutomaticSetup();
        bindManualToken();
        bindTokenToggle();
        bindManualConnectionStatus();
        bindManualSectionLinks();
        bindFirstInstall();
    });
})();
