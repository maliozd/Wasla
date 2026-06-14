(function () {
    function toAsciiTurkish(value) {
        return (value || "").replace(/[çÇ]/g, "c")
            .replace(/[ğĞ]/g, "g")
            .replace(/[ı]/g, "i")
            .replace(/[İ]/g, "i")
            .replace(/[öÖ]/g, "o")
            .replace(/[şŞ]/g, "s")
            .replace(/[üÜ]/g, "u");
    }

    function generateSlugFromBusinessName(businessName) {
        var ascii = toAsciiTurkish(businessName).toLowerCase();
        var builder = "";
        var lastWasHyphen = false;

        for (var i = 0; i < ascii.length; i++) {
            var ch = ascii.charAt(i);
            if ((ch >= "a" && ch <= "z") || (ch >= "0" && ch <= "9")) {
                builder += ch;
                lastWasHyphen = false;
                continue;
            }

            if (/\s/.test(ch) || ch === "-" || ch === "_") {
                if (builder.length > 0 && !lastWasHyphen) {
                    builder += "-";
                    lastWasHyphen = true;
                }
            }
        }

        return builder.replace(/^-+|-+$/g, "");
    }

    var slugInput = document.getElementById("signupSlug");
    var businessNameInput = document.getElementById("signupBusinessName");
    var preview = document.getElementById("signupDomainPreview");
    var planSelect = document.getElementById("signupPlanCode");
    var enterpriseNotice = document.getElementById("signupEnterpriseNotice");
    var selfServiceSections = document.getElementById("signupSelfServiceSections");
    var billingCol = document.getElementById("signupBillingCol");
    var baseDomainEl = document.getElementById("signupBaseDomain");
    var baseDomain = baseDomainEl ? baseDomainEl.value : "";
    var slugManuallyEdited = false;
    var initialSlug = slugInput ? slugInput.value : "";

    if (slugInput && initialSlug.trim().length > 0) {
        slugManuallyEdited = true;
    }

    function updateDomainPreview() {
        if (!slugInput || !preview) return;
        var slug = (slugInput.value || "your-name").trim().toLowerCase() || "your-name";
        preview.textContent = slug + "." + baseDomain;
    }

    function updatePlanUi() {
        if (!planSelect) return;
        var option = planSelect.options[planSelect.selectedIndex];
        var isContactSales = option && option.getAttribute("data-contact-sales") === "true";
        if (enterpriseNotice) {
            enterpriseNotice.classList.toggle("d-none", !isContactSales);
        }
        if (selfServiceSections) {
            selfServiceSections.classList.toggle("d-none", isContactSales);
        }
        if (billingCol) {
            billingCol.classList.toggle("d-none", isContactSales);
        }
    }

    if (planSelect) {
        planSelect.addEventListener("change", updatePlanUi);
        updatePlanUi();
    }

    if (slugInput) {
        slugInput.addEventListener("input", function () {
            slugManuallyEdited = true;
            updateDomainPreview();
        });
    }

    if (businessNameInput && slugInput) {
        businessNameInput.addEventListener("input", function () {
            if (slugManuallyEdited) {
                return;
            }

            slugInput.value = generateSlugFromBusinessName(businessNameInput.value);
            updateDomainPreview();
        });
    }

    updateDomainPreview();
})();
