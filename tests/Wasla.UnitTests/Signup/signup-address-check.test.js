const test = require("node:test");
const assert = require("node:assert/strict");

const check = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-address-check.js");

function fakeTimers() {
    const timers = [];
    return {
        setTimer: (fn, ms) => { const t = { fn, ms, cleared: false }; timers.push(t); return t; },
        clearTimer: t => { t.cleared = true; },
        // Fires the pending (not cleared, not run) timers.
        fire: () => timers.filter(t => !t.cleared && !t.ran).forEach(t => { t.ran = true; t.fn(); }),
        pending: () => timers.filter(t => !t.cleared && !t.ran).length
    };
}

function deferred() {
    let resolve, reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

function harness() {
    const timers = fakeTimers();
    const requests = [];
    const states = [];
    const checker = check.create({
        debounceMs: 400,
        setTimer: timers.setTimer,
        clearTimer: timers.clearTimer,
        createAbortController: () => {
            const controller = { aborted: false, signal: {} };
            controller.abort = () => { controller.aborted = true; };
            return controller;
        },
        check: (slug, signal) => {
            const request = deferred();
            requests.push({ slug, signal, ...request });
            return request.promise;
        },
        onState: state => states.push(state)
    });
    return { timers, requests, states, checker };
}

test("the shape of the address is checked at once, with the server's limit of 63 characters", () => {
    assert.equal(check.checkFormat(""), "empty");
    assert.equal(check.checkFormat("sushim"), "ok");
    assert.equal(check.checkFormat("sushim-kadikoy_2"), "ok");
    assert.equal(check.checkFormat("Sushim Kadıköy"), "invalid");
    assert.equal(check.checkFormat("-sushim"), "invalid");
    assert.equal(check.checkFormat("a".repeat(63)), "ok");
    assert.equal(check.checkFormat("a".repeat(64)), "too-long");
});

test("the server is asked once typing pauses, only for the latest address", async () => {
    const { timers, requests, states, checker } = harness();

    checker.input("s");
    checker.input("su");
    checker.input("sushim");
    assert.equal(requests.length, 0, "nothing is sent while typing");
    assert.equal(timers.pending(), 1);

    timers.fire();
    await flush();
    assert.deepEqual(requests.map(r => r.slug), ["sushim"]);
    assert.deepEqual(states.at(-1), { state: "checking", slug: "sushim" });

    requests[0].resolve({ state: "available" });
    await flush();
    assert.deepEqual(states.at(-1), { state: "available", slug: "sushim" });
});

test("an answer for an address that changed meanwhile is dropped and its request aborted", async () => {
    const { timers, requests, states, checker } = harness();

    checker.input("sushi");
    timers.fire();
    await flush();
    checker.input("sushim");
    assert.equal(requests[0].signal !== undefined, true);
    timers.fire();
    await flush();

    // The newer answer arrives first, then the stale one.
    requests[1].resolve({ state: "available" });
    await flush();
    requests[0].resolve({ state: "taken" });
    await flush();

    const settled = states.filter(s => s.state !== "checking");
    assert.deepEqual(settled, [{ state: "available", slug: "sushim" }]);
});

test("the request in flight is aborted when the address changes", async () => {
    const timers = fakeTimers();
    const controllers = [];
    const checker = check.create({
        setTimer: timers.setTimer,
        clearTimer: timers.clearTimer,
        createAbortController: () => {
            const controller = { aborted: false, signal: {} };
            controller.abort = () => { controller.aborted = true; };
            controllers.push(controller);
            return controller;
        },
        check: () => new Promise(() => { }),
        onState: () => { }
    });

    checker.input("sushi");
    timers.fire();
    await flush();
    checker.input("sushim");

    assert.equal(controllers[0].aborted, true);
});

test("an empty or malformed address never reaches the server", async () => {
    const { timers, requests, states, checker } = harness();

    checker.input("Sushim Kadıköy");
    checker.input("");
    timers.fire();
    await flush();

    assert.equal(requests.length, 0);
    assert.deepEqual(states, [
        { state: "invalid", slug: "Sushim Kadıköy", reason: "invalid" },
        { state: "idle", slug: "" }
    ]);
});

test("leaving the field asks at once, and not again once the answer is in", async () => {
    const { timers, requests, checker } = harness();

    checker.input("sushim");
    checker.flush();
    await flush();
    assert.equal(requests.length, 1);
    assert.equal(timers.pending(), 0, "the debounce timer is cancelled");

    requests[0].resolve({ state: "taken", suggestion: "sushim-kadikoy" });
    await flush();
    checker.flush();
    await flush();
    assert.equal(requests.length, 1);
});

test("a failed request shows the error state, and the server's extra fields are kept", async () => {
    const { timers, requests, states, checker } = harness();

    checker.input("sushim");
    timers.fire();
    await flush();
    requests[0].reject(new Error("network"));
    await flush();
    assert.deepEqual(states.at(-1), { state: "error", slug: "sushim" });

    checker.input("sushim2");
    timers.fire();
    await flush();
    requests[1].resolve({ state: "taken", suggestion: "sushim2-kadikoy" });
    await flush();
    assert.deepEqual(states.at(-1), { state: "taken", suggestion: "sushim2-kadikoy", slug: "sushim2" });
});

test("an unchanged address is not asked again, and dispose silences a pending answer", async () => {
    const { timers, requests, states, checker } = harness();

    checker.input("sushim");
    checker.input(" sushim ");
    timers.fire();
    await flush();
    assert.equal(requests.length, 1);

    checker.dispose();
    requests[0].resolve({ state: "available" });
    await flush();
    assert.equal(states.some(s => s.state === "available"), false);
});
