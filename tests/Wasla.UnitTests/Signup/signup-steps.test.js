const test = require("node:test");
const assert = require("node:assert/strict");

const steps = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-steps.js");

const order = ["plan", "restaurant", "owner", "review"];

test("a new flow opens on the first step and only that step is allowed", () => {
    const state = steps.create(order);

    assert.equal(state.current, "plan");
    assert.equal(steps.canEnter(state, "plan"), true);
    assert.equal(steps.canEnter(state, "restaurant"), false);
    assert.equal(steps.furthestAllowed(state), "plan");
});

test("forward is allowed only up to the first incomplete step; back is always allowed", () => {
    let state = steps.create(order, "plan", ["plan", "restaurant"]);

    assert.equal(steps.canEnter(state, "owner"), true);
    assert.equal(steps.canEnter(state, "review"), false);

    state = steps.goTo(state, "review");
    assert.equal(state.current, "owner", "a step past an incomplete one lands on the incomplete step");

    state = steps.goTo(state, "plan");
    assert.equal(state.current, "plan");
});

test("a requested start step is clamped to what the completed steps allow", () => {
    assert.equal(steps.create(order, "review", ["plan"]).current, "restaurant");
    assert.equal(steps.create(order, "owner", ["plan", "restaurant"]).current, "owner");
    assert.equal(steps.create(order, "unknown").current, "plan");
});

test("completing and reopening a step changes what can be entered, without mutating the earlier state", () => {
    const start = steps.create(order);
    const done = steps.setComplete(start, "plan", true);

    assert.equal(steps.isComplete(start, "plan"), false);
    assert.equal(steps.isComplete(done, "plan"), true);
    assert.equal(steps.canEnter(done, "restaurant"), true);

    const reopened = steps.setComplete(done, "plan", false);
    assert.equal(steps.canEnter(reopened, "restaurant"), false);
    assert.equal(steps.setComplete(done, "nope", true), done);
});

test("when every step is complete the last step is the furthest allowed", () => {
    const state = steps.create(order, "plan", order);
    assert.equal(steps.furthestAllowed(state), "review");
    assert.equal(steps.canEnter(state, "review"), true);
});

test("statuses: the open step is current, completed ones are done, the rest are upcoming", () => {
    const state = steps.goTo(steps.create(order, "plan", ["plan", "restaurant", "owner"]), "restaurant");

    assert.deepEqual(order.map(id => steps.statusOf(state, id)), ["done", "current", "done", "upcoming"]);
});

test("next and previous follow the flow order", () => {
    const state = steps.create(order, "restaurant", ["plan"]);

    assert.equal(steps.next(state), "owner");
    assert.equal(steps.previous(state), "plan");
    assert.equal(steps.previous(steps.create(order)), null);
    assert.equal(steps.next(steps.create(order, "review", order)), null);
});

test("a forward jump lists the steps from the open one up to the target", () => {
    const state = steps.create(order, "restaurant", order);

    assert.deepEqual(steps.stepsToValidate(state, "review"), ["restaurant", "owner"]);
    assert.deepEqual(steps.stepsToValidate(state, "plan"), []);
    assert.deepEqual(steps.stepsToValidate(state, "restaurant"), []);
});

test("after a submission the first step, in flow order, that owns an error opens", () => {
    const fieldSteps = { PlanCode: "plan", Slug: "restaurant", Password: "owner" };

    assert.equal(steps.firstStepWithErrors(["Password", "Slug"], fieldSteps, order), "restaurant");
    assert.equal(steps.firstStepWithErrors(["", "Unknown"], fieldSteps, order), null);
    assert.equal(steps.firstStepWithErrors([], fieldSteps, order), null);
});

test("the step travels in the query and every other parameter and the hash are kept", () => {
    assert.equal(steps.urlForStep("/signup?plan=Pro", "owner"), "/signup?plan=Pro&step=owner");
    assert.equal(steps.urlForStep("/signup?step=plan&plan=Pro#top", "review"), "/signup?step=review&plan=Pro#top");
    assert.equal(steps.urlForStep("https://wasla.tr/signup", "plan"), "https://wasla.tr/signup?step=plan");

    assert.equal(steps.readStepFromUrl("/signup?plan=Pro&step=owner", order), "owner");
    assert.equal(steps.readStepFromUrl("/signup?step=payment", order), null);
    assert.equal(steps.readStepFromUrl("/signup", order), null);
});

test("a flow needs steps", () => {
    assert.throws(() => steps.create([]));
});
