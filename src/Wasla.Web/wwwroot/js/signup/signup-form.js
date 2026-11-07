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
    var planInputs = document.querySelectorAll('input[name="PlanCode"]');
    var enterpriseNotice = document.getElementById("signupEnterpriseNotice");
    var baseDomainEl = document.getElementById("signupBaseDomain");
    var baseDomain = baseDomainEl ? baseDomainEl.value : "";
    var slugManuallyEdited = false;
    var initialSlug = slugInput ? slugInput.value : "";
    var citySelect = document.getElementById("signupCityId");
    var districtSelect = document.getElementById("signupDistrictId");
    var neighborhoodInput = document.getElementById("signupNeighborhood");
    var neighborhoodIdInput = document.getElementById("signupNeighborhoodId");
    var neighborhoodList = document.getElementById("signupNeighborhoodList");
    var neighborhoodHint = document.getElementById("signupNeighborhoodHint");
    var streetInput = document.getElementById("signupStreetAddress");
    var streetIdInput = document.getElementById("signupStreetId");
    var streetList = document.getElementById("signupStreetList");
    var streetHint = document.getElementById("signupStreetHint");
    var phoneTypeInputs = document.querySelectorAll('input[name="BusinessPhoneType"]');
    var phonePrefix = document.getElementById("signupPhonePrefix");
    var phoneHint = document.getElementById("signupPhoneHint");
    var citiesById = {};
    var neighborhoodsCache = [];
    var streetsCache = [];

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

    // The plan list (WAS-194) has only the self-service plans; the Enterprise notice shown for ?plan=Enterprise goes
    // away once a plan is chosen. The receipt and the prices follow the selection in signup-flow.js.
    function updatePlanUi() {
        if (!enterpriseNotice) return;
        var chosen = Array.prototype.some.call(planInputs, function (input) { return input.checked; });
        if (chosen) {
            enterpriseNotice.hidden = true;
        }
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
            clearNeighborhoodFields();
            clearStreetFields();
            return;
        }

        setDistrictDisabled(true);
        clearNeighborhoodFields();
        clearStreetFields();
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

    function clearNeighborhoodFields() {
        neighborhoodsCache = [];
        if (neighborhoodList) neighborhoodList.innerHTML = "";
        if (neighborhoodInput) neighborhoodInput.value = "";
        if (neighborhoodIdInput) neighborhoodIdInput.value = "";
        if (neighborhoodHint) neighborhoodHint.textContent = config.neighborhoodManualHint || "";
    }

    function clearStreetFields() {
        streetsCache = [];
        if (streetList) streetList.innerHTML = "";
        if (streetInput) streetInput.value = "";
        if (streetIdInput) streetIdInput.value = "";
        if (streetHint) streetHint.textContent = config.streetManualHint || "";
    }

    function populateDatalist(listElement, items, valueKey) {
        if (!listElement) return;
        listElement.innerHTML = "";
        items.forEach(function (item) {
            var option = document.createElement("option");
            option.value = item[valueKey];
            listElement.appendChild(option);
        });
    }

    function syncReferenceSelection(textValue, cache, idInput, nameKey) {
        if (!idInput) return;
        var normalized = (textValue || "").trim();
        if (!normalized) {
            idInput.value = "";
            return;
        }

        var match = null;
        for (var i = 0; i < cache.length; i++) {
            if (cache[i][nameKey] === normalized) {
                match = cache[i];
                break;
            }
        }

        idInput.value = match ? String(match.id) : "";
    }

    function loadNeighborhoods(districtId, selectedNeighborhoodId, selectedNeighborhoodName) {
        if (!districtId) {
            clearNeighborhoodFields();
            clearStreetFields();
            return;
        }

        clearNeighborhoodFields();
        clearStreetFields();

        fetch((config.neighborhoodsUrl || "/signup/neighborhoods") + "?districtId=" + encodeURIComponent(districtId), {
            headers: { Accept: "application/json" }
        })
            .then(function (response) {
                if (!response.ok) throw new Error("neighborhoods");
                return response.json();
            })
            .then(function (neighborhoods) {
                neighborhoodsCache = neighborhoods || [];
                populateDatalist(neighborhoodList, neighborhoodsCache, "name");

                if (neighborhoodHint) {
                    neighborhoodHint.textContent = neighborhoodsCache.length > 0
                        ? ""
                        : (config.neighborhoodManualHint || "");
                }

                if (selectedNeighborhoodId && neighborhoodIdInput) {
                    neighborhoodIdInput.value = String(selectedNeighborhoodId);
                }

                if (selectedNeighborhoodName && neighborhoodInput) {
                    neighborhoodInput.value = selectedNeighborhoodName;
                }

                if (neighborhoodIdInput && neighborhoodIdInput.value) {
                    loadStreets(neighborhoodIdInput.value, streetIdInput ? streetIdInput.value : null, streetInput ? streetInput.value : null);
                }
            })
            .catch(function () {
                neighborhoodsCache = [];
                if (neighborhoodHint) {
                    neighborhoodHint.textContent = config.neighborhoodManualHint || "";
                }
            });
    }

    function loadStreets(neighborhoodId, selectedStreetId, selectedStreetName) {
        if (!neighborhoodId) {
            clearStreetFields();
            return;
        }

        clearStreetFields();

        fetch((config.streetsUrl || "/signup/streets") + "?neighborhoodId=" + encodeURIComponent(neighborhoodId), {
            headers: { Accept: "application/json" }
        })
            .then(function (response) {
                if (!response.ok) throw new Error("streets");
                return response.json();
            })
            .then(function (streets) {
                streetsCache = (streets || []).map(function (street) {
                    return {
                        id: street.id,
                        name: street.displayName || street.name,
                        streetType: street.streetType
                    };
                });
                populateDatalist(streetList, streetsCache, "name");

                if (streetHint) {
                    streetHint.textContent = streetsCache.length > 0
                        ? ""
                        : (config.streetManualHint || "");
                }

                if (selectedStreetId && streetIdInput) {
                    streetIdInput.value = String(selectedStreetId);
                }

                if (selectedStreetName && streetInput) {
                    streetInput.value = selectedStreetName;
                }
            })
            .catch(function () {
                streetsCache = [];
                if (streetHint) {
                    streetHint.textContent = config.streetManualHint || "";
                }
            });
    }

    function getSelectedCity() {
        if (!citySelect || !citySelect.value) return null;
        return citiesById[Number(citySelect.value)] || null;
    }

    function currentPhoneType() {
        var checked = document.querySelector('input[name="BusinessPhoneType"]:checked');
        return checked ? checked.value : "";
    }

    function updatePhoneUi() {
        if (!phoneTypeInputs.length) return;
        var isMobile = currentPhoneType() === "Mobile";
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

    function localizeCompareValidation() {
        var confirmInput = document.getElementById("signupConfirmPassword");
        if (!confirmInput || !config.passwordMismatchText) {
            return;
        }

        confirmInput.setAttribute("data-val-compare", config.passwordMismatchText);
        var form = document.getElementById("signupForm");
        if (form && window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
            jQuery.validator.unobtrusive.parse(form);
        }
    }

    function initOptionalBusinessEmailValidation() {
        var emailInput = document.getElementById("signupBusinessEmail");
        if (!emailInput || !window.jQuery || !jQuery.validator) {
            return;
        }

        if (config.emailInvalidText) {
            emailInput.setAttribute("data-val-optionalemail", config.emailInvalidText);
        }

        if (!jQuery.validator.methods.optionalemail) {
            jQuery.validator.addMethod("optionalemail", function (value) {
                var trimmed = jQuery.trim(value);
                if (trimmed.length === 0) {
                    return true;
                }

                return jQuery.validator.methods.email.call(this, trimmed);
            }, config.emailInvalidText || "");
        }

        if (jQuery.validator.unobtrusive && !initOptionalBusinessEmailValidation.adapterRegistered) {
            jQuery.validator.unobtrusive.adapters.add("optionalemail", function (options) {
                options.rules.optionalemail = true;
                options.messages.optionalemail = options.message;
            });
            initOptionalBusinessEmailValidation.adapterRegistered = true;
        }

        function revalidateBusinessEmail() {
            var $input = jQuery(emailInput);
            var form = $input.closest("form");
            if (form.length && form.data("validator")) {
                $input.valid();
            }
        }

        emailInput.addEventListener("input", revalidateBusinessEmail);
        emailInput.addEventListener("blur", function () {
            emailInput.value = jQuery.trim(emailInput.value);
            revalidateBusinessEmail();
        });

        var form = document.getElementById("signupForm");
        if (form && jQuery.validator.unobtrusive) {
            jQuery.validator.unobtrusive.parse(form);
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

    Array.prototype.forEach.call(planInputs, function (input) {
        input.addEventListener("change", updatePlanUi);
    });
    updatePlanUi();

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
        var initialNeighborhoodId = neighborhoodIdInput ? neighborhoodIdInput.value : "";
        var initialNeighborhoodName = neighborhoodInput ? neighborhoodInput.value : "";
        var initialStreetId = streetIdInput ? streetIdInput.value : "";
        var initialStreetName = streetInput ? streetInput.value : "";

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

    if (districtSelect) {
        districtSelect.addEventListener("change", function () {
            loadNeighborhoods(districtSelect.value, null, null);
        });

        if (districtSelect.value) {
            loadNeighborhoods(
                districtSelect.value,
                neighborhoodIdInput ? neighborhoodIdInput.value : null,
                neighborhoodInput ? neighborhoodInput.value : null);
        } else if (neighborhoodHint) {
            neighborhoodHint.textContent = config.neighborhoodManualHint || "";
        }
    }

    if (neighborhoodInput) {
        neighborhoodInput.addEventListener("input", function () {
            syncReferenceSelection(neighborhoodInput.value, neighborhoodsCache, neighborhoodIdInput, "name");
            if (neighborhoodIdInput && neighborhoodIdInput.value) {
                loadStreets(neighborhoodIdInput.value, null, null);
            } else {
                clearStreetFields();
            }
        });

        neighborhoodInput.addEventListener("change", function () {
            syncReferenceSelection(neighborhoodInput.value, neighborhoodsCache, neighborhoodIdInput, "name");
            if (neighborhoodIdInput && neighborhoodIdInput.value) {
                loadStreets(neighborhoodIdInput.value, null, null);
            } else {
                clearStreetFields();
            }
        });
    }

    if (streetInput) {
        streetInput.addEventListener("input", function () {
            syncReferenceSelection(streetInput.value, streetsCache, streetIdInput, "name");
        });

        streetInput.addEventListener("change", function () {
            syncReferenceSelection(streetInput.value, streetsCache, streetIdInput, "name");
        });
    }

    Array.prototype.forEach.call(phoneTypeInputs, function (input) {
        input.addEventListener("change", function () {
            updatePhoneUi();
            if (window.WaslaSignupPhone) {
                window.WaslaSignupPhone.refreshExistingValues();
            }
        });
    });

    initPasswordToggles();
    initOptionalBusinessEmailValidation();
    if (window.WaslaSignupPhone) {
        window.WaslaSignupPhone.init();
    }
    localizeCompareValidation();
    updateDomainPreview();
    updatePhoneUi();
})();
