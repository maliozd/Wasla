// The signup steps on one page (WAS-194, Figma "02 · Claude — Kayıt ve Ödeme"): Plan, Restaurant, Account owner and
// Review, each a section of the one signup form. Moving forward checks the open step with the form's own validation
// (the server's rules, rendered by the tag helpers) and the server checks everything again on submit. Each step has
// its own history entry, so the browser's Back and Forward move between steps. The receipt (desktop aside, mobile
// sheet and strip) follows every change. Localized text comes from the page's configuration; nothing here decides
// prices, availability or verification.
(function () {
    "use strict";

    var Steps = window.WaslaSignupSteps;
    var Receipt = window.WaslaSignupReceipt;
    var Review = window.WaslaSignupReview;
    var shell = document.querySelector("[data-su-root]");
    var form = document.getElementById("signupForm");
    if (!Steps || !Receipt || !Review || !shell || !form) {
        return;
    }

    function readConfig() {
        var element = document.getElementById("signupFormConfig");
        try {
            return element ? JSON.parse(element.textContent || "{}") : {};
        } catch (e) {
            return {};
        }
    }

    var config = readConfig();
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

    var state = Steps.create(order, flow.initial, flow.completed);
    var baseTitle = document.title;
    var strip = shell.querySelector("[data-su-strip]");
    var stripAction = strip ? strip.querySelector("[data-su-strip-action]") : null;
    var submitButton = document.getElementById("signupSubmitButton");
    var submitting = false;
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
        if (!element) {
            return "";
        }
        if (typeof element.length === "number" && !element.tagName) {
            // A radio group (RadioNodeList).
            return element.value || "";
        }
        return typeof element.value === "string" ? element.value : "";
    }

    function checkedLabel(name) {
        var checked = form.querySelector('input[name="' + name + '"]:checked');
        var label = checked && checked.id ? form.querySelector('label[for="' + checked.id + '"]') : null;
        return label ? label.textContent.trim() : "";
    }

    function selectedOptionText(id) {
        var select = document.getElementById(id);
        if (!select || !select.value || select.selectedIndex < 0) {
            return "";
        }
        return select.options[select.selectedIndex].text.trim();
    }

    // ---------------------------------------------------------------- Receipt, strip, payment card

    function isYearly() {
        return valueOf("BillingPeriod") === "Yearly";
    }

    function selectedPlan() {
        return Receipt.findPlan(config.plans, valueOf("PlanCode"));
    }

    function receiptValues() {
        var plan = selectedPlan();
        var yearly = isYearly();
        var view = Receipt.planView(plan, yearly, messages);
        var period = plan ? (yearly ? plan.yearly : plan.monthly) || {} : {};
        var periodWord = yearly ? messages.periodYearly : messages.periodMonthly;

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

    // ---------------------------------------------------------------- Review

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

    function fillReview() {
        var receipt = receiptValues();
        var plan = selectedPlan();
        var businessTypes = window.WaslaSignupBusinessTypes
            ? Review.businessTypes(window.WaslaSignupBusinessTypes.selectedLabels(document), messages.reviewBusinessTypeLine)
            : "";
        var businessPhone = Review.phone(valueOf("BusinessPhone"), config.defaultCountryCode);
        var phoneType = checkedLabel("BusinessPhoneType");

        setReview("plan", plan ? plan.name : "");
        setReview("period", checkedLabel("BillingPeriod"));
        setReview("price", receipt.values.priceLine);
        setReview("businessName", valueOf("BusinessName"));
        setReview("businessTypes", businessTypes);
        setReview("domain", receipt.values.domain);
        setReview("businessPhone", businessPhone && phoneType ? businessPhone + " · " + phoneType : businessPhone);
        setReview("businessEmail", valueOf("BusinessEmail"), messages.reviewNotProvided);
        setReview("address", Review.composeAddress({
            street: valueOf("StreetAddress"),
            building: valueOf("BuildingNumber"),
            floor: valueOf("Floor"),
            door: valueOf("DoorNumber"),
            neighborhood: valueOf("Neighborhood"),
            postalCode: valueOf("PostalCode"),
            district: selectedOptionText("signupDistrictId"),
            city: selectedOptionText("signupCityId")
        }, {
            building: messages.reviewBuilding,
            floor: messages.reviewFloor,
            door: messages.reviewDoor
        }));
        setReview("ownerName", valueOf("OwnerFullName"));
        setReview("ownerEmail", valueOf("OwnerEmail"));
        setReview("ownerPhone", Review.phone(valueOf("OwnerPhone"), config.defaultCountryCode), messages.reviewNotProvided);
        setReview("password", valueOf("Password") ? messages.reviewPasswordSet : "");
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

    // ---------------------------------------------------------------- Validation of one step

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
                || element.name === "SelectedBusinessTypeCodes") {
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

    // The invalid fields of a step, in page order: { element, message }.
    function validateStep(id) {
        var panel = panels[id];
        var errors = [];
        if (!panel) {
            return errors;
        }

        var instance = validator();
        validatableFields(panel).forEach(function (element) {
            var valid = instance ? instance.element(element) !== false : element.checkValidity();
            if (!valid) {
                errors.push({ element: element, message: messageFor(element) });
            }
        });

        if (id === "plan" && !valueOf("PlanCode")) {
            var firstPlan = form.querySelector('input[name="PlanCode"]');
            var planError = document.getElementById("signupPlanCode-error");
            if (planError) {
                planError.textContent = messages.planRequired || "";
                planError.className = "field-validation-error";
            }
            if (firstPlan) {
                errors.unshift({ element: firstPlan, message: messages.planRequired || "" });
            }
        } else if (id === "plan") {
            var clearPlanError = document.getElementById("signupPlanCode-error");
            if (clearPlanError && clearPlanError.textContent === messages.planRequired) {
                clearPlanError.textContent = "";
                clearPlanError.className = "field-validation-valid";
            }
        }

        if (id === "restaurant" && window.WaslaSignupBusinessTypes) {
            var types = window.WaslaSignupBusinessTypes.check(document);
            if (!types.ok && types.focusTarget) {
                errors.push({ element: types.focusTarget, message: types.message });
            }
        }

        errors.sort(function (a, b) {
            return a.element.compareDocumentPosition(b.element) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1;
        });
        return errors;
    }

    function scrollIntoView(element, block) {
        if (element && typeof element.scrollIntoView === "function") {
            element.scrollIntoView({ block: block || "center", inline: "nearest" });
        }
    }

    // One error: focus its field (its message is in its description). Several: a summary of links, focused.
    // Returns the element to focus.
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
            link.textContent = error.message || (error.element.labels && error.element.labels[0] ? error.element.labels[0].textContent.trim() : error.element.name);
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
        scrollIntoView(element, element.matches("[data-su-error-summary], .su-title") ? "start" : "center");
        element.focus({ preventScroll: true });
    }

    // ---------------------------------------------------------------- Showing a step

    function stepUrl(id) {
        return Steps.urlForStep(window.location.href, id);
    }

    function show(id, options) {
        options = options || {};
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

        if (current === "review") {
            fillReview();
        }
        renderReceipt();

        if (options.history === "push") {
            window.history.pushState({ suStep: current }, "", stepUrl(current));
        } else if (options.history === "replace") {
            window.history.replaceState({ suStep: current }, "", stepUrl(current));
        } else if (options.history === "mark") {
            // The landing entry keeps its address; it only learns which step it shows, for Back.
            window.history.replaceState({ suStep: current }, "");
        }

        return heading;
    }

    // Moves to a step; forward moves check the open step first. Returns the element that should get focus.
    function navigate(target) {
        if (!target || !panels[target]) {
            return null;
        }

        var from = state.current;
        if (order.indexOf(target) > order.indexOf(from)) {
            var errors = validateStep(from);
            if (errors.length) {
                state = Steps.setComplete(state, from, false);
                renderReceipt();
                return presentErrors(from, errors);
            }
            state = Steps.setComplete(state, from, true);
            hideErrorSummary(from);
        }

        if (target === state.current) {
            return panels[target].querySelector(".su-title");
        }
        return show(target, { history: "push" });
    }

    function navigateAndFocus(target, fromSheet) {
        var focusTarget = navigate(target);
        if (fromSheet && sheet && sheet.isOpen && sheet.isOpen()) {
            sheet.close(focusTarget || undefined);
            if (focusTarget) {
                scrollIntoView(focusTarget, "start");
            }
            return;
        }
        focusElement(focusTarget);
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
            focusElement(document.getElementById(errorLink.getAttribute("data-su-error-link")));
            return;
        }

        if (target.closest("[data-su-next]")) {
            event.preventDefault();
            navigateAndFocus(Steps.next(state), false);
            return;
        }

        var go = target.closest("[data-su-go]");
        if (go) {
            event.preventDefault();
            navigateAndFocus(go.getAttribute("data-su-go"), !!go.closest(".su-sheet"));
            return;
        }

        if (stripAction && target.closest("[data-su-strip-action]")) {
            event.preventDefault();
            if (state.current === "review" && submitButton) {
                if (typeof form.requestSubmit === "function") {
                    form.requestSubmit(submitButton);
                } else {
                    submitButton.click();
                }
                return;
            }
            var next = panels[state.current].querySelector("[data-su-next]");
            if (next) {
                next.click();
            }
        }
    });

    form.addEventListener("input", function (event) {
        var name = event.target && event.target.name;
        if (name === "Password" || name === "ConfirmPassword") {
            updatePasswordRules();
        }
        renderReceipt();
    });

    form.addEventListener("change", function () {
        renderReceipt();
    });

    // Capture phase, so this runs before the other submit handlers of the form. Enter in a field of an earlier step
    // continues to the next step; the registration is sent only from the review step, with every step complete.
    form.addEventListener("submit", function (event) {
        if (submitting) {
            event.preventDefault();
            event.stopImmediatePropagation();
            return;
        }

        if (state.current !== "review") {
            event.preventDefault();
            event.stopImmediatePropagation();
            navigateAndFocus(Steps.next(state), false);
            return;
        }

        var incomplete = order.filter(function (id) {
            return id !== "review" && !Steps.isComplete(state, id);
        })[0];
        if (incomplete) {
            event.preventDefault();
            event.stopImmediatePropagation();
            show(incomplete, { history: "push" });
            var errors = validateStep(incomplete);
            focusElement(errors.length ? presentErrors(incomplete, errors) : panels[incomplete].querySelector(".su-title"));
            return;
        }

        // Busy once no other handler (the form's validation) has stopped this submission; a second press is ignored.
        window.setTimeout(function () {
            if (event.defaultPrevented) {
                return;
            }
            submitting = true;
            [submitButton, stripAction].forEach(function (button) {
                if (button) {
                    button.setAttribute("aria-busy", "true");
                }
            });
        }, 0);
    }, true);

    // A page restored from the back/forward cache after a submission can be used again.
    window.addEventListener("pageshow", function (event) {
        if (event.persisted) {
            submitting = false;
            [submitButton, stripAction].forEach(function (button) {
                if (button) {
                    button.removeAttribute("aria-busy");
                }
            });
        }
    });

    window.addEventListener("popstate", function (event) {
        var requested = event.state && event.state.suStep;
        if (!requested || !panels[requested]) {
            return;
        }
        if (sheet && sheet.isOpen && sheet.isOpen()) {
            sheet.close();
        }
        var heading = show(requested);
        if (state.current !== requested) {
            window.history.replaceState({ suStep: state.current }, "", stepUrl(state.current));
        }
        focusElement(heading);
    });

    // ---------------------------------------------------------------- Start

    function markServerErrors() {
        Array.prototype.forEach.call(form.querySelectorAll(".input-validation-error"), function (element) {
            element.setAttribute("aria-invalid", "true");
        });
    }

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

    markServerErrors();
    showToday();
    updatePasswordRules();

    // A step named in the address opens when it may (a reload or a shared link); after a submission the server's
    // choice (the first step with an error) wins.
    var requested = flow.restoreFocus ? null : Steps.readStepFromUrl(window.location.href, order);
    show(requested || state.current, { history: "mark" });
    if (requested && requested !== state.current) {
        window.history.replaceState({ suStep: state.current }, "", stepUrl(state.current));
    }

    if (flow.restoreFocus) {
        var serverErrors = validatableFields(panels[state.current])
            .filter(function (element) { return element.classList.contains("input-validation-error"); })
            .map(function (element) { return { element: element, message: messageFor(element) }; });
        var formErrors = form.querySelector(".su-model-errors.validation-summary-errors");
        if (serverErrors.length) {
            focusElement(presentErrors(state.current, serverErrors));
        } else if (formErrors) {
            formErrors.setAttribute("tabindex", "-1");
            focusElement(formErrors);
        }
    }
})();
