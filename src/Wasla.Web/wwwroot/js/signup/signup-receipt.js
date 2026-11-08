(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupReceipt = api;
    }
})(function () {
    "use strict";

    // The registration receipt (kayıt fişi, WAS-194): the desktop aside, the mobile summary sheet and the bottom
    // strip show the same lines. Every amount and VAT text is the server's (the Central Admin price in effect, WAS-147);
    // this module only picks the selected plan and period and puts the localized templates together. A period without
    // a price shows the server's "not set" text; nothing is derived from the other period.

    function format(template, args) {
        return String(template == null ? "" : template).replace(/\{(\d+)\}/g, function (match, index) {
            var value = args[Number(index)];
            return value == null ? "" : String(value);
        });
    }

    function findPlan(plans, code) {
        if (!Array.isArray(plans) || !code) {
            return null;
        }
        for (var i = 0; i < plans.length; i++) {
            if (plans[i].code === code && !plans[i].isContactSales) {
                return plans[i];
            }
        }
        return null;
    }

    // What the receipt says about the selected plan and period. messages carries the localized templates.
    function planView(plan, isYearly, messages) {
        messages = messages || {};
        if (!plan) {
            return {
                selected: false,
                name: "",
                planLine: "",
                priceLine: "",
                amount: messages.choosePlan || "",
                amountSet: false,
                vat: "",
                periodNote: "",
                stripDetail: ""
            };
        }

        var period = (isYearly ? plan.yearly : plan.monthly) || {};
        var periodName = isYearly ? messages.periodYearly : messages.periodMonthly;
        var amountSet = typeof period.amount === "string" && period.amount.length > 0;
        var vat = amountSet && period.vat ? period.vat : "";
        var name = plan.name || "";

        return {
            selected: true,
            name: name,
            planLine: format(messages.planLine, [name, periodName]),
            priceLine: vat ? format(messages.priceLine, [period.price || "", vat]) : (period.price || ""),
            amount: amountSet ? period.amount : (period.price || messages.priceNotSet || ""),
            amountSet: amountSet,
            vat: vat,
            periodNote: amountSet
                ? format(isYearly ? messages.periodNoteYearly : messages.periodNoteMonthly, [name])
                : "",
            stripDetail: vat ? format(messages.stripDetail, [name, periodName, vat]) : format(messages.planLine, [name, periodName])
        };
    }

    // "sushim.app.wasla.tr" once a slug is typed; empty before.
    function domainOf(slug, baseDomain) {
        var value = typeof slug === "string" ? slug.trim().toLowerCase() : "";
        return value && baseDomain ? value + "." + baseDomain : "";
    }

    // Writes the named values into every element bound with data-su-text="name" under the given roots.
    function bindText(roots, values) {
        roots.forEach(function (root) {
            if (!root) {
                return;
            }
            Array.prototype.forEach.call(root.querySelectorAll("[data-su-text]"), function (element) {
                var key = element.getAttribute("data-su-text");
                if (Object.prototype.hasOwnProperty.call(values, key)) {
                    var text = values[key] == null ? "" : String(values[key]);
                    if (element.textContent !== text) {
                        element.textContent = text;
                    }
                }
            });
        });
    }

    // A step shows its values once it is open or done and has any; otherwise its "next up" hint.
    function shouldShowValues(status, values) {
        if (status === "upcoming") {
            return false;
        }
        return values.some(function (value) {
            return typeof value === "string" && value.trim().length > 0;
        });
    }

    // Marks each receipt step done/current/upcoming (aria-current="step" on the open one) and chooses between its
    // values and its hint. statusOf(id) comes from the step state.
    function renderSteps(receipt, statusOf, stateLabels) {
        Array.prototype.forEach.call(receipt.querySelectorAll("[data-su-receipt-step]"), function (item) {
            var id = item.getAttribute("data-su-receipt-step");
            var status = statusOf(id);
            item.setAttribute("data-state", status);
            if (status === "current") {
                item.setAttribute("aria-current", "step");
            } else {
                item.removeAttribute("aria-current");
            }

            var stateText = item.querySelector("[data-su-step-state]");
            if (stateText && stateLabels && stateLabels[status]) {
                stateText.textContent = stateLabels[status];
            }

            var lines = Array.prototype.slice.call(item.querySelectorAll("[data-su-text]"));
            var showValues = shouldShowValues(status, lines.map(function (line) { return line.textContent; }));
            lines.forEach(function (line) {
                line.hidden = !showValues || !line.textContent.trim();
            });
            var hint = item.querySelector("[data-su-hint]");
            if (hint) {
                hint.hidden = showValues;
            }
        });
    }

    return {
        format: format,
        findPlan: findPlan,
        planView: planView,
        domainOf: domainOf,
        bindText: bindText,
        shouldShowValues: shouldShowValues,
        renderSteps: renderSteps
    };
});
