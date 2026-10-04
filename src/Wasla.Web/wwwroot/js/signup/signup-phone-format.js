(function (window) {
    "use strict";

    var MOBILE_TYPE = "Mobile";

    function runWhenReady(callback) {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", callback);
        } else {
            callback();
        }
    }

    function stripPhoneDigits(value) {
        return (value || "").replace(/\D/g, "");
    }

    function normalizeTurkishLocalDigits(value) {
        var digits = stripPhoneDigits(value);

        if (digits.length >= 12 && digits.indexOf("90") === 0) {
            digits = digits.slice(2);
        } else if (digits.length === 11 && digits.charAt(0) === "0") {
            digits = digits.slice(1);
        } else if (digits.length > 10 && digits.indexOf("90") === 0) {
            digits = digits.slice(2);
        }

        return digits.slice(0, 10);
    }

    function formatTurkishMobileDigits(digits) {
        digits = digits.slice(0, 10);
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
            parts.push(digits.slice(8, 10));
        }

        return parts.join(" ");
    }

    function formatMobileInput(input) {
        if (!input) {
            return;
        }

        var formatted = formatTurkishMobileDigits(normalizeTurkishLocalDigits(input.value));
        if (input.value !== formatted) {
            input.value = formatted;
        }
    }

    function getPhoneTypeSelect() {
        return document.getElementById("signupBusinessPhoneType");
    }

    function getBusinessPhoneInput() {
        return document.getElementById("signupBusinessPhone");
    }

    function getOwnerPhoneInput() {
        return document.getElementById("signupOwnerPhone");
    }

    function isBusinessMobileSelected(phoneTypeSelect) {
        return phoneTypeSelect && phoneTypeSelect.value === MOBILE_TYPE;
    }

    function bindPasteFormatter(input, shouldFormat) {
        input.addEventListener("paste", function (event) {
            if (typeof shouldFormat === "function" && !shouldFormat()) {
                return;
            }

            event.preventDefault();
            var clipboard = event.clipboardData || window.clipboardData;
            var pasted = clipboard ? clipboard.getData("text") : "";
            input.value = pasted;
            formatMobileInput(input);
        });
    }

    function bindOwnerPhoneInput(input) {
        if (!input || input.dataset.waslaOwnerPhoneBound === "true") {
            return;
        }

        input.dataset.waslaOwnerPhoneBound = "true";

        input.addEventListener("input", function () {
            formatMobileInput(input);
        });
        input.addEventListener("blur", function () {
            formatMobileInput(input);
        });
        bindPasteFormatter(input, function () {
            return true;
        });

        formatMobileInput(input);
    }

    function bindBusinessPhoneInput(businessInput, phoneTypeSelect) {
        if (!businessInput || !phoneTypeSelect || businessInput.dataset.waslaBusinessPhoneBound === "true") {
            return;
        }

        businessInput.dataset.waslaBusinessPhoneBound = "true";

        function handleBusinessInput() {
            if (isBusinessMobileSelected(phoneTypeSelect)) {
                formatMobileInput(businessInput);
            }
        }

        businessInput.addEventListener("input", handleBusinessInput);
        businessInput.addEventListener("blur", handleBusinessInput);
        bindPasteFormatter(businessInput, function () {
            return isBusinessMobileSelected(phoneTypeSelect);
        });

        if (phoneTypeSelect.dataset.waslaPhoneTypeBound !== "true") {
            phoneTypeSelect.dataset.waslaPhoneTypeBound = "true";
            phoneTypeSelect.addEventListener("change", function () {
                if (isBusinessMobileSelected(phoneTypeSelect)) {
                    formatMobileInput(businessInput);
                }
            });
        }

        if (isBusinessMobileSelected(phoneTypeSelect)) {
            formatMobileInput(businessInput);
        }
    }

    function normalizeForSubmit() {
        var phoneTypeSelect = getPhoneTypeSelect();
        var businessInput = getBusinessPhoneInput();
        var ownerInput = getOwnerPhoneInput();

        if (businessInput) {
            if (isBusinessMobileSelected(phoneTypeSelect)) {
                businessInput.value = normalizeTurkishLocalDigits(businessInput.value);
            } else {
                businessInput.value = stripPhoneDigits(businessInput.value).slice(0, 50);
            }
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

    function refreshExistingValues() {
        formatMobileInput(getOwnerPhoneInput());

        var phoneTypeSelect = getPhoneTypeSelect();
        if (isBusinessMobileSelected(phoneTypeSelect)) {
            formatMobileInput(getBusinessPhoneInput());
        }
    }

    function init() {
        bindOwnerPhoneInput(getOwnerPhoneInput());
        bindBusinessPhoneInput(getBusinessPhoneInput(), getPhoneTypeSelect());
        bindSubmitNormalization();
        refreshExistingValues();
        window.setTimeout(refreshExistingValues, 0);
    }

    window.WaslaSignupPhone = {
        init: init,
        formatMobileInput: formatMobileInput,
        normalizeTurkishLocalDigits: normalizeTurkishLocalDigits,
        normalizeForSubmit: normalizeForSubmit,
        refreshExistingValues: refreshExistingValues,
        isBusinessMobileSelected: function () {
            return isBusinessMobileSelected(getPhoneTypeSelect());
        }
    };

    runWhenReady(init);
    window.addEventListener("pageshow", init);
})(window);
