const test = require("node:test");
const assert = require("node:assert/strict");

const code = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-code-input.js");

function fakeInput(value) {
    const listeners = {};
    return {
        value: value || "",
        selection: null,
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        removeEventListener: (type, fn) => { listeners[type] = (listeners[type] || []).filter(f => f !== fn); },
        setSelectionRange(start, end) { this.selection = [start, end]; },
        fire(type, event) { (listeners[type] || []).forEach(fn => fn(event || {})); },
        count: type => (listeners[type] || []).length
    };
}

function pasteEvent(text) {
    return {
        prevented: false,
        clipboardData: { getData: () => text },
        preventDefault() { this.prevented = true; }
    };
}

test("separators and spaces from a message or a password manager are dropped", () => {
    assert.equal(code.normalize("482 913"), "482913");
    assert.equal(code.normalize("482-913"), "482913");
    assert.equal(code.normalize(" 48.29-13 "), "482913");
    assert.equal(code.normalize("Kod: 482913"), "482913");
    assert.equal(code.normalize(null), "");
});

test("Arabic-Indic, Persian and full-width digits read as Latin digits", () => {
    assert.equal(code.normalize("٤٨٢٩١٣"), "482913");
    assert.equal(code.normalize("۴۸۲ ۹۱۳"), "482913");
    assert.equal(code.normalize("４８２９１３"), "482913");
});

test("the server's code length caps the value; without one any length is kept", () => {
    assert.equal(code.normalize("1234567890", 6), "123456");
    assert.equal(code.normalize("1234567890"), "1234567890");
    assert.equal(code.normalize("1234567890", 0), "1234567890");
});

test("a code is complete only at the server's length and only with digits", () => {
    assert.equal(code.isComplete("482913", 6), true);
    assert.equal(code.isComplete("48291", 6), false);
    assert.equal(code.isComplete("482913", undefined), false);
    assert.equal(code.isComplete("48291a", 6), false);
});

test("typing keeps the field to digits and reports a full code once", () => {
    const input = fakeInput();
    const completed = [];
    code.bind(input, { length: 6, onComplete: value => completed.push(value) });

    input.value = "48 29";
    input.fire("input");
    assert.equal(input.value, "4829");
    assert.deepEqual(input.selection, [4, 4]);

    input.value = "482913";
    input.fire("input");
    input.fire("input");
    assert.deepEqual(completed, ["482913"]);

    input.value = "48291";
    input.fire("input");
    input.value = "482913";
    input.fire("input");
    assert.deepEqual(completed, ["482913", "482913"], "editing and completing again reports again");
});

test("a pasted code replaces the field, cleaned and capped", () => {
    const input = fakeInput("12");
    const completed = [];
    code.bind(input, { length: () => 6, onComplete: value => completed.push(value) });

    const event = pasteEvent("Kodunuz: 482-913. Kimseyle paylaşmayın.");
    input.fire("paste", event);

    assert.equal(event.prevented, true);
    assert.equal(input.value, "482913");
    assert.deepEqual(completed, ["482913"]);
});

test("unbinding removes the listeners", () => {
    const input = fakeInput();
    const unbind = code.bind(input, { length: 6 });
    unbind();

    assert.equal(input.count("input"), 0);
    assert.equal(input.count("paste"), 0);
});

test("WebOTP is used only where the browser offers it, and a refusal is silent", async () => {
    assert.equal(code.supportsWebOtp({ navigator: {} }), false);
    assert.equal(await code.requestWebOtp({ navigator: {} }), null);

    let request = null;
    const supported = {
        OTPCredential: function () { },
        navigator: { credentials: { get: options => { request = options; return Promise.resolve({ code: "482913" }); } } }
    };
    const signal = {};
    assert.equal(code.supportsWebOtp(supported), true);
    assert.equal(await code.requestWebOtp(supported, signal), "482913");
    assert.deepEqual(request.otp, { transport: ["sms"] });
    assert.equal(request.signal, signal);

    const aborted = {
        OTPCredential: function () { },
        navigator: { credentials: { get: () => Promise.reject(Object.assign(new Error("aborted"), { name: "AbortError" })) } }
    };
    assert.equal(await code.requestWebOtp(aborted, signal), null);
});
