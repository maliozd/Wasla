(function (window) {
    "use strict";

    // The phone verification step of the signup flow (WAS-194, Figma T1–T7, MT1–MT7, L3) over the WAS-192 phone
    // endpoints. The server owns every rule (code, expiry, resend wait, attempts, lock, limits); the page shows the
    // status it returns, counts its relative seconds down locally and asks again when a wait ends. Error texts are the
    // server's localized messages; only the page's own labels come from the configuration.

    var Draft = window.WaslaSignupDraft;
    var Countdown = window.WaslaSignupCountdown;
    var CodeInput = window.WaslaSignupCodeInput;

    function format(template, args) {
        return String(template == null ? "" : template).replace(/\{(\d+)\}/g, function (match, index) {
            var value = args[Number(index)];
            return value == null ? "" : String(value);
        });
    }

    function init(options) {
        var root = options.root;
        if (!root || !Draft || !Countdown || !CodeInput) {
            return null;
        }

        var api = options.api;
        var messages = options.messages || {};
        var codeLength = options.codeLength || 6;
        var hooks = options.hooks || {};

        function q(selector) {
            return root.querySelector(selector);
        }

        var el = {
            title: q("[data-su-phone-title]"),
            lead: q("[data-su-phone-lead]"),
            alert: q("[data-su-phone-alert]"),
            alertTitle: q("[data-su-phone-alert-title]"),
            alertText: q("[data-su-phone-alert-text]"),
            alertSupport: q("[data-su-phone-alert-support]"),
            card: q("[data-su-phone-card]"),
            cardLabel: q("[data-su-phone-card-label]"),
            masked: q("[data-su-phone-masked]"),
            changeButtons: root.querySelectorAll("[data-su-phone-change]"),
            verifiedCard: q("[data-su-phone-verified]"),
            fullNumber: q("[data-su-phone-full]"),
            changer: q("[data-su-phone-changer]"),
            changeInput: q("[data-su-phone-change-input]"),
            changeError: q("[data-su-phone-change-error]"),
            changeCancel: q("[data-su-phone-change-cancel]"),
            codeField: q("[data-su-code-field]"),
            codeWrap: q("[data-su-code]"),
            code: q("#signupPhoneCode"),
            codeError: q("#signupPhoneCode-error"),
            meta: q("[data-su-phone-meta]"),
            sent: q("[data-su-phone-sent]"),
            countdownLine: q("[data-su-phone-countdown]"),
            countdownValue: q("[data-su-phone-countdown] .su-countdown"),
            resend: q("[data-su-phone-resend]"),
            sending: q("[data-su-phone-sending]"),
            moving: q("[data-su-phone-moving]"),
            movingLive: q("[data-su-phone-moving-live]"),
            primary: q("[data-su-phone-primary]")
        };

        // After a verify answer of this page confirms the number, the step stays for a moment with a confirmation, then
        // the flow moves on. Only that answer starts the wait (a draft restored as verified moves at once). Leaving the
        // step, another phone state or the visitor's own "continue" ends it, and a wait that ended never moves anyone.
        var VERIFIED_PAUSE_MS = 1200;
        var moving = null;

        var status = options.status || { state: "notSent" };
        var local = { sending: false, changing: false, codeExpired: false, busy: false, confirming: false };
        // Counts the phone requests started and answered here, so a draft read that overlapped one of them does not
        // put back the older phone state it carries.
        var revision = 0;
        var sentAt = null;
        var resendTimer = null;
        var expiryTimer = null;
        var otpController = null;
        var fullNumber = options.fullNumber || "";

        // ------------------------------------------------------------ Alerts and field errors

        function showAlert(title, text, withSupport) {
            if (!el.alert) {
                return;
            }
            el.alertTitle.textContent = title || "";
            el.alertTitle.hidden = !title;
            el.alertText.textContent = text || "";
            if (el.alertSupport) {
                el.alertSupport.hidden = !withSupport;
            }
            el.alert.hidden = false;
        }

        function clearAlert() {
            if (el.alert) {
                el.alert.hidden = true;
            }
        }

        function setCodeError(text) {
            if (!el.codeError) {
                return;
            }
            el.codeError.textContent = text || "";
            if (text) {
                el.code.setAttribute("aria-invalid", "true");
            } else {
                el.code.removeAttribute("aria-invalid");
            }
        }

        function setChangeError(text) {
            if (!el.changeError) {
                return;
            }
            el.changeError.textContent = text || "";
            el.changeError.className = text ? "field-validation-error" : "field-validation-valid";
            if (text) {
                el.changeInput.setAttribute("aria-invalid", "true");
            } else {
                el.changeInput.removeAttribute("aria-invalid");
            }
        }

        // ------------------------------------------------------------ The move after a fresh verification

        // The confirmation: its sentence once in the status region, and the visible line (hidden from assistive
        // technology, so the sentence is not read twice) with a check and a small spinner.
        function showMoving(text) {
            if (el.movingLive) {
                el.movingLive.textContent = text || "";
            }
            if (!el.moving) {
                return;
            }
            el.moving.textContent = "";
            el.moving.hidden = !text;
            if (!text) {
                return;
            }
            var icon = document.createElement("i");
            icon.className = "bi bi-check-circle-fill";
            icon.setAttribute("aria-hidden", "true");
            var label = document.createElement("span");
            label.textContent = text;
            var spinner = document.createElement("span");
            spinner.className = "su-spinner";
            spinner.setAttribute("aria-hidden", "true");
            el.moving.appendChild(icon);
            el.moving.appendChild(label);
            el.moving.appendChild(spinner);
        }

        function endMove() {
            if (!moving) {
                return;
            }
            clearTimeout(moving.timer);
            moving = null;
            local.confirming = false;
            showMoving("");
            render();
        }

        function moveOn() {
            endMove();
            if (status.state === "verified" && typeof hooks.onVerified === "function") {
                hooks.onVerified(status);
            }
        }

        function startMove() {
            if (moving) {
                return;
            }
            var move = moving = {};
            local.confirming = true;
            render();
            showMoving(messages.verifiedMoving);
            move.timer = setTimeout(function () {
                if (moving === move) {
                    moveOn();
                }
            }, VERIFIED_PAUSE_MS);
        }

        // ------------------------------------------------------------ Timers

        function stopTimers() {
            if (resendTimer) {
                resendTimer.stop();
                resendTimer = null;
            }
            if (expiryTimer) {
                expiryTimer.stop();
                expiryTimer = null;
            }
        }

        function stopOtp() {
            if (otpController) {
                otpController.abort();
                otpController = null;
            }
        }

        function refreshStatus() {
            if (!api) {
                return Promise.resolve();
            }
            return api.getPhone().then(function (result) {
                if (result.ok && result.data) {
                    apply(result.data);
                } else if (!result.ok && typeof hooks.onRequestFailed === "function") {
                    hooks.onRequestFailed(result);
                }
            });
        }

        function startTimers(view) {
            stopTimers();
            if (view.resendIn && view.resendIn > 0) {
                resendTimer = Countdown.start({
                    seconds: view.resendIn,
                    onTick: function (seconds) {
                        if (el.countdownValue) {
                            el.countdownValue.textContent = Countdown.format(seconds);
                        }
                    },
                    onDone: function () {
                        resendTimer = null;
                        if (status.state === "locked") {
                            refreshStatus();
                            return;
                        }
                        status = Object.assign({}, status, { resendAvailableInSeconds: 0 });
                        render();
                        if (el.resend && !el.resend.hidden && document.activeElement === document.body) {
                            el.resend.focus();
                        }
                    }
                });
            }
            if (status.state === "codeSent" && typeof status.codeExpiresInSeconds === "number" && !local.codeExpired) {
                expiryTimer = Countdown.start({
                    seconds: status.codeExpiresInSeconds,
                    onDone: function () {
                        expiryTimer = null;
                        local.codeExpired = true;
                        setCodeError(messages.codeExpired);
                        render();
                    }
                });
            }
        }

        // ------------------------------------------------------------ Rendering

        function setText(element, value) {
            if (element && element.textContent !== value) {
                element.textContent = value;
            }
        }

        function primaryLabel(kind) {
            return {
                send: messages.send,
                resend: messages.resend,
                verify: messages.verify,
                "continue": messages.toReview,
                sendToNumber: messages.sendToNumber
            }[kind] || "";
        }

        function render() {
            var view = Draft.phoneView(status, local);
            root.setAttribute("data-phone-state", view.state);

            if (el.title) {
                setText(el.title, view.state === "verified" ? el.title.getAttribute("data-title-verified") : el.title.getAttribute("data-title-default"));
            }
            if (el.lead) {
                var lead = view.state === "verified" ? "data-lead-verified"
                    : (view.state === "codeSent" || view.state === "expired") ? "data-lead-sent" : "data-lead-notsent";
                setText(el.lead, el.lead.getAttribute(lead) || "");
            }

            if (el.card) {
                el.card.hidden = !view.showNumberCard;
                setText(el.masked, status.maskedPhone || "");
                if (el.cardLabel) {
                    setText(el.cardLabel, view.state === "codeSent" || view.state === "expired"
                        ? el.cardLabel.getAttribute("data-label-sent")
                        : el.cardLabel.getAttribute("data-label-default"));
                }
            }
            Array.prototype.forEach.call(el.changeButtons, function (button) {
                button.hidden = !view.canChangeNumber;
            });
            if (el.verifiedCard) {
                el.verifiedCard.hidden = !view.showVerifiedCard;
                setText(el.fullNumber, fullNumber || status.maskedPhone || "");
            }
            if (el.changer) {
                el.changer.hidden = !view.changing;
            }

            if (el.codeField) {
                el.codeField.hidden = !view.showCode;
                el.code.disabled = !view.codeEnabled;
                el.code.readOnly = view.state === "verified";
                if (el.codeWrap) {
                    el.codeWrap.setAttribute("data-state", view.state === "verified" ? "verified" : "");
                }
                if (view.state !== "codeSent" && view.state !== "expired") {
                    setCodeError("");
                }
            }

            if (el.sending) {
                el.sending.hidden = !view.sending;
            }
            if (el.sent) {
                el.sent.hidden = !(sentAt && view.state === "codeSent" && !view.sending);
                if (sentAt) {
                    setText(el.sent.querySelector("span") || el.sent, format(messages.sent, [sentAt]));
                }
            }
            var left = resendTimer ? resendTimer.remaining() : view.resendIn;
            var waiting = typeof left === "number" && left > 0 && view.state !== "verified";
            if (el.countdownLine) {
                el.countdownLine.hidden = !waiting || view.sending || view.changing;
                if (waiting && el.countdownValue) {
                    el.countdownValue.textContent = Countdown.format(left);
                }
            }
            if (el.resend) {
                el.resend.hidden = !(view.state === "codeSent" && view.canSendNow) || view.changing;
            }
            if (el.meta) {
                el.meta.hidden = view.changing;
            }

            if (el.primary) {
                var kind = view.primary;
                el.primary.hidden = !kind;
                el.primary.setAttribute("data-kind", kind || "");
                setText(el.primary, primaryLabel(kind));
                var blocked = view.sending || local.busy
                    || ((kind === "send" || kind === "resend") && !view.canSendNow);
                el.primary.setAttribute("aria-disabled", blocked ? "true" : "false");
                if (view.sending || local.busy) {
                    el.primary.setAttribute("aria-busy", "true");
                } else {
                    el.primary.removeAttribute("aria-busy");
                }
            }

            if (typeof hooks.onRender === "function") {
                hooks.onRender(status, view);
            }
            return view;
        }

        function apply(next) {
            var previous = status.state;
            status = next || status;
            if (status.state !== "verified") {
                endMove();
            }
            if (status.state !== "codeSent") {
                local.codeExpired = false;
            }
            if (previous !== status.state || status.state !== "codeSent") {
                stopOtp();
            }
            if (typeof hooks.onStatus === "function") {
                hooks.onStatus(status);
            }
            stopTimers();
            var view = render();
            startTimers(view);
            return view;
        }

        // ------------------------------------------------------------ Server errors

        // Shows an error answer of a phone endpoint. Returns true when it was handled here.
        function handleError(result, where) {
            if (!result || result.ok) {
                return false;
            }
            if (result.kind !== "api") {
                if (typeof hooks.onRequestFailed === "function") {
                    hooks.onRequestFailed(result);
                }
                return true;
            }

            var error = result.error || {};
            if (error.phone) {
                apply(error.phone);
            }

            switch (error.code) {
                case "Signup.Draft.Missing":
                case "Signup.Draft.Submitted":
                    if (typeof hooks.onRequestFailed === "function") {
                        hooks.onRequestFailed(result);
                    }
                    return true;
                case "Signup.Phone.OwnerIncomplete":
                    if (typeof hooks.onOwnerIncomplete === "function") {
                        hooks.onOwnerIncomplete(error);
                    }
                    return true;
                case "Signup.Phone.AlreadyVerified":
                    refreshStatus().then(function () {
                        if (status.state === "verified" && typeof hooks.onVerified === "function") {
                            hooks.onVerified(status);
                        }
                    });
                    return true;
                case "Signup.Phone.CodeIncorrect":
                case "Signup.Phone.CodeFormatInvalid":
                case "Signup.Phone.NoActiveCode":
                case "Signup.Phone.CodeExpired":
                    if (error.code === "Signup.Phone.CodeExpired") {
                        local.codeExpired = true;
                    }
                    clearAlert();
                    render();
                    setCodeError(error.message);
                    if (el.code && !el.code.disabled) {
                        el.code.focus();
                        el.code.select();
                    }
                    return true;
                case "Signup.Phone.DeliveryFailed":
                    showAlert(messages.deliveryFailedTitle, error.message, true);
                    break;
                case "Signup.Phone.Unavailable":
                    showAlert("", error.message, true);
                    break;
                default:
                    // Waits and limits (ResendNotAvailable, Locked, SendLimitReached, CapacityReached, TooManyAttempts)
                    // and validation answers: the server's message, at the top of the step.
                    if (where === "change" && error.fieldErrors && error.fieldErrors.length) {
                        setChangeError(error.fieldErrors[0].message);
                        el.changeInput.focus();
                        render();
                        return true;
                    }
                    showAlert("", error.message, false);
                    break;
            }

            render();
            if (el.alert && !el.alert.hidden) {
                el.alert.focus();
            }
            return true;
        }

        // ------------------------------------------------------------ Actions

        function requestOtp() {
            stopOtp();
            if (!CodeInput.supportsWebOtp(window) || typeof AbortController !== "function") {
                return;
            }
            otpController = new AbortController();
            CodeInput.requestWebOtp(window, otpController.signal).then(function (code) {
                if (code && el.code && !el.code.disabled) {
                    CodeInput.fill(el.code, code);
                }
            });
        }

        function send() {
            if (local.sending || local.busy) {
                return Promise.resolve();
            }
            local.sending = true;
            revision += 1;
            clearAlert();
            setCodeError("");
            render();
            return api.sendCode().then(function (result) {
                revision += 1;
                local.sending = false;
                if (result.ok) {
                    sentAt = new Intl.DateTimeFormat(document.documentElement.lang || undefined, { hour: "2-digit", minute: "2-digit", calendar: "gregory" }).format(new Date());
                    local.codeExpired = false;
                    if (el.code) {
                        el.code.value = "";
                    }
                    apply(result.data);
                    if (el.code && !el.code.disabled) {
                        el.code.focus();
                    }
                    requestOtp();
                    return;
                }
                handleError(result, "send");
            });
        }

        function verify(code) {
            if (local.busy || local.sending) {
                return Promise.resolve();
            }
            var normalized = CodeInput.normalize(code, codeLength);
            if (!CodeInput.isComplete(normalized, codeLength)) {
                setCodeError(messages.codeFormat);
                el.code.focus();
                return Promise.resolve();
            }
            local.busy = true;
            revision += 1;
            setCodeError("");
            render();
            // The number this code was typed for. If the owner's number changed while the check was out, its answer
            // belongs to the old number: it verifies nothing here and moves nobody; the server's current state is read.
            var forNumber = fullNumber;
            return api.verifyCode(normalized).then(function (result) {
                revision += 1;
                local.busy = false;
                if (fullNumber !== forNumber) {
                    render();
                    return refreshStatus();
                }
                if (result.ok) {
                    clearAlert();
                    apply(result.data);
                    // An answer that arrives after the visitor left the step starts nothing: back on the step, the
                    // verified number waits for their "continue".
                    if (status.state === "verified" && !root.hidden) {
                        startMove();
                    }
                    return;
                }
                handleError(result, "verify");
            });
        }

        function openChanger() {
            endMove();
            local.changing = true;
            clearAlert();
            setChangeError("");
            if (el.changeInput && typeof options.currentNumber === "function") {
                el.changeInput.value = options.currentNumber() || "";
            }
            render();
            if (el.changeInput) {
                el.changeInput.focus();
            }
        }

        // The visible "change number" button: on the number card, or on the verified card.
        function shownChangeButton() {
            return Array.prototype.filter.call(el.changeButtons, function (button) {
                return !button.hidden && button.getClientRects().length > 0;
            })[0] || null;
        }

        function closeChanger() {
            local.changing = false;
            setChangeError("");
            render();
            var change = shownChangeButton();
            if (change) {
                change.focus();
            }
        }

        function submitChange() {
            if (local.busy) {
                return Promise.resolve();
            }
            local.busy = true;
            revision += 1;
            render();
            var number = el.changeInput ? el.changeInput.value : "";
            return api.changePhone(number).then(function (result) {
                revision += 1;
                local.busy = false;
                if (result.ok) {
                    local.changing = false;
                    setChangeError("");
                    apply(result.data);
                    if (typeof hooks.onNumberChanged === "function") {
                        hooks.onNumberChanged(number);
                    }
                    if (status.state !== "verified") {
                        return send();
                    }
                    return undefined;
                }
                handleError(result, "change");
                return undefined;
            });
        }

        function primaryAction() {
            var kind = el.primary ? el.primary.getAttribute("data-kind") : "";
            if (el.primary && el.primary.getAttribute("aria-disabled") === "true") {
                return;
            }
            if (kind === "send" || kind === "resend") {
                send();
            } else if (kind === "verify") {
                verify(el.code ? el.code.value : "");
            } else if (kind === "continue") {
                // During the confirmation too: the visitor's press moves now, once, and the wait is dropped.
                moveOn();
            } else if (kind === "sendToNumber") {
                submitChange();
            }
        }

        // ------------------------------------------------------------ Wiring

        if (el.code) {
            el.code.setAttribute("maxlength", String(codeLength));
            CodeInput.bind(el.code, {
                length: codeLength,
                onInput: function () {
                    if (el.codeError && el.codeError.textContent) {
                        setCodeError("");
                    }
                },
                onComplete: function (value) {
                    var view = Draft.phoneView(status, local);
                    if (view.codeEnabled && !local.busy) {
                        verify(value);
                    }
                }
            });
        }

        root.addEventListener("click", function (event) {
            var target = event.target && event.target.closest ? event.target : null;
            if (!target) {
                return;
            }
            if (target.closest("[data-su-phone-primary]")) {
                event.preventDefault();
                primaryAction();
            } else if (target.closest("[data-su-phone-resend]")) {
                event.preventDefault();
                send();
            } else if (target.closest("[data-su-phone-change]")) {
                event.preventDefault();
                openChanger();
            } else if (target.closest("[data-su-phone-change-cancel]")) {
                event.preventDefault();
                closeChanger();
            }
        });

        // The new number is the owner's mobile too: the same format and the same 10-digit limit.
        if (el.changeInput && window.WaslaSignupPhone) {
            window.WaslaSignupPhone.bindMobileInput(el.changeInput);
        }

        apply(status);

        return {
            // The visitor arrived at the step. autoSend: the owner step's "send verification code" brought them here.
            enter: function (enterOptions) {
                var view = render();
                if (enterOptions && enterOptions.autoSend && view.canSendNow
                    && (view.state === "notSent" || view.state === "expired")) {
                    return send();
                }
                if (view.state === "codeSent" && el.code && !el.code.disabled) {
                    requestOtp();
                }
                return Promise.resolve();
            },
            leave: function () {
                stopOtp();
                endMove();
            },
            focusTarget: function () {
                if (local.changing && el.changeInput) {
                    return el.changeInput;
                }
                var view = Draft.phoneView(status, local);
                if (view.codeEnabled && el.code) {
                    return el.code;
                }
                return null;
            },
            apply: apply,
            setFullNumber: function (value) {
                fullNumber = value || "";
                render();
            },
            status: function () { return status; },
            revision: function () { return revision; },
            primaryAction: primaryAction
        };
    }

    window.WaslaSignupPhoneStep = { init: init };
})(window);
