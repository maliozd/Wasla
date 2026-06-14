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

    function readConfig() {
        var el = document.getElementById("signupFormConfig");
        if (!el) return {};
        try {
            return JSON.parse(el.textContent || "{}");
        } catch {
            return {};
        }
    }

    var config = readConfig();
    var slugInput = document.getElementById("signupSlug");
    var businessNameInput = document.getElementById("signupBusinessName");
    var preview = document.getElementById("signupDomainPreview");
    var planSelect = document.getElementById("signupPlanCode");
    var billingSelect = document.getElementById("signupBillingPeriod");
    var enterpriseNotice = document.getElementById("signupEnterpriseNotice");
    var selfServiceSections = document.getElementById("signupSelfServiceSections");
    var billingCol = document.getElementById("signupBillingCol");
    var baseDomainEl = document.getElementById("signupBaseDomain");
    var baseDomain = baseDomainEl ? baseDomainEl.value : "";
    var slugManuallyEdited = false;
    var initialSlug = slugInput ? slugInput.value : "";
    var citySelect = document.getElementById("signupCityId");
    var districtSelect = document.getElementById("signupDistrictId");
    var phoneTypeSelect = document.getElementById("signupBusinessPhoneType");
    var phonePrefix = document.getElementById("signupPhonePrefix");
    var phoneHint = document.getElementById("signupPhoneHint");
    var citiesById = {};

    if (Array.isArray(config.cities)) {
        config.cities.forEach(function (city) {
            citiesById[city.id] = city;
        });
    }

    if (slugInput && initialSlug.trim().length > 0) {
        slugManuallyEdited = true;
    }

    function updateDomainPreview() {
        if (!slugInput || !preview) return;
        var slug = (slugInput.value || "your-name").trim().toLowerCase() || "your-name";
        preview.textContent = slug + "." + baseDomain;
    }

    function findSelectedPlan() {
        if (!planSelect || !Array.isArray(config.plans)) return null;
        var code = planSelect.value;
        for (var i = 0; i < config.plans.length; i++) {
            if (config.plans[i].code === code) {
                return config.plans[i];
            }
        }
        return config.plans[0] || null;
    }

    function updatePlanSummary() {
        var plan = findSelectedPlan();
        var nameEl = document.getElementById("signupPlanSummaryName");
        var descriptionEl = document.getElementById("signupPlanSummaryDescription");
        var priceEl = document.getElementById("signupPlanSummaryPrice");
        var yearlyNoteEl = document.getElementById("signupPlanSummaryYearlyNote");
        var deviceEl = document.getElementById("signupPlanSummaryDevice");
        var featuresEl = document.getElementById("signupPlanSummaryFeatures");

        if (!plan || !nameEl || !descriptionEl || !priceEl || !deviceEl || !featuresEl) {
            return;
        }

        var isYearly = billingSelect && billingSelect.value === "Yearly";
        nameEl.textContent = plan.packageTitle || "";
        descriptionEl.textContent = plan.description || "";
        priceEl.textContent = isYearly ? (plan.yearlyPrice || plan.monthlyPrice || "") : (plan.monthlyPrice || "");

        if (yearlyNoteEl) {
            yearlyNoteEl.classList.toggle("d-none", !(isYearly && plan.showYearlyNote));
        }

        deviceEl.textContent = plan.deviceLimit || "";
        featuresEl.innerHTML = "";
        (plan.features || []).forEach(function (feature) {
            var item = document.createElement("li");
            item.textContent = feature;
            featuresEl.appendChild(item);
        });
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
        updatePlanSummary();
    }

    function setDistrictDisabled(disabled) {
        if (!districtSelect) return;
        districtSelect.disabled = disabled;
        if (disabled) {
            districtSelect.value = "";
        }
    }

    function populateDistrictOptions(districts, selectedId) {
        if (!districtSelect) return;
        districtSelect.innerHTML = "";
        var placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = config.selectDistrictText || "";
        districtSelect.appendChild(placeholder);

        districts.forEach(function (district) {
            var option = document.createElement("option");
            option.value = String(district.id);
            option.textContent = district.name;
            if (selectedId && String(selectedId) === String(district.id)) {
                option.selected = true;
            }
            districtSelect.appendChild(option);
        });
    }

    function loadDistricts(cityId, selectedDistrictId) {
        if (!districtSelect || !cityId) {
            setDistrictDisabled(true);
            return;
        }

        setDistrictDisabled(true);
        fetch((config.districtsUrl || "/signup/districts") + "?cityId=" + encodeURIComponent(cityId), {
            headers: { Accept: "application/json" }
        })
            .then(function (response) {
                if (!response.ok) throw new Error("districts");
                return response.json();
            })
            .then(function (districts) {
                populateDistrictOptions(districts, selectedDistrictId);
                setDistrictDisabled(false);
            })
            .catch(function () {
                populateDistrictOptions([], null);
                setDistrictDisabled(true);
            });
    }

    function getSelectedCity() {
        if (!citySelect || !citySelect.value) return null;
        return citiesById[Number(citySelect.value)] || null;
    }

    function updatePhoneUi() {
        if (!phoneTypeSelect) return;
        var isMobile = phoneTypeSelect.value === "Mobile";
        var city = getSelectedCity();

        if (phonePrefix) {
            phonePrefix.textContent = config.defaultCountryCode || "+90";
        }

        if (phoneHint) {
            if (isMobile) {
                phoneHint.textContent = config.mobileHint || "";
            } else if (city && city.phoneAreaCode) {
                phoneHint.textContent = (config.defaultCountryCode || "+90") + " " + city.phoneAreaCode;
            } else {
                phoneHint.textContent = "";
            }
        }
    }

    function initPasswordToggles() {
        document.querySelectorAll(".signup-password-toggle").forEach(function (button) {
            button.addEventListener("click", function () {
                var targetId = button.getAttribute("data-target");
                var input = targetId ? document.getElementById(targetId) : null;
                if (!input) return;

                var isPassword = input.type === "password";
                input.type = isPassword ? "text" : "password";
                var icon = button.querySelector("i");
                if (icon) {
                    icon.classList.toggle("bi-eye", !isPassword);
                    icon.classList.toggle("bi-eye-slash", isPassword);
                }

                var label = isPassword ? (config.hidePasswordText || "") : (config.showPasswordText || "");
                button.setAttribute("aria-label", label);
                button.setAttribute("title", label);
            });
        });
    }

    if (planSelect) {
        planSelect.addEventListener("change", updatePlanUi);
        updatePlanUi();
    }

    if (billingSelect) {
        billingSelect.addEventListener("change", updatePlanSummary);
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

    if (citySelect) {
        var initialDistrictId = districtSelect ? districtSelect.value : "";
        citySelect.addEventListener("change", function () {
            loadDistricts(citySelect.value, null);
            updatePhoneUi();
        });

        if (citySelect.value) {
            if (!districtSelect || districtSelect.options.length <= 1) {
                loadDistricts(citySelect.value, initialDistrictId);
            } else {
                setDistrictDisabled(false);
            }
        } else {
            setDistrictDisabled(true);
        }
    }

    if (phoneTypeSelect) {
        phoneTypeSelect.addEventListener("change", updatePhoneUi);
    }

    initPasswordToggles();
    updateDomainPreview();
    updatePhoneUi();
})();
