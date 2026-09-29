const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const guided = require(path.join(__dirname, "..", "..", "..", "src", "Wasla.Web", "wwwroot", "js", "wasla-guided-setup.js"));

function fakeForm(isGuided = true) {
  const attributes = {};
  const buttons = [{ disabled: false }, { disabled: false }];
  return {
    buttons,
    matches: (selector) => isGuided && selector === "form[data-guided-setup-form]",
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); },
    removeAttribute: (name) => { delete attributes[name]; },
    querySelectorAll: () => buttons
  };
}

function submitEvent(form) {
  return { target: form, prevented: false, preventDefault() { this.prevented = true; } };
}

test("a guided-setup form submits once; a second click is ignored while the first is running", () => {
  const form = fakeForm();

  const first = submitEvent(form);
  assert.equal(guided.handleSubmit(first), true);
  assert.equal(first.prevented, false);
  assert.equal(form.getAttribute("aria-busy"), "true");
  assert.ok(form.buttons.every((button) => button.disabled));

  const second = submitEvent(form);
  assert.equal(guided.handleSubmit(second), false);
  assert.equal(second.prevented, true);
});

test("other forms on the page are never touched", () => {
  const other = fakeForm(false);
  const event = submitEvent(other);

  assert.equal(guided.handleSubmit(event), true);
  assert.equal(event.prevented, false);
  assert.equal(other.getAttribute("aria-busy"), null);
  assert.ok(other.buttons.every((button) => !button.disabled));
});

test("a page restored from the back/forward cache can submit again", () => {
  const form = fakeForm();
  guided.handleSubmit(submitEvent(form));

  guided.resetAll({ querySelectorAll: () => [form] });

  assert.equal(form.getAttribute("aria-busy"), "false");
  assert.ok(form.buttons.every((button) => !button.disabled));
  const again = submitEvent(form);
  assert.equal(guided.handleSubmit(again), true);
  assert.equal(again.prevented, false);
});

test("opening the End dialog focuses its safe choice and changes no state", () => {
  let focused = null;
  const cancel = { focus: () => { focused = "cancel"; } };
  const dialog = {
    hasAttribute: (name) => name === "data-guided-setup-dialog",
    querySelector: (selector) => (selector === "[data-guided-setup-cancel]" ? cancel : null)
  };

  guided.focusSafeChoice({ target: dialog });

  assert.equal(focused, "cancel");
});

test("other dialogs keep Bootstrap's own focus handling", () => {
  let focused = false;
  const dialog = {
    hasAttribute: () => false,
    querySelector: () => ({ focus: () => { focused = true; } })
  };

  guided.focusSafeChoice({ target: dialog });

  assert.equal(focused, false);
});

test("Cancel and Escape have no guided-setup handler: only the confirm form posts", () => {
  const source = require("node:fs").readFileSync(
    path.join(__dirname, "..", "..", "..", "src", "Wasla.Web", "wwwroot", "js", "wasla-guided-setup.js"), "utf8");

  assert.equal(/fetch\(|XMLHttpRequest|\.submit\(\)|requestSubmit/.test(source), false);
  assert.equal(/hide\.bs\.modal|hidden\.bs\.modal|keydown/.test(source), false);
});
