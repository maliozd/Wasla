const test = require("node:test");
const assert = require("node:assert/strict");

const signupApi = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-api.js");
const fixture = require("./signup-draft.fixture.json");

const endpoints = {
    draft: "/signup/draft",
    planSection: "/signup/draft/plan",
    restaurantSection: "/signup/draft/restaurant",
    ownerSection: "/signup/draft/owner",
    phone: "/signup/draft/phone",
    phoneSend: "/signup/draft/phone/send",
    phoneVerify: "/signup/draft/phone/verify",
    submit: "/signup/draft/submit",
    addressAvailability: "/signup/address-availability"
};

function response(status, body, headers) {
    const all = Object.assign({ "Content-Type": body === undefined ? "text/plain" : "application/json; charset=utf-8" }, headers || {});
    return {
        status,
        headers: { get: name => all[Object.keys(all).find(k => k.toLowerCase() === name.toLowerCase())] ?? null },
        text: () => Promise.resolve(body === undefined ? "" : JSON.stringify(body))
    };
}

function fakeFetch(answers) {
    const calls = [];
    const fn = (url, init) => {
        calls.push({ url, init });
        const answer = answers.shift();
        return typeof answer === "function" ? answer(url, init) : Promise.resolve(answer);
    };
    fn.calls = calls;
    return fn;
}

test("a write sends JSON with the page's antiforgery token and returns the draft", async () => {
    const fetch = fakeFetch([response(200, fixture.codeSent)]);
    const client = signupApi.create({ endpoints, fetch, token: () => "token-1" });

    const result = await client.savePlan({ planCode: "Pro", billingPeriod: "Monthly" });

    assert.equal(result.ok, true);
    assert.equal(result.data.plan.planCode, "Pro");
    const call = fetch.calls[0];
    assert.equal(call.url, "/signup/draft/plan");
    assert.equal(call.init.method, "PUT");
    assert.equal(call.init.credentials, "same-origin");
    assert.equal(call.init.headers.RequestVerificationToken, "token-1");
    assert.equal(call.init.headers["Content-Type"], "application/json");
    assert.deepEqual(JSON.parse(call.init.body), { planCode: "Pro", billingPeriod: "Monthly" });
});

test("reads send no token and no body", async () => {
    const fetch = fakeFetch([response(200, fixture.phoneStatuses.locked)]);
    const client = signupApi.create({ endpoints, fetch, token: "secret" });

    const result = await client.getPhone();

    assert.equal(result.ok, true);
    assert.equal(fetch.calls[0].init.method, "GET");
    assert.equal(fetch.calls[0].init.body, undefined);
    assert.equal(fetch.calls[0].init.headers.RequestVerificationToken, undefined);
});

test("a JSON error is an api failure with its code, message and retry time", async () => {
    const fetch = fakeFetch([response(429, fixture.errors.tooMany, { "Retry-After": "120" })]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });

    const result = await client.sendCode();

    assert.equal(result.ok, false);
    assert.equal(result.kind, "api");
    assert.equal(result.status, 429);
    assert.equal(result.error.code, "Signup.TooManyAttempts");
    assert.equal(result.retryAfterSeconds, 120);
});

test("400 without a JSON body is an antiforgery failure; 404 without a body means signup is closed", () => {
    assert.equal(signupApi.classify(400, "text/html", null, null).kind, "antiforgery");
    assert.equal(signupApi.classify(404, "", null, null).kind, "closed");
    assert.equal(signupApi.classify(500, "text/html", null, "30").kind, "server");
    assert.equal(signupApi.classify(503, "", null, "30").retryAfterSeconds, 30);
    assert.equal(signupApi.classify(400, "application/json", fixture.errors.validation, null).kind, "api");
    assert.equal(signupApi.classify(204, "", null, null).ok, true);
});

test("a JSON body without an error code is not taken for an api error", () => {
    assert.equal(signupApi.classify(400, "application/json", { title: "Bad Request" }, null).kind, "antiforgery");
    assert.equal(signupApi.classify(502, "application/json", { unexpected: true }, null).kind, "server");
});

test("no answer is a network failure, an abort is reported as such", async () => {
    const fetch = fakeFetch([
        () => Promise.reject(new TypeError("Failed to fetch")),
        () => Promise.reject(Object.assign(new Error("aborted"), { name: "AbortError" }))
    ]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });

    assert.equal((await client.getDraft()).kind, "network");
    assert.equal((await client.checkAddress("sushim", {})).kind, "aborted");
});

test("writes run one after another, so the first save's draft cookie exists before the next write", async () => {
    const order = [];
    let releaseFirst;
    const fetch = fakeFetch([
        () => new Promise(resolve => { order.push("first started"); releaseFirst = () => resolve(response(200, fixture.codeSent)); }),
        () => { order.push("second started"); return Promise.resolve(response(200, fixture.codeSent)); }
    ]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });

    const first = client.savePlan({ planCode: "Pro", billingPeriod: "Monthly" });
    const second = client.saveRestaurant({ businessName: "Burger House" });
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(order, ["first started"]);

    releaseFirst();
    await first;
    await second;
    assert.deepEqual(order, ["first started", "second started"]);
});

test("a failed write does not block the next one", async () => {
    const fetch = fakeFetch([
        () => Promise.reject(new TypeError("offline")),
        response(200, fixture.phoneStatuses.verified)
    ]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });

    assert.equal((await client.verifyCode("123456")).kind, "network");
    const second = await client.verifyCode("482913");
    assert.equal(second.ok, true);
    assert.deepEqual(JSON.parse(fetch.calls[1].init.body), { code: "482913" });
});

test("the address check puts the label in the query, encoded", async () => {
    const fetch = fakeFetch([response(200, { slug: "sushim", status: "available" })]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });
    const signal = {};

    const result = await client.checkAddress("sushi m&x", signal);

    assert.equal(result.data.status, "available");
    assert.equal(fetch.calls[0].url, "/signup/address-availability?slug=sushi%20m%26x");
    assert.equal(fetch.calls[0].init.signal, signal);
});

test("the phone change and the submit use their own routes", async () => {
    const fetch = fakeFetch([response(200, fixture.phoneStatuses.notSentNow), response(200, { registrationId: "x", redirectUrl: "/signup/pending/x" })]);
    const client = signupApi.create({ endpoints, fetch, token: "t" });

    await client.changePhone("0532 123 45 67");
    const submitted = await client.submit();

    assert.equal(fetch.calls[0].url, "/signup/draft/phone");
    assert.equal(fetch.calls[0].init.method, "PUT");
    assert.deepEqual(JSON.parse(fetch.calls[0].init.body), { mobilePhone: "0532 123 45 67" });
    assert.equal(fetch.calls[1].url, "/signup/draft/submit");
    assert.equal(fetch.calls[1].init.method, "POST");
    assert.equal(submitted.data.redirectUrl, "/signup/pending/x");
});
