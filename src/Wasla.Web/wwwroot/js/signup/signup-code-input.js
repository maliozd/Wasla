(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupCodeInput = api;
    }
})(function () {
    "use strict";

    // The SMS code field of the phone verification step (WAS-194, Figma "Code input"): one field, not one box per
    // digit, so paste, the keyboard's one-time-code suggestion and WebOTP all fill it in one go. The code length is
    // the server's (codeLength); nothing here assumes one.

    // Arabic-Indic, extended Arabic-Indic (Persian) and full-width digits read as the Latin digits the SMS contains.
    var DIGIT_RANGES = [0x0660, 0x06F0, 0xFF10];

    function toLatinDigits(text) {
        var result = "";
        for (var i = 0; i < text.length; i++) {
            var code = text.charCodeAt(i);
            var mapped = null;
            for (var r = 0; r < DIGIT_RANGES.length; r++) {
                if (code >= DIGIT_RANGES[r] && code <= DIGIT_RANGES[r] + 9) {
                    mapped = String.fromCharCode(48 + code - DIGIT_RANGES[r]);
                    break;
                }
            }
            result += mapped === null ? text.charAt(i) : mapped;
        }
        return result;
    }

    // Digits only: spaces, hyphens, dots and any other separator a message or a password manager adds are dropped.
    // A positive maxLength keeps the first maxLength digits.
    function normalize(raw, maxLength) {
        var digits = toLatinDigits(String(raw == null ? "" : raw)).replace(/[^0-9]/g, "");
        var limit = Number(maxLength);
        return limit > 0 ? digits.slice(0, limit) : digits;
    }

    function isComplete(value, length) {
        var limit = Number(length);
        return limit > 0 && typeof value === "string" && value.length === limit && /^[0-9]+$/.test(value);
    }

    function supportsWebOtp(win) {
        return !!(win && "OTPCredential" in win && win.navigator && win.navigator.credentials
            && typeof win.navigator.credentials.get === "function");
    }

    // Android Chrome reads the code from an SMS bound to this origin. Feature-detected; an abort (the step was left,
    // the number changed) or a refusal is silent, because typing the code always works.
    function requestWebOtp(win, signal) {
        if (!supportsWebOtp(win)) {
            return Promise.resolve(null);
        }

        return win.navigator.credentials.get({ otp: { transport: ["sms"] }, signal: signal })
            .then(function (credential) {
                return credential && credential.code ? credential.code : null;
            })
            .catch(function () {
                return null;
            });
    }

    // Keeps the field to digits as the visitor types or pastes, and reports a full-length code once per value.
    // Returns an unbind function.
    function bind(input, options) {
        options = options || {};
        var lengthOf = typeof options.length === "function"
            ? options.length
            : function () { return options.length; };
        var lastCompleted = null;

        function apply(value) {
            var normalized = normalize(value, lengthOf());
            if (input.value !== normalized) {
                input.value = normalized;
                if (typeof input.setSelectionRange === "function") {
                    try {
                        input.setSelectionRange(normalized.length, normalized.length);
                    } catch (e) {
                        // Some input types refuse a selection; the value is still right.
                    }
                }
            }

            if (typeof options.onInput === "function") {
                options.onInput(normalized);
            }

            if (isComplete(normalized, lengthOf())) {
                if (normalized !== lastCompleted && typeof options.onComplete === "function") {
                    lastCompleted = normalized;
                    options.onComplete(normalized);
                }
            } else {
                lastCompleted = null;
            }
        }

        function onInput() {
            apply(input.value);
        }

        function onPaste(event) {
            var clipboard = event.clipboardData || (typeof window !== "undefined" ? window.clipboardData : null);
            if (!clipboard) {
                return;
            }
            event.preventDefault();
            apply(clipboard.getData("text"));
        }

        input.addEventListener("input", onInput);
        input.addEventListener("paste", onPaste);

        return function unbind() {
            input.removeEventListener("input", onInput);
            input.removeEventListener("paste", onPaste);
        };
    }

    return {
        normalize: normalize,
        isComplete: isComplete,
        supportsWebOtp: supportsWebOtp,
        requestWebOtp: requestWebOtp,
        bind: bind,
        // Fills the field as if the visitor had typed the value (used for a WebOTP result).
        fill: function (input, value) {
            input.value = String(value == null ? "" : value);
            input.dispatchEvent(new Event("input", { bubbles: true }));
        }
    };
});
