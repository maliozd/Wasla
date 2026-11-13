(function (window) {
    "use strict";

    // Türkiye phone numbers on the signup page: the owner's mobile, and the restaurant's mobile or landline. Each is the
    // 10-digit national number after the fixed +90 prefix (mobile 5xx, landline area code and 7 digits), typed and shown
    // as "212 345 67 89" and sent as digits. A country code or trunk zero the visitor types or pastes is removed, so it is
    // never stored twice. A field holds at most 10 national digits (owner's decision): typing an 11th does nothing, and a
    // longer paste keeps its first 10 after the prefix. A shorter number (a 444 line) is kept as typed. The server still
    // refuses a longer business phone sent to it directly, and the owner's mobile keeps its exact rule.

    var MOBILE_TYPE = "Mobile";
    var NATIONAL_LENGTH = 10;
    var EXAMPLE_AREA_CODE = "212";
    var EXAMPLE_SUBSCRIBER = "345 67 89";

    function stripPhoneDigits(value) {
        return String(value == null ? "" : value).replace(/\D/g, "");
    }

    // The national digits, how many prefix digits came off the front, and whether more than 10 national digits were
    // there (cut to the first 10). A national number has 10 digits, so with more the front is a prefix: 0090, 90 or the
    // trunk 0, the prefixes the server removes. Waiting for more than 10 digits lets "0212…" and "+90 212…" be typed
    // digit by digit.
    function splitNational(value) {
        var digits = stripPhoneDigits(value);
        var prefix = 0;

        if (digits.length > NATIONAL_LENGTH) {
            if (digits.indexOf("0090") === 0) {
                prefix = 4;
            } else if (digits.indexOf("90") === 0) {
                prefix = 2;
            } else if (digits.charAt(0) === "0") {
                prefix = 1;
            }
        }

        var rest = digits.slice(prefix);
        return { national: rest.slice(0, NATIONAL_LENGTH), prefix: prefix, overflow: rest.length > NATIONAL_LENGTH };
    }

    function normalizeTurkishLocalDigits(value) {
        return splitNational(value).national;
    }

    // Typing text over the selection [start, end) would give more than 10 national digits: the field refuses it, so a
    // digit typed into a full number never pushes out one already there. A prefix typed in front still fits.
    function wouldOverflow(value, start, end, text) {
        var current = String(value == null ? "" : value);
        var from = typeof start === "number" ? start : current.length;
        var to = typeof end === "number" ? end : from;
        return splitNational(current.slice(0, from) + String(text == null ? "" : text) + current.slice(to)).overflow;
    }

    // "2123456789" → "212 345 67 89"; fewer digits are grouped as far as they go.
    function formatTurkishPhoneDigits(digits) {
        digits = stripPhoneDigits(digits).slice(0, NATIONAL_LENGTH);
        var parts = [];

        if (digits.length > 0) {
            parts.push(digits.slice(0, Math.min(3, digits.length)));
        }
        if (digits.length > 3) {
            parts.push(digits.slice(3, Math.min(6, digits.length)));
        }
        if (digits.length > 6) {
            parts.push(digits.slice(6, Math.min(8, digits.length)));
        }
        if (digits.length > 8) {
            parts.push(digits.slice(8, NATIONAL_LENGTH));
        }

        return parts.join(" ");
    }

    // The field's text after typing or pasting, at most 10 national digits, and where the caret goes: after the digit it
    // followed before, less any prefix digits removed in front of it. Without a caret it goes to the end.
    function formatPhoneValue(value, caret) {
        var text = String(value == null ? "" : value);
        var split = splitNational(text);
        var formatted = formatTurkishPhoneDigits(split.national);
        if (typeof caret !== "number") {
            return { value: formatted, caret: formatted.length };
        }

        var before = stripPhoneDigits(text.slice(0, Math.max(0, Math.min(caret, text.length)))).length;
        var wanted = Math.max(0, Math.min(split.national.length, before - split.prefix));
        var position = 0;
        var seen = 0;
        while (position < formatted.length && seen < wanted) {
            if (/\d/.test(formatted.charAt(position))) {
                seen++;
            }
            position++;
        }
        return { value: formatted, caret: position };
    }

    // The landline example for the hint: the chosen city's area code when it has one, otherwise 212. Only a hint: a city
    // can have several codes (İstanbul has 212 and 216), so nothing is filled in or checked against it.
    function landlineExample(areaCode) {
        var code = stripPhoneDigits(areaCode);
        return "+90 " + (code.length === 3 ? code : EXAMPLE_AREA_CODE) + " " + EXAMPLE_SUBSCRIBER;
    }

    var pure = {
        stripPhoneDigits: stripPhoneDigits,
        normalizeTurkishLocalDigits: normalizeTurkishLocalDigits,
        wouldOverflow: wouldOverflow,
        formatTurkishPhoneDigits: formatTurkishPhoneDigits,
        formatPhoneValue: formatPhoneValue,
        landlineExample: landlineExample
    };

    if (typeof module !== "undefined" && module.exports) {
        module.exports = pure;
    }
    if (!window || !window.document) {
        return;
    }
    var document = window.document;

    function runWhenReady(callback) {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", callback);
        } else {
            callback();
        }
    }

    function formatPhoneInput(input) {
        if (!input) {
            return;
        }

        var focused = document.activeElement === input;
        var result = formatPhoneValue(input.value, focused ? input.selectionStart : null);
        if (input.value !== result.value) {
            input.value = result.value;
            if (focused && typeof input.setSelectionRange === "function") {
                input.setSelectionRange(result.caret, result.caret);
            }
        }
    }

    // The business phone type is a pair of radio buttons (Mobile / Landline) on the restaurant step (WAS-194).
    function getPhoneTypeInputs() {
        return document.querySelectorAll('input[name="BusinessPhoneType"]');
    }

    function getBusinessPhoneInput() {
        return document.getElementById("signupBusinessPhone");
    }

    function getOwnerPhoneInput() {
        return document.getElementById("signupOwnerPhone");
    }

    function isBusinessMobileSelected() {
        var checked = document.querySelector('input[name="BusinessPhoneType"]:checked');
        return !!checked && checked.value === MOBILE_TYPE;
    }

    // A paste replaces the selection with the pasted text; then the whole field is formatted, the caret follows the
    // pasted digits, and the page hears an input like any other change.
    function bindPasteFormatter(input) {
        input.addEventListener("paste", function (event) {
            var clipboard = event.clipboardData;
            if (!clipboard) {
                return;
            }

            event.preventDefault();
            var pasted = clipboard.getData("text") || "";
            var start = typeof input.selectionStart === "number" ? input.selectionStart : input.value.length;
            var end = typeof input.selectionEnd === "number" ? input.selectionEnd : start;
            var result = formatPhoneValue(input.value.slice(0, start) + pasted + input.value.slice(end), start + pasted.length);
            input.value = result.value;
            if (typeof input.setSelectionRange === "function") {
                input.setSelectionRange(result.caret, result.caret);
            }
            input.dispatchEvent(new Event("input", { bubbles: true }));
        });
    }

    function isTyping(inputType) {
        return typeof inputType === "string" && inputType.indexOf("insert") === 0
            && inputType !== "insertFromPaste" && inputType !== "insertFromDrop";
    }

    // Typing into a full field changes nothing: the keystroke is refused before it lands, and where a browser cannot
    // refuse it (an input method), the field goes back to its last value with the caret where it was. Pastes, drops and
    // values set by the page (a restored draft) keep their first 10 national digits instead.
    function bindDigitLimit(input) {
        var last = { value: input.value, caret: null };

        input.addEventListener("beforeinput", function (event) {
            var start = typeof input.selectionStart === "number" ? input.selectionStart : input.value.length;
            var end = typeof input.selectionEnd === "number" ? input.selectionEnd : start;
            last = { value: input.value, caret: start };
            if (isTyping(event.inputType) && event.data != null && wouldOverflow(input.value, start, end, event.data)) {
                event.preventDefault();
            }
        });

        input.addEventListener("input", function (event) {
            if (isTyping(event.inputType) && splitNational(input.value).overflow && splitNational(last.value).national.length === NATIONAL_LENGTH) {
                input.value = last.value;
                if (typeof last.caret === "number" && typeof input.setSelectionRange === "function") {
                    input.setSelectionRange(last.caret, last.caret);
                }
            }
            formatPhoneInput(input);
            last = { value: input.value, caret: null };
        }, true);
    }

    function bindPhoneInput(input, flag) {
        if (!input || input.dataset[flag] === "true") {
            return;
        }

        input.dataset[flag] = "true";
        bindDigitLimit(input);
        input.addEventListener("blur", function () {
            formatPhoneInput(input);
        });
        bindPasteFormatter(input);
        formatPhoneInput(input);
    }

    function normalizeForSubmit() {
        var businessInput = getBusinessPhoneInput();
        var ownerInput = getOwnerPhoneInput();

        if (businessInput) {
            businessInput.value = normalizeTurkishLocalDigits(businessInput.value);
        }
        if (ownerInput && ownerInput.value) {
            ownerInput.value = normalizeTurkishLocalDigits(ownerInput.value);
        }
    }

    function bindSubmitNormalization() {
        var form = document.getElementById("signupForm");
        if (!form || form.dataset.waslaPhoneSubmitBound === "true") {
            return;
        }

        form.dataset.waslaPhoneSubmitBound = "true";
        form.addEventListener("submit", normalizeForSubmit);
    }

    // Mobile and landline share one format, so switching the type leaves the number as it is.
    function refreshExistingValues() {
        formatPhoneInput(getOwnerPhoneInput());
        formatPhoneInput(getBusinessPhoneInput());
    }

    function init() {
        bindPhoneInput(getOwnerPhoneInput(), "waslaOwnerPhoneBound");
        if (getPhoneTypeInputs().length) {
            bindPhoneInput(getBusinessPhoneInput(), "waslaBusinessPhoneBound");
        }
        bindSubmitNormalization();
        refreshExistingValues();
        window.setTimeout(refreshExistingValues, 0);
    }

    window.WaslaSignupPhone = {
        init: init,
        formatMobileInput: formatPhoneInput,
        formatPhoneInput: formatPhoneInput,
        normalizeTurkishLocalDigits: normalizeTurkishLocalDigits,
        // Another field for the owner's mobile (the phone step's "change number"): same format and limit.
        bindMobileInput: function (input) { bindPhoneInput(input, "waslaChangePhoneBound"); },
        landlineExample: landlineExample,
        normalizeForSubmit: normalizeForSubmit,
        refreshExistingValues: refreshExistingValues,
        isBusinessMobileSelected: isBusinessMobileSelected
    };

    runWhenReady(init);
    window.addEventListener("pageshow", init);
})(typeof window !== "undefined" ? window : null);
