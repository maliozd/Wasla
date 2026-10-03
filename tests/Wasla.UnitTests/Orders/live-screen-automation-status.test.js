const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const status = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-automation-status.js");

// Localized copy comes from the page's messages; the test uses markers so it never depends on a culture.
const MESSAGES = {
  automationStatusSyncActive: "[sync on]",
  automationStatusSyncDisabled: "[sync off]",
  automationStatusAutoApproveActive: "[approve on]",
  automationStatusAutoApproveDisabled: "[approve off]",
  automationStatusReceiptPrintingActive: "[receipt on]",
  automationStatusReceiptPrintingDisabled: "[receipt off]",
  automationStatusPendingSetup: "[after setup]"
};
const message = (key) => MESSAGES[key];

function element(id, className) {
  const classes = new Set(className ? className.split(" ") : []);
  const attributes = new Map();
  return {
    id: id,
    textContent: "…",
    hidden: false,
    classList: {
      add: (...names) => names.forEach((name) => classes.add(name)),
      remove: (...names) => names.forEach((name) => classes.delete(name)),
      contains: (name) => classes.has(name)
    },
    classes: classes,
    setAttribute: (name, value) => attributes.set(name, String(value)),
    getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null),
    removeAttribute: (name) => attributes.delete(name)
  };
}

/** The settings menu as LiveDisplay.cshtml renders it: three muted indicators showing "…" and a hidden explanation. */
function settingsMenu() {
  const muted = "wasla-live-settings-menu__value wasla-orders-status-chip--muted";
  const els = {
    automationStatusSync: element("automationStatusSync", muted),
    automationStatusAutoApprove: element("automationStatusAutoApprove", muted),
    automationStatusReceipt: element("automationStatusReceipt", muted),
    automationPendingSetupHint: element("automationPendingSetupHint", "wasla-live-settings-menu__hint")
  };
  els.automationPendingSetupHint.hidden = true;
  els.automationPendingSetupHint.textContent = "[turns on when setup is completed or skipped]";
  const listeners = {};
  return {
    els: els,
    getElementById: (id) => els[id] || null,
    addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
    dispatch: (type, detail) => (listeners[type] || []).forEach((fn) => fn({ detail: detail }))
  };
}

const ALL_CLASSES = ["wasla-orders-status-chip--active", "wasla-orders-status-chip--muted", "wasla-orders-status-chip--pending"];
function chipClass(el) {
  return ALL_CLASSES.filter((name) => el.classes.has(name));
}

test("configured on in Setup: a pending indicator with its own words and look, described by the visible explanation", () => {
  const doc = settingsMenu();
  assert.equal(status.apply(doc, { orderSync: "Active", autoApprove: "PendingSetup", autoReceipt: "Off" }, message), true);

  const { automationStatusSync: sync, automationStatusAutoApprove: approve, automationStatusReceipt: receipt, automationPendingSetupHint: hint } = doc.els;
  assert.equal(sync.textContent, "[sync on]");
  assert.deepEqual(chipClass(sync), ["wasla-orders-status-chip--active"], "Setup does not make synchronization look disabled");
  assert.equal(approve.textContent, "[after setup]");
  assert.deepEqual(chipClass(approve), ["wasla-orders-status-chip--pending"], "neither the green Active nor the Off look");
  assert.notEqual(approve.textContent, "[approve on]");
  assert.notEqual(approve.textContent, "[approve off]");
  assert.equal(approve.getAttribute("aria-describedby"), "automationPendingSetupHint");
  assert.equal(receipt.textContent, "[receipt off]");
  assert.deepEqual(chipClass(receipt), ["wasla-orders-status-chip--muted"], "configured off stays Off");
  assert.equal(receipt.getAttribute("aria-describedby"), null);
  assert.equal(sync.getAttribute("aria-describedby"), null);
  assert.equal(hint.hidden, false, "the explanation is visible text, not only a tooltip or a color");
});

test("configured on and Live: Active, and the explanation goes away", () => {
  const doc = settingsMenu();
  status.apply(doc, { orderSync: "Active", autoApprove: "PendingSetup", autoReceipt: "PendingSetup" }, message);
  const approve = doc.els.automationStatusAutoApprove;

  status.apply(doc, { orderSync: "Active", autoApprove: "Active", autoReceipt: "Active" }, message);

  assert.equal(doc.els.automationStatusAutoApprove, approve, "the same element is updated, nothing is rebuilt");
  assert.equal(approve.textContent, "[approve on]");
  assert.deepEqual(chipClass(approve), ["wasla-orders-status-chip--active"]);
  assert.equal(approve.getAttribute("aria-describedby"), null);
  assert.equal(doc.els.automationStatusReceipt.textContent, "[receipt on]");
  assert.equal(doc.els.automationPendingSetupHint.hidden, true);
});

test("configured off: Off in Setup and in Live, never pending", () => {
  for (const mode of ["Setup", "Live"]) {
    const doc = settingsMenu();
    status.apply(doc, { orderSync: "Off", autoApprove: "Off", autoReceipt: "Off" }, message);
    assert.equal(doc.els.automationStatusSync.textContent, "[sync off]", mode);
    assert.equal(doc.els.automationStatusAutoApprove.textContent, "[approve off]", mode);
    assert.equal(doc.els.automationStatusReceipt.textContent, "[receipt off]", mode);
    for (const id of ["automationStatusSync", "automationStatusAutoApprove", "automationStatusReceipt"])
      assert.deepEqual(chipClass(doc.els[id]), ["wasla-orders-status-chip--muted"], mode);
    assert.equal(doc.els.automationPendingSetupHint.hidden, true, mode);
  }
});

test("a snapshot without an automation section (the status could not be read) changes nothing", () => {
  const doc = settingsMenu();
  assert.equal(status.apply(doc, null, message), false);
  assert.equal(status.apply(doc, undefined, message), false);
  for (const id of ["automationStatusSync", "automationStatusAutoApprove", "automationStatusReceipt"]) {
    assert.equal(doc.els[id].textContent, "…");
    assert.deepEqual(chipClass(doc.els[id]), ["wasla-orders-status-chip--muted"]);
  }
  assert.equal(doc.els.automationPendingSetupHint.hidden, true);
});

test("an unknown state leaves that indicator as it is", () => {
  const doc = settingsMenu();
  status.apply(doc, { orderSync: "Active", autoApprove: "Active", autoReceipt: "Active" }, message);
  status.apply(doc, { orderSync: "Paused", autoApprove: "", autoReceipt: 1 }, message);
  assert.equal(doc.els.automationStatusSync.textContent, "[sync on]");
  assert.equal(doc.els.automationStatusAutoApprove.textContent, "[approve on]");
  assert.equal(doc.els.automationStatusReceipt.textContent, "[receipt on]");
  assert.equal(status.stateView("Paused", { active: "a", off: "o", pending: "p" }), null);
});

test("each poll's rendered snapshot updates the indicators: Setup → Live in another tab shows without a reload", () => {
  const doc = settingsMenu();
  status.boot(doc, { getMessage: message });
  doc.dispatch("wasla:live-rendered", { orders: [], training: null, automation: { orderSync: "Active", autoApprove: "PendingSetup", autoReceipt: "PendingSetup" } });
  assert.equal(doc.els.automationStatusAutoApprove.textContent, "[after setup]");
  assert.equal(doc.els.automationPendingSetupHint.hidden, false);

  doc.dispatch("wasla:live-rendered", { orders: [], training: null, automation: { orderSync: "Active", autoApprove: "Active", autoReceipt: "Active" } });
  assert.equal(doc.els.automationStatusAutoApprove.textContent, "[approve on]");
  assert.equal(doc.els.automationStatusReceipt.textContent, "[receipt on]");
  assert.equal(doc.els.automationPendingSetupHint.hidden, true);

  // A later snapshot without the section (a failed status read) keeps the last state.
  doc.dispatch("wasla:live-rendered", { orders: [], training: null, automation: null });
  assert.equal(doc.els.automationStatusAutoApprove.textContent, "[approve on]");
});

test("the indicator script only displays the server's answer: no request, no realtime transport, no page inference", () => {
  const source = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/wwwroot/js/orders/orders-automation-status.js"), "utf8");
  for (const forbidden of ["fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "signalR", "HubConnection", "querySelector", "WaslaGuidedTraining", "localStorage"])
    assert.equal(source.includes(forbidden), false, forbidden);
});
