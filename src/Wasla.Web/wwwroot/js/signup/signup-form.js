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
    var neighborhoodHint = document.getElementById("signupNeighborhoodHint");
    var streetInput = document.getElementById("signupStreetAddress");
    var streetIdInput = document.getElementById("signupStreetId");
    var streetHint = document.getElementById("signupStreetHint");
    var cityCombo = null;
    var districtCombo = null;
    var neighborhoodCombo = null;
    var streetCombo = null;
    var phoneTypeInputs = document.querySelectorAll('input[name="BusinessPhoneType"]');
    var phonePrefix = document.getElementById("signupPhonePrefix");
    var phoneHint = document.getElementById("signupPhoneHint");
    var citiesById = {};
    var neighborhoodsCache = [];
    var streetsCache = [];
    // The neighbourhood whose streets the street list shows or is loading; empty when it shows none.
    var streetsNeighborhoodId = "";
    // The district list that failed to load, for its retry: the city it was for and the district to select again.
    var districtRetry = null;

    // The searchable address fields (signup-combobox.js). Without the script the native controls still work.
    var addressText = config.address || {};
    function attachCombo(rootId, mode, select, extra) {
        var rootEl = document.getElementById(rootId);
        if (!rootEl || !window.WaslaCombobox) {
            return null;
        }
        var messages = {
            search: addressText.search,
            loading: addressText.loading,
            noMatches: addressText.noMatches,
            empty: addressText.empty,
            noSuggestions: addressText.noSuggestions,
            useTyped: addressText.useTyped,
            results: addressText.results
        };
        Object.keys(extra.messages || {}).forEach(function (key) {
            messages[key] = extra.messages[key];
        });
        return window.WaslaCombobox.attach({
            root: rootEl,
            input: rootEl.querySelector("[data-su-combo-input]"),
            list: rootEl.querySelector('[role="listbox"]'),
            popup: rootEl.querySelector("[data-su-combo-popup]"),
            state: rootEl.querySelector("[data-su-combo-state]"),
            alert: rootEl.querySelector("[data-su-combo-alert]"),
            live: rootEl.querySelector("[data-su-combo-live]"),
            mode: mode,
            select: select,
            messages: messages,
            onRetry: extra.onRetry || null,
            avoidBottom: stripHeight
        });
    }

    // Below the desktop layout the signup's summary strip is fixed to the bottom of the screen.
    function stripHeight() {
        var strip = document.querySelector("[data-su-strip]");
        if (!strip) {
            return 0;
        }
        var style = window.getComputedStyle(strip);
        if (style.position !== "fixed" || style.display === "none" || strip.hidden) {
            return 0;
        }
        return Math.max(0, window.innerHeight - strip.getBoundingClientRect().top);
    }

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
        refreshCombo(districtCombo);
    }

    function refreshCombo(combo) {
        if (combo) {
            combo.refresh();
        }
    }

    function setComboStatus(combo, status) {
        if (combo) {
            combo.setStatus(status);
        }
    }

    function setComboItems(combo, items) {
        if (combo) {
            combo.setItems(items.map(function (item) {
                return { id: item.id, label: item.name };
            }));
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
        refreshCombo(districtCombo);
    }

    // Each level's answer is used only while its request is the newest of that level. A new city, district or
    // neighbourhood lookup turns the answers still out for its own level and the levels below into stale ones, so an
    // answer for an earlier choice never lands on a later one.
    var LOOKUP_LEVELS = ["districts", "neighborhoods", "streets"];
    var lookupRounds = { districts: 0, neighborhoods: 0, streets: 0 };

    // ownLevelOnly: a retry of the same choice. It replaces the failed answer of its level only, so the answers still
    // out for the levels below (the restore's neighbourhoods of that same district) stay current.
    function startLookup(level, ownLevelOnly) {
        var last = ownLevelOnly ? LOOKUP_LEVELS.indexOf(level) + 1 : LOOKUP_LEVELS.length;
        for (var i = LOOKUP_LEVELS.indexOf(level); i < last; i++) {
            lookupRounds[LOOKUP_LEVELS[i]]++;
        }
        var round = lookupRounds[level];
        return function () {
            return lookupRounds[level] === round;
        };
    }

    // options, from restoreAddress only. restoring: the fields are not emptied, so nothing the visitor entered meanwhile
    // is lost; signal: ends the restore's lookups; keepStreet() / keepNeighborhood(): the visitor changed the street /
    // the neighbourhood since the restore began.
    function answerApplies(isCurrent, options) {
        return isCurrent() && !(options.signal && options.signal.aborted);
    }

    function kept(options, key) {
        return !!(options[key] && options[key]());
    }

    // The saved street, unless the visitor changed it, or the neighbourhood it belongs to, since the restore began; then
    // only its id follows its text.
    function restoreStreet(streetId, streetName, options) {
        if (kept(options, "keepStreet") || kept(options, "keepNeighborhood")) {
            if (streetInput) {
                syncReferenceSelection(streetInput.value, streetsCache, streetIdInput, "name", streetCombo);
            }
            return;
        }
        if (streetId && streetIdInput) {
            streetIdInput.value = String(streetId);
        }
        if (streetName && streetInput) {
            streetInput.value = streetName;
        }
    }

    // Each load resolves to true while its answer still applies, so a restore goes on only with its own answers.
    // options.retry: the same city again after a failed list (see retryDistricts).
    function loadDistricts(cityId, selectedDistrictId, options) {
        options = options || {};
        var isCurrent = startLookup("districts", !!options.retry);
        districtRetry = null;
        if (!districtSelect || !cityId) {
            setComboStatus(districtCombo, "ready");
            setDistrictDisabled(true);
            clearNeighborhoodFields();
            clearStreetFields();
            return Promise.resolve(true);
        }

        setComboStatus(districtCombo, "loading");
        setDistrictDisabled(true);
        if (!options.restoring) {
            clearNeighborhoodFields();
            clearStreetFields();
        }
        return fetch((config.districtsUrl || "/signup/districts") + "?cityId=" + encodeURIComponent(cityId), {
            headers: { Accept: "application/json" },
            signal: options.signal
        })
            .then(function (response) {
                if (!response.ok) throw new Error("districts");
                return response.json();
            })
            .then(function (districts) {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                setComboStatus(districtCombo, "ready");
                populateDistrictOptions(districts, selectedDistrictId);
                setDistrictDisabled(false);
                return true;
            })
            .catch(function () {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                // The list did not load. The field stays usable and empty, so a save still asks for a district, and
                // it says so with a retry; nothing else the visitor entered is cleared.
                districtRetry = { cityId: String(cityId), selectedId: selectedDistrictId == null ? null : String(selectedDistrictId) };
                populateDistrictOptions([], null);
                setComboStatus(districtCombo, "error");
                setDistrictDisabled(false);
                return true;
            });
    }

    // Loads the failed district list again for the city still chosen, and selects the district that was wanted then
    // (the saved one during a restore). It replaces only the district answer, so the neighbourhood and street fields and
    // any of their answers still out keep belonging to that district.
    function retryDistricts() {
        var retry = districtRetry;
        if (!retry || !citySelect || citySelect.value !== retry.cityId) {
            return Promise.resolve(false);
        }
        return loadDistricts(retry.cityId, retry.selectedId, { restoring: true, retry: true });
    }

    // A retry keeps what the fields show: the lists come back, and each id follows its field's text again.
    function keepAll() {
        return true;
    }

    function retryNeighborhoods() {
        if (!districtSelect || !districtSelect.value) {
            setComboStatus(neighborhoodCombo, "ready");
            return Promise.resolve(false);
        }
        return loadNeighborhoods(districtSelect.value, null, null, null, null,
            { restoring: true, retry: true, keepNeighborhood: keepAll, keepStreet: keepAll });
    }

    function retryStreets() {
        if (!neighborhoodIdInput || !neighborhoodIdInput.value) {
            setComboStatus(streetCombo, "ready");
            return Promise.resolve(false);
        }
        return loadStreets(neighborhoodIdInput.value, null, null, { restoring: true, retry: true, keepStreet: keepAll });
    }

    function clearNeighborhoodFields() {
        neighborhoodsCache = [];
        setComboItems(neighborhoodCombo, []);
        setComboStatus(neighborhoodCombo, "ready");
        if (neighborhoodInput) neighborhoodInput.value = "";
        if (neighborhoodIdInput) neighborhoodIdInput.value = "";
        if (neighborhoodHint) neighborhoodHint.textContent = config.neighborhoodManualHint || "";
    }

    function clearStreetFields() {
        streetsNeighborhoodId = "";
        streetsCache = [];
        setComboItems(streetCombo, []);
        setComboStatus(streetCombo, "ready");
        if (streetInput) streetInput.value = "";
        if (streetIdInput) streetIdInput.value = "";
        if (streetHint) streetHint.textContent = config.streetManualHint || "";
    }

    // The id that goes with a neighbourhood or street text: the option the visitor picked while its name is still in the
    // field, otherwise the catalog entry with exactly that name, otherwise the one entry with that name in another case
    // or Turkish letter form (the list then offers no "keep as typed" row; the server saves the catalog's name with the
    // id), otherwise none, so typed text never keeps an id from an earlier place.
    function syncReferenceSelection(textValue, cache, idInput, nameKey, combo) {
        if (!idInput) return;
        var normalized = (textValue || "").trim();
        if (!normalized) {
            idInput.value = "";
            return;
        }

        var pickedId = combo ? combo.pickedId() : null;
        if (pickedId) {
            idInput.value = pickedId;
            return;
        }

        var match = null;
        for (var i = 0; i < cache.length; i++) {
            if (cache[i][nameKey] === normalized) {
                match = cache[i];
                break;
            }
        }

        var equivalent = !match && combo ? combo.equivalentId(normalized) : null;
        idInput.value = match ? String(match.id) : (equivalent || "");
    }

    // selectedStreetId / selectedStreetName: the street to restore after the neighbourhood's streets are loaded.
    function loadNeighborhoods(districtId, selectedNeighborhoodId, selectedNeighborhoodName, selectedStreetId, selectedStreetName, options) {
        options = options || {};
        var isCurrent = startLookup("neighborhoods");
        if (!districtId) {
            clearNeighborhoodFields();
            clearStreetFields();
            return Promise.resolve(true);
        }

        var streetIdToRestore = selectedStreetId === undefined ? null : selectedStreetId;
        var streetNameToRestore = selectedStreetName === undefined ? null : selectedStreetName;
        if (!options.restoring) {
            clearNeighborhoodFields();
            clearStreetFields();
        }
        setComboStatus(neighborhoodCombo, "loading");

        // The saved neighbourhood, unless the visitor typed one since the restore began; then only its id follows its text.
        function restoreNeighborhood() {
            if (kept(options, "keepNeighborhood")) {
                if (neighborhoodInput) {
                    syncReferenceSelection(neighborhoodInput.value, neighborhoodsCache, neighborhoodIdInput, "name", neighborhoodCombo);
                }
                return;
            }
            if (selectedNeighborhoodId && neighborhoodIdInput) {
                neighborhoodIdInput.value = String(selectedNeighborhoodId);
            }
            if (selectedNeighborhoodName && neighborhoodInput) {
                neighborhoodInput.value = selectedNeighborhoodName;
            }
        }

        return fetch((config.neighborhoodsUrl || "/signup/neighborhoods") + "?districtId=" + encodeURIComponent(districtId), {
            headers: { Accept: "application/json" },
            signal: options.signal
        })
            .then(function (response) {
                if (!response.ok) throw new Error("neighborhoods");
                return response.json();
            })
            .then(function (neighborhoods) {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                neighborhoodsCache = neighborhoods || [];
                setComboItems(neighborhoodCombo, neighborhoodsCache);
                setComboStatus(neighborhoodCombo, "ready");

                if (neighborhoodHint) {
                    neighborhoodHint.textContent = neighborhoodsCache.length > 0
                        ? ""
                        : (config.neighborhoodManualHint || "");
                }

                restoreNeighborhood();
                if (neighborhoodIdInput && neighborhoodIdInput.value) {
                    return loadStreets(neighborhoodIdInput.value, streetIdToRestore, streetNameToRestore, options);
                }
                restoreStreet(null, streetNameToRestore, options);
                return true;
            })
            .catch(function () {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                neighborhoodsCache = [];
                setComboItems(neighborhoodCombo, []);
                setComboStatus(neighborhoodCombo, "error");
                if (neighborhoodHint) {
                    neighborhoodHint.textContent = config.neighborhoodManualHint || "";
                }
                // The list only suggests names: without it the saved neighbourhood and street stay, with the ids the
                // server stored for them (WAS-211). A failed retry changes nothing either.
                if (options.restoring && !options.retry) {
                    restoreNeighborhood();
                    restoreStreet(neighborhoodIdInput && neighborhoodIdInput.value ? streetIdToRestore : null, streetNameToRestore, options);
                }
                return true;
            });
    }

    function loadStreets(neighborhoodId, selectedStreetId, selectedStreetName, options) {
        options = options || {};
        var isCurrent = startLookup("streets");
        if (!neighborhoodId) {
            clearStreetFields();
            return Promise.resolve(true);
        }

        if (!options.restoring) {
            clearStreetFields();
        }
        streetsNeighborhoodId = String(neighborhoodId);
        setComboStatus(streetCombo, "loading");

        return fetch((config.streetsUrl || "/signup/streets") + "?neighborhoodId=" + encodeURIComponent(neighborhoodId), {
            headers: { Accept: "application/json" },
            signal: options.signal
        })
            .then(function (response) {
                if (!response.ok) throw new Error("streets");
                return response.json();
            })
            .then(function (streets) {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                streetsCache = (streets || []).map(function (street) {
                    return {
                        id: street.id,
                        name: street.displayName || street.name,
                        streetType: street.streetType
                    };
                });
                setComboItems(streetCombo, streetsCache);
                setComboStatus(streetCombo, "ready");

                if (streetHint) {
                    streetHint.textContent = streetsCache.length > 0
                        ? ""
                        : (config.streetManualHint || "");
                }

                restoreStreet(selectedStreetId, selectedStreetName, options);
                return true;
            })
            .catch(function () {
                if (!answerApplies(isCurrent, options)) {
                    return false;
                }
                // Choosing the neighbourhood again loads its streets again.
                streetsNeighborhoodId = "";
                streetsCache = [];
                setComboItems(streetCombo, []);
                setComboStatus(streetCombo, "error");
                if (streetHint) {
                    streetHint.textContent = config.streetManualHint || "";
                }
                if (options.restoring && !options.retry) {
                    restoreStreet(selectedStreetId, selectedStreetName, options);
                }
                return true;
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

    // The localized landline example ("Örnek: {0}") with the number kept left to right, also in Arabic.
    function showLandlineHint(city) {
        var helper = window.WaslaSignupPhone;
        var example = helper ? helper.landlineExample(city && city.phoneAreaCode) : "";
        var parts = String(config.landlineHint || "{0}").split("{0}");
        phoneHint.textContent = "";
        phoneHint.appendChild(document.createTextNode(parts[0] || ""));
        var number = document.createElement("bdi");
        number.setAttribute("dir", "ltr");
        number.textContent = example;
        phoneHint.appendChild(number);
        phoneHint.appendChild(document.createTextNode(parts[1] || ""));
    }

    function updatePhoneUi() {
        if (!phoneTypeInputs.length) return;
        var type = currentPhoneType();

        if (phonePrefix) {
            phonePrefix.textContent = config.defaultCountryCode || "+90";
        }

        if (phoneHint) {
            if (type === "Mobile") {
                phoneHint.textContent = config.mobileHint || "";
            } else if (type === "Landline") {
                showLandlineHint(getSelectedCity());
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

    cityCombo = citySelect ? attachCombo("signupCityCombo", "select", citySelect, {}) : null;
    districtCombo = districtSelect ? attachCombo("signupDistrictCombo", "select", districtSelect, {
        messages: { failed: addressText.districtsFailed, failedShort: addressText.districtsFailedShort },
        onRetry: retryDistricts
    }) : null;
    neighborhoodCombo = neighborhoodInput ? attachCombo("signupNeighborhoodCombo", "suggest", null, {
        messages: { failed: addressText.neighborhoodsFailed, placeholder: addressText.neighborhoodPlaceholder },
        onRetry: retryNeighborhoods
    }) : null;
    streetCombo = streetInput ? attachCombo("signupStreetCombo", "suggest", null, {
        messages: { failed: addressText.streetsFailed, placeholder: addressText.streetPlaceholder },
        onRetry: retryStreets
    }) : null;

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

    // A changed neighbourhood takes its streets with it: its own when it is on the list, none otherwise. Either way a
    // street answer still out for the neighbourhood before is stale. Still the neighbourhood whose streets are shown or
    // on their way (the change event after the input events, or its name in another case) changes nothing: the street
    // and its list stay.
    function followNeighborhood() {
        syncReferenceSelection(neighborhoodInput.value, neighborhoodsCache, neighborhoodIdInput, "name", neighborhoodCombo);
        var id = neighborhoodIdInput ? neighborhoodIdInput.value : "";
        if (id && id === streetsNeighborhoodId) {
            return;
        }
        loadStreets(id, null, null);
    }

    if (neighborhoodInput) {
        neighborhoodInput.addEventListener("input", followNeighborhood);
        neighborhoodInput.addEventListener("change", followNeighborhood);
    }

    if (streetInput) {
        var followStreet = function () {
            syncReferenceSelection(streetInput.value, streetsCache, streetIdInput, "name", streetCombo);
        };
        streetInput.addEventListener("input", followStreet);
        streetInput.addEventListener("change", followStreet);
    }

    Array.prototype.forEach.call(phoneTypeInputs, function (input) {
        input.addEventListener("change", function () {
            updatePhoneUi();
            if (window.WaslaSignupPhone) {
                window.WaslaSignupPhone.refreshExistingValues();
            }
        });
    });

    // Puts a saved address back (WAS-194: the page restores the registration draft): the city, then its districts with
    // the saved one selected, then the neighbourhood and street lists with the saved values. No field is emptied on the
    // way, and nothing the visitor changed is written over: not a street (options.keepStreet), and not a neighbourhood
    // (options.keepNeighborhood), which also keeps the saved street out. The chain stops when a newer choice replaced its
    // lookup or options.signal ends it. A retry (options.retry) does not load the districts again once the saved one is
    // selected.
    function restoreAddress(values, options) {
        options = options || {};
        var restore = {
            restoring: true,
            signal: options.signal,
            keepStreet: options.keepStreet,
            keepNeighborhood: options.keepNeighborhood
        };
        if (!citySelect) {
            return Promise.resolve();
        }

        citySelect.value = values && values.cityId != null ? String(values.cityId) : "";
        refreshCombo(cityCombo);
        updatePhoneUi();
        if (!citySelect.value) {
            setDistrictDisabled(true);
            return Promise.resolve();
        }

        var districtsReady = options.retry && districtSelect && districtSelect.value === String(values.districtId)
            ? Promise.resolve(true)
            : loadDistricts(citySelect.value, values.districtId, restore);
        return districtsReady.then(function (applies) {
            if (!applies || !values.districtId || (restore.signal && restore.signal.aborted)) {
                return undefined;
            }
            return loadNeighborhoods(
                String(values.districtId),
                values.neighborhoodId,
                values.neighborhood || null,
                values.streetId,
                values.streetAddress || null,
                restore);
        });
    }

    window.WaslaSignupForm = {
        restoreAddress: restoreAddress,
        refreshDomainPreview: updateDomainPreview
    };

    initPasswordToggles();
    initOptionalBusinessEmailValidation();
    if (window.WaslaSignupPhone) {
        window.WaslaSignupPhone.init();
    }
    localizeCompareValidation();
    updateDomainPreview();
    updatePhoneUi();
})();
