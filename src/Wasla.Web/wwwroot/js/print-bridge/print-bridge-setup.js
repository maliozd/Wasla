// Print Bridge setup page: copy server URL to clipboard.
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
    var currentManualSetupCode = null;

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

    function copyText(text, button, successMessage) {
        if (!text) return Promise.reject(new Error("empty"));

        function onSuccess() {
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
                copyText(serverInput.value, copyServerBtn, messages.copied).catch(function () {
                    showMessage(messages.copyFailed || "Copy failed", "danger");
                });
            });
        }

        var copySetupCodeBtn = document.getElementById("pbManualCopySetupCodeBtn");
        if (copySetupCodeBtn) {
            copySetupCodeBtn.addEventListener("click", function () {
                copyText(currentManualSetupCode, copySetupCodeBtn, messages.copied).catch(function () {
                    showMessage(messages.copyFailed || "Could not copy setup code", "danger");
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
                            break;
                        case "Failed":
                            finish(messages.failed, "danger");
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
        showMessage(messages.selectDevice || "Select a device.", "danger");
        return false;
    }

    function shouldConfirmAutomaticTokenReplacement() {
        if (getSelectedSetupMode() !== "reconnect") return true;
        return window.confirm(messages.reconnectTokenWarning || messages.autoReplaceTokenConfirm || "This will replace the selected device token. Continue?");
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

    function updateSetupModeUi() {
        var reconnect = getSelectedSetupMode() === "reconnect";
        var section = document.getElementById("pbReconnectDeviceSection");
        if (section) section.classList.toggle("d-none", !reconnect);
        var manualSetupCodeText = document.getElementById("pbManualSetupCodeActionText");
        if (manualSetupCodeText) {
            manualSetupCodeText.textContent = reconnect
                ? (messages.generateReconnectSetupCode || "Generate reconnect setup code")
                : (messages.generateNewDeviceSetupCode || "Generate new device setup code");
        }
    }

    function bindSetupMode() {
        var radios = document.querySelectorAll('input[name="pbSetupMode"]');
        radios.forEach(function (radio) {
            radio.addEventListener("change", updateSetupModeUi);
        });
        updateSetupModeUi();
    }

    function showManualSetupCode(code, mode, options) {
        options = options || {};
        currentManualSetupCode = code || null;
        var box = document.getElementById("pbManualSetupCodeBox");
        var title = document.getElementById("pbManualSetupCodeTitle");
        var notice = document.getElementById("pbManualSetupCodeNotice");
        var warning = document.getElementById("pbManualSetupCodeWarning");
        var value = document.getElementById("pbManualSetupCodeValue");
        if (!box || !value || !currentManualSetupCode) return;

        if (title) title.textContent = options.title || messages.oneTimeSetupCodeCreated || "";
        if (notice) notice.textContent = options.notice || "";
        if (warning) {
            var warningText = options.warning || "";
            warning.textContent = warningText;
            warning.classList.toggle("d-none", !warningText);
        }
        value.textContent = currentManualSetupCode;
        box.classList.remove("d-none");
        box.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    function bindManualSetupCodeButtons() {
        var actionBtn = document.getElementById("pbManualSetupCodeActionBtn");
        if (!actionBtn) return;

        actionBtn.addEventListener("click", function () {
            var mode = getSelectedSetupMode();
            if (mode === "reconnect" && !validateReconnectSelection()) return;
            if (mode === "reconnect") {
                if (!window.confirm(messages.reconnectTokenWarning || messages.confirmRegenerateToken || "Regenerate token?")) {
                    return;
                }
            }

            actionBtn.disabled = true;
            var fields = { setupMode: mode };
            if (mode === "reconnect") {
                fields.deviceId = getSelectedReconnectDeviceId();
                fields.confirmReplaceActiveToken = "true";
            }

            postForm(cfg.sessionCreateUrl, fields)
                    .then(function (data) {
                        if (!data || !data.success || !data.code) throw new Error(messages.manualTokenFailed);
                        var expires = data.expiresAtUtc
                            ? (messages.setupCodeExpires || "Expires: {0}").replace("{0}", new Date(data.expiresAtUtc).toLocaleString())
                            : "";
                        showManualSetupCode(data.code, mode, {
                            title: messages.oneTimeSetupCodeCreated,
                            notice: expires,
                            warning: mode === "reconnect" ? messages.reconnectTokenWarning : ""
                        });
                        showMessage(messages.oneTimeSetupCodeCreated || "Setup code created.", "success");
                    })
                    .catch(function (err) {
                        showMessage(err.message || messages.manualTokenFailed, "danger");
                    })
                    .finally(function () {
                        actionBtn.disabled = false;
                    });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        bindCopyButtons();
        bindSetupMode();
        bindAutomaticSetup();
        bindManualSetupCodeButtons();
    });
})();
