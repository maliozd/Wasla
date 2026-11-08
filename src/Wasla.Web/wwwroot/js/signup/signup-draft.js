(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupDraft = api;
    }
})(function () {
    "use strict";

    // Reads the server's registration draft (WAS-192 draft state) for the signup page (WAS-194): which steps are
    // complete, where the visitor continues, the form values to restore, the request bodies of the section saves, and
    // how the phone step looks for a phone status. The server decides completeness and verification; this only reads it.

    var SECTION_STEPS = ["plan", "restaurant", "owner"];

    function phoneVerified(draft) {
        return !!(draft && draft.phone && draft.phone.state === "verified");
    }

    function sectionComplete(draft, section) {
        return !!(draft && draft[section] && draft[section].complete);
    }

    // The steps the server reports complete. The review step is never complete; a submitted draft is handled apart.
    function completedSteps(draft) {
        var done = SECTION_STEPS.filter(function (section) { return sectionComplete(draft, section); });
        if (phoneVerified(draft) && sectionComplete(draft, "owner")) {
            done.push("phone");
        }
        return done;
    }

    // Where the visitor continues: the first unsaved section, then the phone step until the number is verified, then
    // the review.
    function currentStep(draft) {
        for (var i = 0; i < SECTION_STEPS.length; i++) {
            if (!sectionComplete(draft, SECTION_STEPS[i])) {
                return SECTION_STEPS[i];
            }
        }
        return phoneVerified(draft) ? "review" : "phone";
    }

    // The plan the page starts with: the saved one, else the ?plan= request when it names a self-service plan.
    function initialPlan(draft, requestedPlanCode, selfServiceCodes) {
        if (draft && draft.plan && draft.plan.planCode) {
            return draft.plan.planCode;
        }
        if (requestedPlanCode && (!selfServiceCodes || selfServiceCodes.indexOf(requestedPlanCode) >= 0)) {
            return requestedPlanCode;
        }
        return null;
    }

    function text(value) {
        return value == null ? "" : String(value);
    }

    function trimmed(value) {
        return text(value).trim();
    }

    function nullable(value) {
        var t = trimmed(value);
        return t.length ? t : null;
    }

    function numberOrNull(value) {
        var t = trimmed(value);
        if (!t) {
            return null;
        }
        var n = Number(t);
        return isFinite(n) ? n : null;
    }

    // "5321234567" → "532 123 45 67" (the national number as the page shows it after the fixed +90).
    function formatMobile(digits) {
        var d = text(digits).replace(/\D/g, "");
        if (d.length !== 10) {
            return d;
        }
        return d.slice(0, 3) + " " + d.slice(3, 6) + " " + d.slice(6, 8) + " " + d.slice(8, 10);
    }

    // Field values for the form, keyed by the page's input ids, from a draft (missing sections give empty values).
    function formValues(draft) {
        var plan = (draft && draft.plan) || {};
        var restaurant = (draft && draft.restaurant) || {};
        var owner = (draft && draft.owner) || {};
        return {
            plan: { planCode: plan.planCode || null, billingPeriod: plan.billingPeriod || null },
            restaurant: {
                businessName: text(restaurant.businessName),
                businessTypeCodes: (restaurant.businessTypeCodes || []).slice(),
                businessPhoneType: restaurant.businessPhoneType || null,
                businessPhone: text(restaurant.businessPhone),
                businessEmail: text(restaurant.businessEmail),
                slug: text(restaurant.slug),
                country: restaurant.country || null,
                cityId: restaurant.cityId == null ? null : restaurant.cityId,
                districtId: restaurant.districtId == null ? null : restaurant.districtId,
                neighborhoodId: restaurant.neighborhoodId == null ? null : restaurant.neighborhoodId,
                streetId: restaurant.streetId == null ? null : restaurant.streetId,
                neighborhood: text(restaurant.neighborhood),
                streetAddress: text(restaurant.streetAddress),
                buildingNumber: text(restaurant.buildingNumber),
                floor: text(restaurant.floor),
                doorNumber: text(restaurant.doorNumber),
                addressNote: text(restaurant.addressNote),
                postalCode: text(restaurant.postalCode),
                locationUrl: text(restaurant.locationUrl)
            },
            owner: {
                fullName: text(owner.fullName),
                email: text(owner.email),
                mobilePhone: formatMobile(owner.mobilePhone),
                passwordSet: !!owner.passwordSet
            }
        };
    }

    // PUT /signup/draft/plan.
    function planRequest(values) {
        return { planCode: nullable(values.planCode), billingPeriod: nullable(values.billingPeriod) };
    }

    // PUT /signup/draft/restaurant. Ids are numbers or null; empty optional text is null. The server normalizes and
    // validates everything; nothing is decided here.
    function restaurantRequest(values) {
        return {
            businessName: nullable(values.businessName),
            businessTypeCodes: (values.businessTypeCodes || []).filter(function (code) { return trimmed(code).length > 0; }),
            businessPhoneType: nullable(values.businessPhoneType),
            businessPhone: nullable(values.businessPhone),
            businessEmail: nullable(values.businessEmail),
            slug: nullable(values.slug),
            country: nullable(values.country),
            cityId: numberOrNull(values.cityId),
            districtId: numberOrNull(values.districtId),
            neighborhoodId: numberOrNull(values.neighborhoodId),
            streetId: numberOrNull(values.streetId),
            city: nullable(values.city),
            district: nullable(values.district),
            neighborhood: nullable(values.neighborhood),
            streetAddress: nullable(values.streetAddress),
            buildingNumber: nullable(values.buildingNumber),
            floor: nullable(values.floor),
            doorNumber: nullable(values.doorNumber),
            addressNote: nullable(values.addressNote),
            postalCode: nullable(values.postalCode),
            locationUrl: nullable(values.locationUrl)
        };
    }

    // PUT /signup/draft/owner. The password goes only when typed: empty fields keep the stored hash.
    function ownerRequest(values) {
        var body = {
            fullName: nullable(values.fullName),
            email: nullable(values.email),
            mobilePhone: nullable(values.mobilePhone)
        };
        if (text(values.password).length > 0 || text(values.confirmPassword).length > 0) {
            body.password = text(values.password);
            body.confirmPassword = text(values.confirmPassword);
        }
        return body;
    }

    // The client may skip its own required-password check only while the server holds a password and nothing new
    // was typed.
    function passwordOptional(draft, password, confirmPassword) {
        return !!(draft && draft.owner && draft.owner.passwordSet)
            && text(password).length === 0 && text(confirmPassword).length === 0;
    }

    // Field errors grouped by step: { plan: [...], restaurant: [...], owner: [...] }, in the server's order.
    function groupFieldErrors(fieldErrors) {
        var groups = {};
        (fieldErrors || []).forEach(function (error) {
            if (!error || !error.section) {
                return;
            }
            (groups[error.section] = groups[error.section] || []).push(error);
        });
        return groups;
    }

    // The first step, in flow order, that has a field error or is listed as missing.
    function firstStepOf(sections) {
        var list = sections || [];
        for (var i = 0; i < SECTION_STEPS.length; i++) {
            if (list.indexOf(SECTION_STEPS[i]) >= 0) {
                return SECTION_STEPS[i];
            }
        }
        return null;
    }

    // How the phone step looks for a phone status (T1–T7). local carries what only the page knows: a send in flight,
    // the number being changed, a code that ran out on the page's clock.
    function phoneView(status, local) {
        status = status || {};
        local = local || {};
        var state = status.state || "notSent";
        if (state === "codeSent" && local.codeExpired) {
            state = "expired";
        }

        var view = {
            state: state,
            sending: !!local.sending,
            changing: !!local.changing,
            showCode: state === "codeSent" || state === "expired" || state === "verified",
            codeEnabled: state === "codeSent" && !local.sending,
            showNumberCard: state !== "verified" && state !== "unavailable",
            showVerifiedCard: state === "verified",
            canChangeNumber: state !== "verified" && state !== "unavailable" && !local.sending,
            resendIn: null,
            canSendNow: false,
            primary: null
        };

        if (state === "codeSent") {
            view.primary = "verify";
        } else if (state === "verified") {
            view.primary = "continue";
        } else if (state === "notSent" || state === "expired" || state === "locked") {
            view.primary = state === "notSent" ? "send" : "resend";
        }

        if (state === "locked") {
            view.resendIn = typeof status.lockedForSeconds === "number" ? status.lockedForSeconds : null;
        } else if (typeof status.resendAvailableInSeconds === "number") {
            view.resendIn = status.resendAvailableInSeconds;
        }
        view.canSendNow = state !== "verified" && state !== "unavailable" && !local.sending
            && typeof status.resendAvailableInSeconds === "number" && status.resendAvailableInSeconds <= 0
            && !(state === "locked" && status.lockedForSeconds > 0);

        if (local.changing) {
            view.showCode = false;
            view.showNumberCard = false;
            view.primary = "sendToNumber";
        }
        return view;
    }

    return {
        completedSteps: completedSteps,
        currentStep: currentStep,
        initialPlan: initialPlan,
        formatMobile: formatMobile,
        formValues: formValues,
        planRequest: planRequest,
        restaurantRequest: restaurantRequest,
        ownerRequest: ownerRequest,
        passwordOptional: passwordOptional,
        groupFieldErrors: groupFieldErrors,
        firstStepOf: firstStepOf,
        phoneView: phoneView
    };
});
