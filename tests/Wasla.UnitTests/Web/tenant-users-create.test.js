const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const root = path.join(__dirname, "..", "..", "..");
const create = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "tenant-users-create.js"));

function fakeElement() {
  const attributes = {};
  return {
    attributes,
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); },
    removeAttribute: (name) => { delete attributes[name]; }
  };
}

function fakeForm(isCreate = true) {
  const form = fakeElement();
  form.buttons = [fakeElement()];
  form.matches = (selector) => isCreate && selector === create.FORM_SELECTOR;
  form.querySelectorAll = () => form.buttons;
  return form;
}

function submitEvent(form, defaultPrevented = false) {
  return { target: form, defaultPrevented, prevented: false, preventDefault() { this.prevented = true; } };
}

function fakeTarget() {
  const listeners = {};
  return {
    listeners,
    addEventListener: (type, listener) => { (listeners[type] = listeners[type] || []).push(listener); },
    dispatch: (type, event) => (listeners[type] || []).forEach((listener) => listener(event))
  };
}

test("the Create user form is sent once: the first submission goes ahead, a second one while it is sent is cancelled", () => {
  const form = fakeForm();

  const first = submitEvent(form);
  assert.equal(create.handleSubmit(first), true);
  assert.equal(first.prevented, false);
  assert.equal(form.getAttribute("aria-busy"), "true");
  assert.equal(form.buttons[0].getAttribute("aria-disabled"), "true");

  const second = submitEvent(form);
  assert.equal(create.handleSubmit(second), false);
  assert.equal(second.prevented, true);
});

test("a submission another script already stopped was not sent, so the form stays usable", () => {
  const form = fakeForm();

  const stopped = submitEvent(form, true);
  assert.equal(create.handleSubmit(stopped), false);
  assert.equal(stopped.prevented, false);
  assert.equal(form.getAttribute("aria-busy"), null);
  assert.equal(form.buttons[0].getAttribute("aria-disabled"), null);

  const next = submitEvent(form);
  assert.equal(create.handleSubmit(next), true);
  assert.equal(next.prevented, false);
});

test("other forms on the page are never touched", () => {
  const other = fakeForm(false);
  const event = submitEvent(other);

  assert.equal(create.handleSubmit(event), true);
  assert.equal(event.prevented, false);
  assert.deepEqual(other.attributes, {});
  assert.deepEqual(other.buttons[0].attributes, {});
});

test("a page restored from the back/forward cache can be sent again; an ordinary page show changes nothing", () => {
  const form = fakeForm();
  const doc = fakeTarget();
  doc.querySelectorAll = (selector) => (selector === create.FORM_SELECTOR ? [form] : []);
  const win = fakeTarget();
  create.bind(doc, win);

  doc.dispatch("submit", submitEvent(form));
  assert.equal(form.getAttribute("aria-busy"), "true");

  win.dispatch("pageshow", { persisted: false });
  assert.equal(form.getAttribute("aria-busy"), "true");

  win.dispatch("pageshow", { persisted: true });
  assert.equal(form.getAttribute("aria-busy"), null);
  assert.equal(form.buttons[0].getAttribute("aria-disabled"), null);

  const again = submitEvent(form);
  doc.dispatch("submit", again);
  assert.equal(again.prevented, false);
  assert.equal(form.getAttribute("aria-busy"), "true");
});
