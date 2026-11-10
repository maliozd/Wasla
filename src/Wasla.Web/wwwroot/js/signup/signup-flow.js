// The signup flow on one page (WAS-194, Figma "02 · Claude — Kayıt ve Ödeme") over the registration draft API
// (WAS-192, docs/architecture/signup-registration.md): Plan, Restaurant, Account owner, Phone verification and Review.
// Each section step saves through its PUT before the next step opens; the page restores itself from the server's draft
// (embedded on render), so a reload, Back, Forward and Edit keep what the server holds. Forward moves first run the
// form's own validation as a convenience; the server validates every save and the submit again. Each step has a
// history entry. The receipt (desktop aside, mobile sheet and strip) follows every change. Localized text comes from
// the page's configuration or from the server's localized error messages; nothing here decides prices, availability
// or verification.
(function () {
    "use strict";

    var Steps = window.WaslaSignupSteps;
    var Receipt = window.WaslaSignupReceipt;
    var Review = window.WaslaSignupReview;
    var Draft = window.WaslaSignupDraft;
    var Api = window.WaslaSignupApi;
    var shell = document.querySelector("[data-su-root]");
    var form = document.getElementById("signupForm");
    if (!Steps || !Receipt || !Review || !Draft || !Api || !shell || !form) {
        return;
    }

    function readJson(id) {
        var element = document.getElementById(id);
        try {
            return element ? JSON.parse(element.textContent || "null") : null;
        } catch (e) {
            return null;
        }
    }

    var config = readJson("signupFormConfig") || {};
    var draft = readJson("signupDraftState") || { exists: false, phone: { state: "notSent" } };
    var messages = config.messages || {};
    var flow = config.flow || {};
    var panels = {};
    Array.prototype.forEach.call(shell.querySelectorAll("[data-su-step]"), function (panel) {
        panels[panel.getAttribute("data-su-step")] = panel;
    });
    var order = (flow.order || []).filter(function (id) { return !!panels[id]; });
    if (order.length === 0) {
        return;
    }

    var SECTIONS = ["plan", "restaurant", "owner"];
    var ADDRESS_RESTORE_TIMEOUT_MS = 15000;
    var state = Steps.create(order, Draft.currentStep(draft), Draft.completedSteps(draft));
    var baseTitle = document.title;
    var strip = shell.querySelector("[data-su-strip]");
    var stripAction = strip ? strip.querySelector("[data-su-strip-action]") : null;
    var submitButton = document.getElementById("signupSubmitButton");
    var pageAlert = shell.querySelector("[data-su-page-alert]");
    var busy = false;
    var submitting = false;
    var phone = null;
    // The saved address being put back after a load, one lookup after another. It stays set when its attempt ran out of
    // time (running is null then), so the next Continue can try again.
    var addressRestore = null;
    // The one Continue waiting for it; going back or a page brought back from the cache forgets it.
    var addressWait = null;
    var tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    var api = Api.create({
        endpoints: readJson("signupEndpoints") || {},
        token: function () { return tokenInput ? tokenInput.value : ""; }
    });
    var sheet = window.WaslaSignupSheet
        ? window.WaslaSignupSheet.init({
            dialog: shell.querySelector(".su-sheet"),
            trigger: strip ? strip.querySelector("[data-su-sheet-open]") : null
        })
        : null;

    if (window.WaslaSignupViewport) {
        window.WaslaSignupViewport.init({ shell: shell, strip: strip });
    }

    // ---------------------------------------------------------------- Field access

    function field(name) {
        return form.elements.namedItem(name);
    }

    function valueOf(name) {
        var element = field(name);
        return element && typeof element.value === "string" ? element.value : "";
    }

    function byId(id) {
        return document.getElementById(id);
    }

    function setValue(id, value) {
        var element = byId(id);
        if (element) {
            element.value = value == null ? "" : String(value);
        }
    }

    function checkRadio(name, value) {
        Array.prototype.forEach.call(form.querySelectorAll('input[name="' + name + '"]'), function (input) {
            input.checked = value != null && input.value === value;
        });
    }

    function labelFor(input) {
        var label = input && input.id ? form.querySelector('label[for="' + input.id + '"]') : null;
        return label ? label.textContent.trim() : "";
    }

    function radioLabel(name, value) {
        return labelFor(form.querySelector('input[name="' + name + '"][value="' + value + '"]'));
    }

    function selectedOptionText(id) {
        var select = byId(id);
        if (!select || !select.value || select.selectedIndex < 0) {
            return "";
        }
        return select.options[select.selectedIndex].text.trim();
    }

    function phoneDigits(value, mobile) {
        var helper = window.WaslaSignupPhone;
        if (mobile && helper) {
            return helper.normalizeTurkishLocalDigits(value);
        }
        return String(value || "").replace(/\D/g, "").slice(0, 50);
    }

    // ---------------------------------------------------------------- Draft → form

    // previous: the attempt that ran out of time, for a retry. streetEdited / neighborhoodEdited: the visitor changed the
    // street / the neighbourhood since the first attempt began, so no attempt writes the saved one over it; a changed
    // neighbourhood also keeps the saved street out, while the city and district still come back (WAS-212).
    function startAddressRestore(values, previous) {
        var controller = new AbortController();
        var attempt = {
            values: values,
            controller: controller,
            retry: !!previous,
            streetEdited: !!(previous && previous.streetEdited),
            neighborhoodEdited: !!(previous && previous.neighborhoodEdited),
            timedOut: false,
            running: null
        };
        var timer = setTimeout(function () {
            attempt.timedOut = true;
            controller.abort();
        }, ADDRESS_RESTORE_TIMEOUT_MS);
        var settled = function () {
            clearTimeout(timer);
            attempt.running = null;
            if (addressRestore === attempt && !attempt.timedOut) {
                addressRestore = null;
            }
            renderReceipt();
        };
        addressRestore = attempt;
        attempt.running = window.WaslaSignupForm.restoreAddress(values, {
            signal: controller.signal,
            retry: attempt.retry,
            keepStreet: function () { return attempt.streetEdited; },
            keepNeighborhood: function () { return attempt.neighborhoodEdited; }
        }).then(settled, settled);
        return attempt;
    }

    // The visitor chose another city or district, from the first attempt on (WAS-212): the saved address no longer
    // applies, its lookups are cancelled, and nothing is put back over the new choice.
    function abandonAddressRestore() {
        var attempt = addressRestore;
        if (!attempt) {
            return;
        }
        addressRestore = null;
        attempt.controller.abort();
        hidePageAlert();
    }

    function forgetAddressWait() {
        if (addressWait) {
            addressWait = null;
            setBusy(false);
        }
    }

    function restoreForm(source) {
        var values = Draft.formValues(source);
        var plan = Draft.initialPlan(source, config.requestedPlanCode, config.selfServicePlans);
        if (plan) {
            checkRadio("PlanCode", plan);
        }
        if (values.plan.billingPeriod) {
            checkRadio("BillingPeriod", values.plan.billingPeriod);
        }

        if (!source || !source.exists) {
            return;
        }

        var restaurant = values.restaurant;
        setValue("signupBusinessName", restaurant.businessName);
        var types = byId("signupBusinessTypes");
        if (types && restaurant.businessTypeCodes.length) {
            Array.prototype.forEach.call(types.querySelectorAll('input[name="SelectedBusinessTypeCodes"]'), function (input) {
                input.checked = restaurant.businessTypeCodes.indexOf(input.value) >= 0;
            });
            Array.prototype.forEach.call(types.querySelectorAll("[data-category-toggle]"), function (toggle) {
                var code = toggle.getAttribute("data-category-toggle");
                toggle.checked = !!types.querySelector('input[data-category="' + code + '"]:checked');
                toggle.dispatchEvent(new Event("change", { bubbles: true }));
            });
        }
        if (restaurant.businessPhoneType) {
            checkRadio("BusinessPhoneType", restaurant.businessPhoneType);
        }
        setValue("signupBusinessPhone", Draft.formatMobile(restaurant.businessPhone));
        setValue("signupBusinessEmail", restaurant.businessEmail);
        if (restaurant.slug) {
            var slug = byId("signupSlug");
            slug.value = restaurant.slug;
            // Marks the address as chosen, so typing the business name no longer rewrites it.
            slug.dispatchEvent(new Event("input", { bubbles: true }));
        }
        if (restaurant.country) {
            setValue("signupCountry", restaurant.country);
        }
        setValue("signupPostalCode", restaurant.postalCode);
        setValue("signupBuildingNumber", restaurant.buildingNumber);
        setValue("signupFloor", restaurant.floor);
        setValue("signupDoorNumber", restaurant.doorNumber);
        setValue("signupAddressNote", restaurant.addressNote);
        setValue("signupLocationUrl", restaurant.locationUrl);
        if (restaurant.addressNote || restaurant.locationUrl) {
            var more = shell.querySelector(".su-disclosure");
            if (more) {
                more.open = true;
            }
        }
        if (window.WaslaSignupForm && restaurant.cityId != null) {
            startAddressRestore({
                cityId: restaurant.cityId,
                districtId: restaurant.districtId,
                neighborhoodId: restaurant.neighborhoodId,
                neighborhood: restaurant.neighborhood,
                streetId: restaurant.streetId,
                streetAddress: restaurant.streetAddress
            }, null);
        } else {
            setValue("signupNeighborhood", restaurant.neighborhood);
            setValue("signupStreetAddress", restaurant.streetAddress);
        }

        setValue("signupOwnerFullName", values.owner.fullName);
        setValue("signupOwnerEmail", values.owner.email);
        setValue("signupOwnerPhone", values.owner.mobilePhone);
        if (window.WaslaSignupPhone) {
            window.WaslaSignupPhone.refreshExistingValues();
        }
    }

    function passwordKeptHint() {
        var hint = shell.querySelector("[data-su-password-kept]");
        if (hint) {
            hint.hidden = !(draft.owner && draft.owner.passwordSet);
        }
    }

    // ---------------------------------------------------------------- Form → requests

    function restaurantValues() {
        var mobile = valueOf("BusinessPhoneType") === "Mobile";
        return {
            businessName: valueOf("BusinessName"),
            businessTypeCodes: Array.prototype.map.call(
                form.querySelectorAll('input[name="SelectedBusinessTypeCodes"]:checked'), function (input) { return input.value; }),
            businessPhoneType: valueOf("BusinessPhoneType"),
            businessPhone: phoneDigits(valueOf("BusinessPhone"), mobile),
            businessEmail: valueOf("BusinessEmail"),
            slug: valueOf("Slug").trim().toLowerCase(),
            country: valueOf("Country"),
            cityId: valueOf("CityId"),
            districtId: valueOf("DistrictId"),
            neighborhoodId: valueOf("NeighborhoodId"),
            streetId: valueOf("StreetId"),
            neighborhood: valueOf("Neighborhood"),
            streetAddress: valueOf("StreetAddress"),
            buildingNumber: valueOf("BuildingNumber"),
            floor: valueOf("Floor"),
            doorNumber: valueOf("DoorNumber"),
            addressNote: valueOf("AddressNote"),
            postalCode: valueOf("PostalCode"),
            locationUrl: valueOf("LocationUrl")
        };
    }

    function ownerValues() {
        return {
            fullName: valueOf("OwnerFullName"),
            email: valueOf("OwnerEmail"),
            mobilePhone: phoneDigits(valueOf("OwnerPhone"), true),
            password: valueOf("Password"),
            confirmPassword: valueOf("ConfirmPassword")
        };
    }

    function saveSection(id) {
        if (id === "plan") {
            return api.savePlan(Draft.planRequest({ planCode: valueOf("PlanCode"), billingPeriod: valueOf("BillingPeriod") }));
        }
        if (id === "restaurant") {
            return api.saveRestaurant(Draft.restaurantRequest(restaurantValues()));
        }
        return api.saveOwner(Draft.ownerRequest(ownerValues()));
    }

    // ---------------------------------------------------------------- Receipt, strip, payment card

    function isYearly() {
        return valueOf("BillingPeriod") === "Yearly";
    }

    function selectedPlan() {
        return Receipt.findPlan(config.plans, valueOf("PlanCode"));
    }

    function phoneVerified() {
        return !!(draft.phone && draft.phone.state === "verified");
    }

    function fullOwnerPhone() {
        var digits = draft.owner && draft.owner.mobilePhone;
        return digits ? (config.defaultCountryCode || "+90") + " " + Draft.formatMobile(digits) : "";
    }

    function receiptValues() {
        var plan = selectedPlan();
        var yearly = isYearly();
        var view = Receipt.planView(plan, yearly, messages);
        var period = plan ? (yearly ? plan.yearly : plan.monthly) || {} : {};
        var periodWord = yearly ? messages.periodYearly : messages.periodMonthly;
        var verified = phoneVerified();

        return {
            view: view,
            values: {
                planLine: view.planLine,
                priceLine: view.priceLine,
                amount: view.amount,
                vat: view.vat,
                periodNote: view.periodNote,
                stripDetail: view.stripDetail,
                businessName: valueOf("BusinessName").trim(),
                domain: Receipt.domainOf(valueOf("Slug"), config.baseDomain),
                ownerName: valueOf("OwnerFullName").trim(),
                ownerEmail: valueOf("OwnerEmail").trim(),
                phoneLine: verified ? fullOwnerPhone() : ((draft.phone && draft.phone.maskedPhone) || ""),
                phoneState: verified ? messages.phoneVerified : "",
                payPlanRow: plan ? Receipt.format(messages.payPlanRow, [view.name, periodWord]) : "",
                payAmount: plan ? view.amount : "",
                payVat: view.amountSet && typeof period.vatIncluded === "boolean"
                    ? (period.vatIncluded ? messages.payVatIncluded : messages.payVatExcluded)
                    : "",
                payPeriod: plan ? (yearly ? messages.payPeriodYearly : messages.payPeriodMonthly) : "",
                payRenewal: plan ? (yearly ? messages.payRenewYearly : messages.payRenewMonthly) : ""
            }
        };
    }

    function renderReceipt() {
        var receipt = receiptValues();
        Receipt.bindText([shell], receipt.values);

        Array.prototype.forEach.call(shell.querySelectorAll("[data-su-amount]"), function (element) {
            var unsetClass = element.classList.contains("su-strip__amount") ? "su-strip__amount--unset" : "su-receipt__amount--unset";
            element.classList.toggle(unsetClass, !receipt.view.amountSet);
        });

        var statusOf = function (id) { return Steps.statusOf(state, id); };
        Array.prototype.forEach.call(shell.querySelectorAll("[data-su-receipt]"), function (receiptElement) {
            Receipt.renderSteps(receiptElement, statusOf, {
                done: messages.stateDone,
                current: messages.stateCurrent,
                upcoming: messages.stateUpcoming
            });
        });

        Array.prototype.forEach.call(shell.querySelectorAll("[data-su-progress]"), function (segment) {
            segment.setAttribute("data-state", statusOf(segment.getAttribute("data-su-progress")));
        });

        shell.setAttribute("data-su-period-shown", isYearly() ? "Yearly" : "Monthly");
    }

    // ---------------------------------------------------------------- Review (from the server's draft)

    function setReview(key, text, emptyText) {
        Array.prototype.forEach.call(shell.querySelectorAll('[data-su-review="' + key + '"]'), function (element) {
            var value = typeof text === "string" ? text.trim() : "";
            element.textContent = value || emptyText || "";
            if (!value && emptyText) {
                element.setAttribute("data-empty", "");
            } else {
                element.removeAttribute("data-empty");
            }
        });
    }

    function businessTypesText(codes) {
        var root = byId("signupBusinessTypes");
        if (!root || !codes || !codes.length) {
            return "";
        }
        var categories = [];
        var byCategory = {};
        codes.forEach(function (code) {
            var input = root.querySelector('input[name="SelectedBusinessTypeCodes"][value="' + code + '"]');
            if (!input) {
                return;
            }
            var category = input.getAttribute("data-category");
            if (!byCategory[category]) {
                byCategory[category] = { label: labelFor(root.querySelector('[data-category-toggle="' + category + '"]')), subtypes: [] };
                categories.push(byCategory[category]);
            }
            byCategory[category].subtypes.push(labelFor(input));
        });
        return Review.businessTypes(categories, messages.reviewBusinessTypeLine);
    }

    function fillReview() {
        var plan = draft.plan || {};
        var restaurant = draft.restaurant || {};
        var owner = draft.owner || {};
        var price = draft.price;
        var planDef = Receipt.findPlan(config.plans, plan.planCode);
        // The number in groups ("216 330 45 67"), isolated left to right so the "+90" stays in front in Arabic.
        var businessPhone = Review.phone(Draft.formatMobile(restaurant.businessPhone), config.defaultCountryCode);
        var phoneType = restaurant.businessPhoneType ? radioLabel("BusinessPhoneType", restaurant.businessPhoneType) : "";
        if (businessPhone) {
            businessPhone = "\u2066" + businessPhone + "\u2069";
        }

        setReview("plan", planDef ? planDef.name : (plan.planCode || ""));
        setReview("period", plan.billingPeriod ? radioLabel("BillingPeriod", plan.billingPeriod) : "");
        setReview("price", price ? (price.vatText ? Receipt.format(messages.priceLine, [price.priceText, price.vatText]) : price.priceText) : "");
        setReview("businessName", restaurant.businessName || "");
        setReview("businessTypes", businessTypesText(restaurant.businessTypeCodes));
        setReview("domain", Receipt.domainOf(restaurant.slug, config.baseDomain));
        setReview("businessPhone", businessPhone && phoneType ? businessPhone + " · " + phoneType : businessPhone);
        setReview("businessEmail", restaurant.businessEmail || "", messages.reviewNotProvided);
        setReview("address", Review.composeAddress({
            street: restaurant.streetAddress,
            building: restaurant.buildingNumber,
            floor: restaurant.floor,
            door: restaurant.doorNumber,
            neighborhood: restaurant.neighborhood,
            postalCode: restaurant.postalCode,
            district: restaurant.district || selectedOptionText("signupDistrictId"),
            city: restaurant.city || selectedOptionText("signupCityId")
        }, {
            building: messages.reviewBuilding,
            floor: messages.reviewFloor,
            door: messages.reviewDoor
        }));
        setReview("ownerName", owner.fullName || "");
        setReview("ownerEmail", owner.email || "");
        setReview("ownerPhone", fullOwnerPhone());
        setReview("password", owner.passwordSet ? messages.reviewPasswordSet : "");
        Array.prototype.forEach.call(shell.querySelectorAll("[data-su-review-verified]"), function (badge) {
            badge.hidden = !phoneVerified();
        });
    }

    // ---------------------------------------------------------------- Messages

    function showPageAlert(text, action) {
        if (!pageAlert) {
            return null;
        }
        pageAlert.querySelector("[data-su-page-alert-text]").textContent = text || "";
        var link = pageAlert.querySelector("[data-su-page-alert-link]");
        if (link) {
            link.hidden = !action;
            if (action) {
                link.textContent = action.text;
                link.href = action.href;
            }
        }
        pageAlert.hidden = false;
        return pageAlert;
    }

    function hidePageAlert() {
        if (pageAlert) {
            pageAlert.hidden = true;
        }
    }

    function reloadAction() {
        return { text: messages.reload, href: window.location.pathname + window.location.search };
    }

    // An answer that is not about a field: the request could not be checked or the draft is gone. Returns the
    // element to focus.
    function presentFailure(result) {
        if (!result || result.ok) {
            return null;
        }
        if (result.kind === "antiforgery" || result.kind === "closed") {
            return showPageAlert(messages.invalidRequest, reloadAction());
        }
        if (result.kind === "network") {
            return showPageAlert(messages.networkError, null);
        }
        if (result.kind !== "api") {
            return showPageAlert(messages.serverError, null);
        }

        var error = result.error || {};
        if (error.code === "Signup.Draft.Missing") {
            return showPageAlert(error.message, { text: messages.startAgain, href: window.location.pathname });
        }
        if (error.code === "Signup.Draft.Submitted") {
            if (error.registrationUrl) {
                window.location.assign(error.registrationUrl);
                return null;
            }
            return showPageAlert(error.message, null);
        }
        return showPageAlert(error.message || messages.serverError, null);
    }

    // ---------------------------------------------------------------- Field errors

    function validator() {
        var $ = window.jQuery;
        if (!$ || !$.fn || typeof $.fn.validate !== "function") {
            return null;
        }
        return $(form).data("validator") || $(form).validate();
    }

    function messageFor(element) {
        var span = element.name ? form.querySelector('[data-valmsg-for="' + element.name + '"]') : null;
        var text = span ? span.textContent.trim() : "";
        return text || element.validationMessage || "";
    }

    function validatableFields(panel) {
        var seen = {};
        return Array.prototype.filter.call(panel.querySelectorAll("input, select, textarea"), function (element) {
            var type = (element.getAttribute("type") || "").toLowerCase();
            if (!element.name || element.disabled || type === "hidden" || type === "button" || type === "submit"
                || element.name === "SelectedBusinessTypeCodes" || element.closest("[data-su-skip-validation]")) {
                return false;
            }
            if (type === "radio") {
                if (seen[element.name]) {
                    return false;
                }
                seen[element.name] = true;
            }
            return true;
        });
    }

    function sortByPage(errors) {
        return errors.sort(function (a, b) {
            return a.element.compareDocumentPosition(b.element) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1;
        });
    }

    function setFieldError(key, message) {
        var span = form.querySelector('[data-su-error-for="' + key + '"]');
        var input = form.querySelector('[data-su-field="' + key + '"]');
        if (span) {
            span.textContent = message || "";
            span.className = message ? "field-validation-error" : "field-validation-valid";
            if (message) {
                span.setAttribute("data-su-server-error", "");
            } else {
                span.removeAttribute("data-su-server-error");
            }
        }
        if (input) {
            if (message) {
                input.setAttribute("aria-invalid", "true");
            } else {
                input.removeAttribute("aria-invalid");
            }
        }
        return input;
    }

    function clearServerErrors(panel) {
        if (!panel) {
            return;
        }
        Array.prototype.forEach.call(panel.querySelectorAll("[data-su-server-error]"), function (span) {
            setFieldError(span.getAttribute("data-su-error-for"), "");
        });
    }

    // The page's own checks of a step (the model's validation attributes): a convenience before the save.
    function validateStep(id) {
        var panel = panels[id];
        var errors = [];
        if (!panel || SECTIONS.indexOf(id) < 0) {
            return errors;
        }

        var instance = validator();
        var keepPassword = id === "owner" && Draft.passwordOptional(draft, valueOf("Password"), valueOf("ConfirmPassword"));
        validatableFields(panel).forEach(function (element) {
            if (keepPassword && (element.name === "Password" || element.name === "ConfirmPassword")) {
                return;
            }
            var valid = instance ? instance.element(element) !== false : element.checkValidity();
            if (!valid) {
                errors.push({ element: element, message: messageFor(element) });
            }
        });

        if (id === "plan" && !valueOf("PlanCode")) {
            var firstPlan = setFieldError("plan.planCode", messages.planRequired);
            if (firstPlan) {
                errors.unshift({ element: firstPlan, message: messages.planRequired || "" });
            }
        }

        if (id === "restaurant" && window.WaslaSignupBusinessTypes) {
            var types = window.WaslaSignupBusinessTypes.check(document);
            if (!types.ok && types.focusTarget) {
                errors.push({ element: types.focusTarget, message: types.message });
            }
        }

        return sortByPage(errors);
    }

    // The server's field errors of one step, shown at their fields. Returns them as { element, message }.
    function showFieldErrors(step, fieldErrors) {
        var panel = panels[step];
        clearServerErrors(panel);
        var heading = panel ? panel.querySelector(".su-title") : null;
        return sortByPage((fieldErrors || []).map(function (error) {
            var input = setFieldError(error.section + "." + error.field, error.message);
            return { element: input || heading, message: error.message };
        }).filter(function (error) { return !!error.element; }));
    }

    function scrollIntoView(element, block) {
        if (element && typeof element.scrollIntoView === "function") {
            element.scrollIntoView({ block: block || "center", inline: "nearest" });
        }
    }

    // One error: focus its field (its message is in its description). Several: a summary of links, focused.
    function presentErrors(id, errors) {
        var panel = panels[id];
        var summary = panel ? panel.querySelector("[data-su-error-summary]") : null;
        if (!summary || errors.length < 2) {
            if (summary) {
                summary.hidden = true;
            }
            return errors.length ? errors[0].element : null;
        }

        var list = summary.querySelector("ul");
        list.textContent = "";
        errors.forEach(function (error) {
            var item = document.createElement("li");
            var link = document.createElement("a");
            link.href = "#" + error.element.id;
            link.textContent = error.message || labelFor(error.element) || error.element.name;
            link.setAttribute("data-su-error-link", error.element.id);
            item.appendChild(link);
            list.appendChild(item);
        });
        summary.hidden = false;
        return summary;
    }

    function hideErrorSummary(id) {
        var summary = panels[id] ? panels[id].querySelector("[data-su-error-summary]") : null;
        if (summary) {
            summary.hidden = true;
        }
    }

    function focusElement(element) {
        if (!element) {
            return;
        }
        scrollIntoView(element, element.matches("[data-su-error-summary], .su-title, [data-su-page-alert], .su-notice") ? "start" : "center");
        element.focus({ preventScroll: true });
    }

    // ---------------------------------------------------------------- Draft state

    function syncCompletion() {
        var done = Draft.completedSteps(draft);
        order.forEach(function (id) {
            if (id !== "review") {
                state = Steps.setComplete(state, id, done.indexOf(id) >= 0);
            }
        });
    }

    // keepPhone: the phone step's own state is newer than the phone state in this draft read.
    function applyDraft(next, options) {
        if (!next) {
            return;
        }
        var keepPhone = !!phone && !!(options && options.keepPhone);
        if (keepPhone) {
            next.phone = phone.status();
        }
        draft = next;
        syncCompletion();
        passwordKeptHint();
        if (phone) {
            phone.setFullNumber(fullOwnerPhone());
            if (!keepPhone) {
                phone.apply(draft.phone || { state: "notSent" });
            }
        }
        if (state.current === "review") {
            fillReview();
        }
        renderReceipt();
    }

    // A phone request started or answered while this read was out (a new number's code being sent, say) leaves a newer
    // phone state than the one the read carries, so that state is kept.
    function refreshDraft() {
        var phoneRevision = phone ? phone.revision() : 0;
        return api.getDraft().then(function (result) {
            if (result.ok) {
                applyDraft(result.data, { keepPhone: !!phone && phone.revision() !== phoneRevision });
            }
            return result;
        });
    }

    // ---------------------------------------------------------------- Showing a step

    function stepUrl(id) {
        return Steps.urlForStep(window.location.href, id);
    }

    function show(id, options) {
        options = options || {};
        var previous = state.current;
        state = Steps.goTo(state, id);
        var current = state.current;

        order.forEach(function (stepId) {
            panels[stepId].hidden = stepId !== current;
        });
        shell.setAttribute("data-su-current", current);

        var panel = panels[current];
        if (stripAction) {
            var carries = panel.hasAttribute("data-su-strip-carries");
            stripAction.hidden = !carries;
            stripAction.textContent = carries ? (panel.getAttribute("data-su-strip-label") || "") : "";
        }

        var heading = panel.querySelector(".su-title");
        if (heading) {
            document.title = heading.textContent.trim() + " - " + baseTitle;
        }

        if (previous === "phone" && current !== "phone" && phone) {
            phone.leave();
        }
        if (current === "review") {
            fillReview();
        }
        renderReceipt();

        if (options.history === "push") {
            window.history.pushState({ suStep: current }, "", stepUrl(current));
        } else if (options.history === "replace") {
            window.history.replaceState({ suStep: current }, "", stepUrl(current));
        } else if (options.history === "mark") {
            window.history.replaceState({ suStep: current }, "");
        }

        return heading;
    }

    function setBusy(on) {
        busy = on;
        // Cleared on every step: the visitor may have gone back while the move was waiting.
        var scope = on ? panels[state.current] : shell;
        var buttons = [stripAction].concat(scope ? Array.prototype.slice.call(scope.querySelectorAll("[data-su-next]")) : []);
        buttons.forEach(function (button) {
            if (!button) {
                return;
            }
            if (on) {
                button.setAttribute("aria-busy", "true");
            } else {
                button.removeAttribute("aria-busy");
            }
        });
    }

    // What happens on arriving at a step; resolves to an element to focus instead of the heading, if any.
    function enterStep(id, options) {
        if (id === "phone" && phone) {
            return phone.enter({ autoSend: !!(options && options.autoSend) }).then(function () {
                return phone.focusTarget();
            });
        }
        return Promise.resolve(null);
    }

    // Moves to a step. A forward move from a section first checks it on the page, then saves it; the server's answer
    // decides what is complete. Resolves to the element that should get focus.
    function navigate(target, options) {
        options = options || {};
        if (!target || !panels[target]) {
            return Promise.resolve(null);
        }

        var from = state.current;
        var forward = order.indexOf(target) > order.indexOf(from);
        if (!forward || SECTIONS.indexOf(from) < 0) {
            if (target === state.current) {
                return Promise.resolve(panels[target].querySelector(".su-title"));
            }
            var heading = show(target, { history: "push" });
            return enterStep(state.current, options).then(function (focus) { return focus || heading; });
        }

        // Until the saved address is back its fields are empty, not missing: the move goes on from there, so the first
        // click counts and nothing the draft already holds is reported as left out. The wait is limited; after it the
        // visitor is told, a late answer moves nothing, and Continue tries again.
        if (from === "restaurant" && addressRestore) {
            var attempt = addressRestore.running ? addressRestore : startAddressRestore(addressRestore.values, addressRestore);
            hidePageAlert();
            var wait = addressWait = {};
            setBusy(true);
            return attempt.running.then(function () {
                if (addressWait !== wait) {
                    return null;
                }
                addressWait = null;
                setBusy(false);
                if (attempt.timedOut) {
                    return showPageAlert(messages.networkError, null);
                }
                return attempt.controller.signal.aborted ? null : navigate(target, options);
            });
        }

        var errors = validateStep(from);
        if (errors.length) {
            return Promise.resolve(presentErrors(from, errors));
        }

        setBusy(true);
        return saveSection(from).then(function (result) {
            setBusy(false);
            if (result.ok) {
                hidePageAlert();
                hideErrorSummary(from);
                clearServerErrors(panels[from]);
                if (from === "owner") {
                    setValue("signupPassword", "");
                    setValue("signupConfirmPassword", "");
                    updatePasswordRules();
                }
                applyDraft(result.data);
                var opened = show(target, { history: "push" });
                return enterStep(state.current, options).then(function (focus) { return focus || opened; });
            }

            if (result.kind === "api" && result.error && result.error.fieldErrors && result.error.fieldErrors.length) {
                var groups = Draft.groupFieldErrors(result.error.fieldErrors);
                var step = Draft.firstStepOf(Object.keys(groups)) || from;
                if (step !== state.current) {
                    show(step, { history: "push" });
                }
                return presentErrors(step, showFieldErrors(step, groups[step]));
            }
            return presentFailure(result);
        });
    }

    function navigateAndFocus(target, options) {
        if (busy) {
            return;
        }
        var fromSheet = options && options.fromSheet;
        navigate(target, options).then(function (focusTarget) {
            if (fromSheet && sheet && sheet.isOpen && sheet.isOpen()) {
                sheet.close(focusTarget || undefined);
                if (focusTarget) {
                    scrollIntoView(focusTarget, "start");
                }
                return;
            }
            focusElement(focusTarget);
        });
    }

    // ---------------------------------------------------------------- Submit

    function setSubmitBusy(on) {
        [submitButton, stripAction].forEach(function (button) {
            if (!button) {
                return;
            }
            if (on) {
                button.setAttribute("aria-busy", "true");
            } else {
                button.removeAttribute("aria-busy");
            }
        });
    }

    function submitDraft() {
        if (submitting || busy) {
            return;
        }
        submitting = true;
        setSubmitBusy(true);

        api.submit().then(function (result) {
            if (result.ok && result.data && result.data.redirectUrl) {
                window.location.assign(result.data.redirectUrl);
                return;
            }

            submitting = false;
            setSubmitBusy(false);

            var error = result.kind === "api" ? (result.error || {}) : null;
            if (!error) {
                focusElement(presentFailure(result));
                return;
            }

            if (error.code === "Signup.SlugUnavailable") {
                // Figma S10: the address was taken meanwhile. Back to the restaurant step; everything else is kept.
                state = Steps.setComplete(state, "restaurant", false);
                show("restaurant", { history: "push" });
                var notice = panels.restaurant.querySelector("[data-su-address-taken]");
                if (notice) {
                    notice.querySelector("[data-su-address-taken-text]").textContent =
                        Receipt.format(messages.addressTakenBody, [Receipt.domainOf(valueOf("Slug"), config.baseDomain)]);
                    notice.hidden = false;
                }
                var slugErrors = showFieldErrors("restaurant", (error.fieldErrors || []).length
                    ? error.fieldErrors
                    : [{ section: "restaurant", field: "slug", code: error.code, message: error.message }]);
                focusElement(slugErrors.length ? slugErrors[0].element : byId("signupSlug"));
                return;
            }

            if (error.code === "Signup.Draft.Incomplete" || error.code === "Signup.Draft.ValidationFailed") {
                var groups = Draft.groupFieldErrors(error.fieldErrors);
                var step = Draft.firstStepOf(Object.keys(groups)) || Draft.firstStepOf(error.missingSections);
                if (step) {
                    (error.missingSections || []).forEach(function (section) {
                        state = Steps.setComplete(state, section, false);
                    });
                    if (groups[step]) {
                        state = Steps.setComplete(state, step, false);
                    }
                    show(step, { history: "push" });
                    var shown = groups[step] ? showFieldErrors(step, groups[step]) : [];
                    showPageAlert(error.message, null);
                    focusElement(shown.length ? presentErrors(step, shown) : pageAlert);
                    return;
                }
            }

            if (error.code === "Signup.Phone.NotVerified") {
                state = Steps.setComplete(state, "phone", false);
                show("phone", { history: "push" });
                showPageAlert(error.message, null);
                refreshDraft();
                focusElement(pageAlert);
                return;
            }

            focusElement(presentFailure(result));
        });
    }

    // ---------------------------------------------------------------- Password rules (the model's own rules, shown live)

    function updatePasswordRules() {
        var list = shell.querySelector("[data-su-password-rules]");
        if (!list) {
            return;
        }

        var password = valueOf("Password");
        var confirm = valueOf("ConfirmPassword");
        var rules = {
            length: password.length >= 8 ? "met" : "neutral",
            match: confirm.length === 0 ? "neutral" : (confirm === password ? "met" : "unmet")
        };
        var icons = { neutral: "bi-circle", met: "bi-check-circle", unmet: "bi-x-circle" };

        Object.keys(rules).forEach(function (rule) {
            var item = list.querySelector('[data-su-rule="' + rule + '"]');
            if (!item) {
                return;
            }
            var ruleState = rules[rule];
            item.setAttribute("data-state", ruleState);
            var icon = item.querySelector(".bi");
            if (icon) {
                icon.className = "bi " + icons[ruleState];
            }
            var stateText = item.querySelector("[data-su-rule-state]");
            if (stateText) {
                stateText.textContent = ruleState === "met"
                    ? ", " + (messages.passwordRuleMet || "")
                    : ruleState === "unmet" ? ", " + (messages.passwordRuleUnmet || "") : "";
            }
        });
    }

    // ---------------------------------------------------------------- Phone step

    phone = window.WaslaSignupPhoneStep && panels.phone
        ? window.WaslaSignupPhoneStep.init({
            root: panels.phone,
            api: api,
            messages: config.phoneMessages || {},
            codeLength: config.codeLength || 6,
            status: draft.phone || { state: "notSent" },
            fullNumber: fullOwnerPhone(),
            currentNumber: function () { return valueOf("OwnerPhone"); },
            hooks: {
                onStatus: function (status) {
                    draft.phone = status;
                    var verified = !!status && status.state === "verified";
                    state = Steps.setComplete(state, "phone", verified && !!(draft.owner && draft.owner.complete));
                    renderReceipt();
                },
                onVerified: function () {
                    hidePageAlert();
                    navigateAndFocus("review");
                },
                onOwnerIncomplete: function (error) {
                    state = Steps.setComplete(state, "owner", false);
                    show("owner", { history: "push" });
                    focusElement(showPageAlert(error.message, null));
                },
                onNumberChanged: function (number) {
                    // The accepted number is the owner's number from now on; the draft read below confirms it.
                    if (draft.owner) {
                        draft.owner.mobilePhone = phoneDigits(number, true);
                        setValue("signupOwnerPhone", Draft.formatMobile(draft.owner.mobilePhone));
                        phone.setFullNumber(fullOwnerPhone());
                    }
                    refreshDraft().then(function () {
                        setValue("signupOwnerPhone", Draft.formatMobile(draft.owner && draft.owner.mobilePhone));
                    });
                },
                onRequestFailed: function (result) {
                    focusElement(presentFailure(result));
                }
            }
        })
        : null;

    // ---------------------------------------------------------------- Panel address check (advisory)

    var slugInput = byId("signupSlug");
    var slugStatus = byId("signupSlugStatus");

    function renderSlugStatus(result) {
        if (!slugStatus) {
            return;
        }
        var message = slugStatus.querySelector("[data-su-slug-message]");
        var preview = slugStatus.querySelector("[data-su-slug-preview]");
        var text = "";
        var stateName = result.state;
        if (stateName === "checking") {
            text = messages.slugChecking;
        } else if (stateName === "available") {
            text = Receipt.format(messages.slugAvailable, [Receipt.domainOf(result.slug, config.baseDomain)]);
        } else if (stateName === "taken") {
            text = messages.slugTaken;
        } else if (stateName === "invalid") {
            text = messages.slugInvalid;
        } else if (stateName === "error") {
            text = messages.slugCheckFailed;
        } else {
            stateName = "preview";
        }
        slugStatus.setAttribute("data-state", stateName);
        if (message) {
            message.textContent = text;
            message.hidden = stateName === "preview";
        }
        if (preview) {
            preview.hidden = stateName !== "preview";
        }
    }

    var addressCheck = window.WaslaSignupAddressCheck && slugInput
        ? window.WaslaSignupAddressCheck.create({
            check: function (slug, signal) {
                return api.checkAddress(slug, signal).then(function (result) {
                    if (result.kind === "aborted") {
                        throw new Error("aborted");
                    }
                    if (!result.ok || !result.data) {
                        return { state: "error" };
                    }
                    // An answer for another spelling than the one in the field is not shown.
                    if (result.data.slug !== slug) {
                        return { state: "idle" };
                    }
                    return { state: result.data.status };
                });
            },
            onState: renderSlugStatus
        })
        : null;

    if (addressCheck) {
        slugInput.addEventListener("input", function () {
            addressCheck.input(slugInput.value.trim().toLowerCase());
        });
        slugInput.addEventListener("blur", function () {
            addressCheck.flush();
        });
        var businessName = byId("signupBusinessName");
        if (businessName) {
            // The address follows the business name until the visitor edits it (signup-form.js); check what it shows.
            businessName.addEventListener("input", function () {
                addressCheck.input(slugInput.value.trim().toLowerCase());
            });
        }
    }

    // ---------------------------------------------------------------- Events

    shell.addEventListener("click", function (event) {
        var target = event.target;
        if (!target || !target.closest) {
            return;
        }

        var errorLink = target.closest("[data-su-error-link]");
        if (errorLink) {
            event.preventDefault();
            focusElement(byId(errorLink.getAttribute("data-su-error-link")));
            return;
        }

        var next = target.closest("[data-su-next]");
        if (next) {
            event.preventDefault();
            navigateAndFocus(Steps.next(state), { autoSend: next.hasAttribute("data-su-autosend") });
            return;
        }

        // The form's only submit button, so Enter in a field "clicks" it (implicit submission): on the review step it
        // sends the registration, on any other step it continues that step.
        if (target.closest("[data-su-submit]")) {
            event.preventDefault();
            continueOpenStep();
            return;
        }

        var go = target.closest("[data-su-go]");
        if (go) {
            event.preventDefault();
            navigateAndFocus(go.getAttribute("data-su-go"), { fromSheet: !!go.closest(".su-sheet") });
            return;
        }

        if (stripAction && target.closest("[data-su-strip-action]")) {
            event.preventDefault();
            if (state.current === "review") {
                submitDraft();
                return;
            }
            var stepNext = panels[state.current].querySelector("[data-su-next]");
            if (stepNext) {
                stepNext.click();
            }
        }
    });

    form.addEventListener("input", function (event) {
        var target = event.target;
        var name = target && target.name;
        if (name === "Password" || name === "ConfirmPassword") {
            updatePasswordRules();
        }
        if (name === "StreetAddress" && addressRestore) {
            addressRestore.streetEdited = true;
        }
        // A typed neighbourhood leaves the city and district it did not touch to the restore, which keeps going.
        if (name === "Neighborhood" && addressRestore) {
            addressRestore.neighborhoodEdited = true;
        }
        if (name === "CityId" || name === "DistrictId") {
            abandonAddressRestore();
        }
        var key = target && target.getAttribute && target.getAttribute("data-su-field");
        if (key && form.querySelector('[data-su-error-for="' + key + '"][data-su-server-error]')) {
            setFieldError(key, "");
        }
        if (target && target.id === "signupSlug" && panels.restaurant) {
            var taken = panels.restaurant.querySelector("[data-su-address-taken]");
            if (taken) {
                taken.hidden = true;
            }
        }
        renderReceipt();
    });

    form.addEventListener("change", function () {
        renderReceipt();
    });

    function continueOpenStep() {
        if (state.current === "review") {
            submitDraft();
        } else if (state.current === "phone") {
            if (phone) {
                phone.primaryAction();
            }
        } else {
            var stepNext = panels[state.current].querySelector("[data-su-next]");
            navigateAndFocus(Steps.next(state), { autoSend: !!(stepNext && stepNext.hasAttribute("data-su-autosend")) });
        }
    }

    // Nothing posts this form: Enter continues the open step, the review step submits the draft.
    form.addEventListener("submit", function (event) {
        event.preventDefault();
        event.stopImmediatePropagation();
        continueOpenStep();
    }, true);

    window.addEventListener("pageshow", function (event) {
        if (event.persisted) {
            submitting = false;
            addressWait = null;
            setBusy(false);
            setSubmitBusy(false);
        }
    });

    window.addEventListener("popstate", function (event) {
        var requested = event.state && event.state.suStep;
        if (!requested || !panels[requested]) {
            return;
        }
        forgetAddressWait();
        if (sheet && sheet.isOpen && sheet.isOpen()) {
            sheet.close();
        }
        var heading = show(requested);
        if (state.current !== requested) {
            window.history.replaceState({ suStep: state.current }, "", stepUrl(state.current));
        }
        enterStep(state.current, {}).then(function (focus) {
            focusElement(focus || heading);
        });
    });

    // ---------------------------------------------------------------- Start

    function showToday() {
        if (typeof Intl === "undefined" || !Intl.DateTimeFormat) {
            return;
        }
        var now = new Date();
        var text;
        try {
            text = new Intl.DateTimeFormat(document.documentElement.lang || undefined, {
                day: "numeric", month: "long", year: "numeric", calendar: "gregory"
            }).format(now);
        } catch (e) {
            return;
        }
        var iso = now.getFullYear() + "-" + String(now.getMonth() + 1).padStart(2, "0") + "-" + String(now.getDate()).padStart(2, "0");
        Array.prototype.forEach.call(shell.querySelectorAll("[data-su-today]"), function (element) {
            element.textContent = text;
            element.setAttribute("datetime", iso);
            element.hidden = false;
        });
    }

    restoreForm(draft);
    passwordKeptHint();
    showToday();
    updatePasswordRules();

    if (draft.submitted) {
        showPageAlert(messages.submitted, draft.registrationUrl ? { text: messages.openRegistration, href: draft.registrationUrl } : null);
    }

    // A step named in the address opens when it may (a reload or a shared link); otherwise where the draft continues.
    var requested = Steps.readStepFromUrl(window.location.href, order);
    show(requested || state.current, { history: "mark" });
    if (requested && requested !== state.current) {
        window.history.replaceState({ suStep: state.current }, "", stepUrl(state.current));
    }
    if (state.current === "phone" && phone) {
        phone.enter({ autoSend: false });
    }
})();
