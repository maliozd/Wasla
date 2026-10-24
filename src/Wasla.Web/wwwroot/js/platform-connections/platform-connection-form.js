(function () {
    "use strict";

    function isTrendyolPlatform(value) {
        return String(value || "").toLowerCase() === "trendyolyemek";
    }

    function updateTrendyolFields() {
        const select = document.getElementById("platformSelect");
        if (!select) return;

        const show = isTrendyolPlatform(select.value);

        document.querySelectorAll(".trendyol-only").forEach(function (el) {
            el.classList.toggle("d-none", !show);
        });

        const help = document.getElementById("trendyolStoreIdHelp");
        if (help) {
            help.classList.toggle("d-none", !show);
        }
    }

    /**
     * Shows or masks one credential field. Only the field's type changes: the server never puts a credential in the
     * field, so the only value this can show is one typed into this form. The control's localized name stays the same
     * ("Show API key"); aria-pressed carries whether the value is shown.
     */
    function setRevealed(toggle, input, revealed) {
        input.type = revealed ? "text" : "password";
        toggle.setAttribute("aria-pressed", revealed ? "true" : "false");
        const icon = toggle.querySelector(".bi");
        if (icon) icon.className = revealed ? "bi bi-eye-slash" : "bi bi-eye";
    }

    function credentialToggles() {
        return Array.prototype.map.call(document.querySelectorAll("[data-credential-toggle]"), function (toggle) {
            return { toggle: toggle, input: document.getElementById(toggle.getAttribute("aria-controls")) };
        }).filter(function (pair) {
            return pair.input !== null;
        });
    }

    function maskAll() {
        credentialToggles().forEach(function (pair) {
            setRevealed(pair.toggle, pair.input, false);
        });
    }

    function bindCredentialToggles() {
        const pairs = credentialToggles();
        pairs.forEach(function (pair) {
            // type="button" in the markup: the control never submits the form, by click or by keyboard.
            pair.toggle.addEventListener("click", function () {
                setRevealed(pair.toggle, pair.input, pair.input.type === "password");
            });
            // Hidden until this script runs, so the page never offers a control that does nothing.
            pair.toggle.hidden = false;
        });
        if (pairs.length === 0) return;

        // A revealed value is a plain text field: mask it again before the form is sent (so the browser does not keep
        // it as form history) and before the page goes into the back/forward cache.
        document.querySelectorAll("form").forEach(function (form) {
            form.addEventListener("submit", maskAll);
        });
        window.addEventListener("pagehide", maskAll);
    }

    document.addEventListener("DOMContentLoaded", function () {
        bindCredentialToggles();

        const select = document.getElementById("platformSelect");
        if (!select) return;

        select.addEventListener("change", updateTrendyolFields);
        updateTrendyolFields();
    });
})();
