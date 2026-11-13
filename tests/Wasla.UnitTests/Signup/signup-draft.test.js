const test = require("node:test");
const assert = require("node:assert/strict");

const draft = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-draft.js");
const fixture = require("./signup-draft.fixture.json");

const clone = value => JSON.parse(JSON.stringify(value));

test("without a draft the visitor starts at the plan and nothing is complete", () => {
    assert.equal(draft.currentStep(fixture.none), "plan");
    assert.deepEqual(draft.completedSteps(fixture.none), []);
    assert.equal(draft.currentStep(null), "plan");
});

test("the visitor continues at the first unsaved section, then the phone step until verified, then the review", () => {
    const planOnly = clone(fixture.none);
    planOnly.exists = true;
    planOnly.plan.complete = true;
    assert.equal(draft.currentStep(planOnly), "restaurant");
    assert.deepEqual(draft.completedSteps(planOnly), ["plan"]);

    assert.equal(draft.currentStep(fixture.codeSent), "phone");
    assert.deepEqual(draft.completedSteps(fixture.codeSent), ["plan", "restaurant", "owner"]);

    const verified = clone(fixture.codeSent);
    verified.phone = fixture.phoneStatuses.verified;
    assert.equal(draft.currentStep(verified), "review");
    assert.deepEqual(draft.completedSteps(verified), ["plan", "restaurant", "owner", "phone"]);
});

test("a verified phone without a saved owner section does not complete the phone step", () => {
    const odd = clone(fixture.codeSent);
    odd.owner.complete = false;
    odd.phone = fixture.phoneStatuses.verified;

    assert.equal(draft.currentStep(odd), "owner");
    assert.equal(draft.completedSteps(odd).includes("phone"), false);
});

test("?plan= preselects only a self-service plan and only when the draft has none", () => {
    const self = ["Starter", "Pro", "ProPlus"];

    assert.equal(draft.initialPlan(fixture.none, "Pro", self), "Pro");
    assert.equal(draft.initialPlan(fixture.none, "Enterprise", self), null);
    assert.equal(draft.initialPlan(fixture.none, null, self), null);
    assert.equal(draft.initialPlan(fixture.codeSent, "Starter", self), "Pro");
});

test("the saved draft becomes form values: the national mobile number as the page shows it, no password", () => {
    const values = draft.formValues(fixture.codeSent);

    assert.equal(values.restaurant.businessName, "Burger House");
    assert.deepEqual(values.restaurant.businessTypeCodes, ["burger"]);
    assert.equal(values.restaurant.cityId, 34);
    assert.equal(values.restaurant.businessEmail, "");
    assert.equal(values.owner.mobilePhone, "532 123 45 67");
    assert.equal(values.owner.passwordSet, true);
    assert.equal("password" in values.owner, false);

    const empty = draft.formValues(fixture.none);
    assert.equal(empty.restaurant.slug, "");
    assert.equal(empty.owner.mobilePhone, "");
});

test("mobile numbers are shown in groups only when they are ten national digits", () => {
    assert.equal(draft.formatMobile("5321234567"), "532 123 45 67");
    assert.equal(draft.formatMobile("532 123"), "532123");
    assert.equal(draft.formatMobile(null), "");
});

test("section requests send ids as numbers and empty optional text as null; the server validates the rest", () => {
    const body = draft.restaurantRequest({
        businessName: " Burger House ",
        businessTypeCodes: ["burger", " "],
        businessPhoneType: "Landline",
        businessPhone: "2125551234",
        businessEmail: "",
        slug: "burger-house",
        country: "Türkiye",
        cityId: "34",
        districtId: "451",
        neighborhoodId: "",
        streetId: undefined,
        streetAddress: "Moda Cad.",
        buildingNumber: "12"
    });

    assert.equal(body.businessName, "Burger House");
    assert.deepEqual(body.businessTypeCodes, ["burger"]);
    assert.equal(body.cityId, 34);
    assert.equal(body.districtId, 451);
    assert.equal(body.neighborhoodId, null);
    assert.equal(body.streetId, null);
    assert.equal(body.businessEmail, null);
    assert.equal(body.floor, null);
    assert.deepEqual(draft.planRequest({ planCode: "Pro", billingPeriod: "" }), { planCode: "Pro", billingPeriod: null });
});

test("the owner request carries a password only when one was typed, so the stored hash is kept", () => {
    const keep = draft.ownerRequest({ fullName: "Ayşe Yılmaz", email: "ayse@example.test", mobilePhone: "5321234567", password: "", confirmPassword: "" });
    assert.deepEqual(Object.keys(keep).sort(), ["email", "fullName", "mobilePhone"]);

    const change = draft.ownerRequest({ fullName: "Ayşe", email: "a@b.test", mobilePhone: "5321234567", password: "new secret", confirmPassword: "new secre" });
    assert.equal(change.password, "new secret");
    assert.equal(change.confirmPassword, "new secre");

    assert.equal(draft.passwordOptional(fixture.codeSent, "", ""), true);
    assert.equal(draft.passwordOptional(fixture.codeSent, "x", ""), false);
    assert.equal(draft.passwordOptional(fixture.none, "", ""), false);
});

test("field errors are grouped by step, and the first step in flow order is chosen", () => {
    const groups = draft.groupFieldErrors(fixture.errors.validation.fieldErrors);

    assert.deepEqual(Object.keys(groups).sort(), ["owner", "restaurant"]);
    assert.deepEqual(groups.restaurant.map(e => e.field), ["slug", "businessName"]);
    assert.equal(draft.firstStepOf(Object.keys(groups)), "restaurant");
    assert.equal(draft.firstStepOf(["owner", "plan"]), "plan");
    assert.equal(draft.firstStepOf(null), null);
    assert.deepEqual(draft.groupFieldErrors(null), {});
});

test("the phone step: a code can be sent now only when the server says so", () => {
    const notSent = draft.phoneView(fixture.phoneStatuses.notSentNow);
    assert.equal(notSent.primary, "send");
    assert.equal(notSent.canSendNow, true);
    assert.equal(notSent.showCode, false);

    const waiting = draft.phoneView(fixture.phoneStatuses.codeSentWaiting);
    assert.equal(waiting.primary, "verify");
    assert.equal(waiting.codeEnabled, true);
    assert.equal(waiting.resendIn, 59);
    assert.equal(waiting.canSendNow, false);

    const sending = draft.phoneView(fixture.phoneStatuses.notSentNow, { sending: true });
    assert.equal(sending.canSendNow, false);
});

test("the phone step: an expired code, a lock, verification and an unavailable sender", () => {
    const expiredOnPage = draft.phoneView(fixture.codeSent.phone, { codeExpired: true });
    assert.equal(expiredOnPage.state, "expired");
    assert.equal(expiredOnPage.primary, "resend");
    assert.equal(expiredOnPage.codeEnabled, false);

    const locked = draft.phoneView(fixture.phoneStatuses.locked);
    assert.equal(locked.resendIn, 812);
    assert.equal(locked.canSendNow, false);
    assert.equal(locked.codeEnabled, false);

    // A verified number opened again: its card, "continue" and "change number"; no code field.
    const verified = draft.phoneView(fixture.phoneStatuses.verified);
    assert.equal(verified.primary, "continue");
    assert.equal(verified.showVerifiedCard, true);
    assert.equal(verified.canChangeNumber, true);
    assert.equal(verified.showCode, false);
    assert.equal(verified.canSendNow, false);
    // Right after the code was accepted, the code stays in sight during the confirmation.
    assert.equal(draft.phoneView(fixture.phoneStatuses.verified, { confirming: true }).showCode, true);
    // Changing a verified number hides its card and offers to send to the new one.
    const changingVerified = draft.phoneView(fixture.phoneStatuses.verified, { changing: true });
    assert.equal(changingVerified.showVerifiedCard, false);
    assert.equal(changingVerified.primary, "sendToNumber");

    const unavailable = draft.phoneView(fixture.phoneStatuses.unavailable);
    assert.equal(unavailable.primary, null);
    assert.equal(unavailable.canSendNow, false);
    assert.equal(unavailable.showNumberCard, false);
});

test("the phone step: changing the number hides the code and offers to send to the new number", () => {
    const view = draft.phoneView(fixture.codeSent.phone, { changing: true });

    assert.equal(view.changing, true);
    assert.equal(view.showCode, false);
    assert.equal(view.showNumberCard, false);
    assert.equal(view.primary, "sendToNumber");
});
