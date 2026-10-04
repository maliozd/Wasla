const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const scriptPath = path.join(__dirname, "../../../src/Wasla.Web/wwwroot/js/wasla-guided-training.js");
const training = require(scriptPath);

const STEPS = ["intro", "practice-new", "practice-accepted", "practice-preparing", "practice-ready", "practice-on-the-way", "practice-delivered"];

// A small DOM: enough selectors (.class, [attr], [attr="v"], descendant) for the training controller.
class FakeClassList {
  constructor(el) { this.el = el; this.set = new Set(); }
  add(...names) { names.forEach((n) => this.set.add(n)); }
  remove(...names) { names.forEach((n) => this.set.delete(n)); }
  contains(name) { return this.set.has(name); }
  toggle(name, force) {
    const on = force === undefined ? !this.set.has(name) : !!force;
    if (on) this.set.add(name); else this.set.delete(name);
    return on;
  }
}

class FakeElement {
  constructor(doc, tagName, attrs = {}) {
    this.ownerDocument = doc;
    this.tagName = tagName.toUpperCase();
    this.attrs = new Map();
    this.children = [];
    this.parent = null;
    this.classList = new FakeClassList(this);
    this.listeners = {};
    this._text = "";
    this.hidden = false;
    this.isContentEditable = false;
    for (const [name, value] of Object.entries(attrs)) {
      if (name === "class") value.split(/\s+/).filter(Boolean).forEach((c) => this.classList.add(c));
      else if (name === "hidden") this.hidden = !!value;
      else this.attrs.set(name, String(value));
    }
  }
  get parentNode() { return this.parent; }
  get textContent() { return this._text + this.children.map((c) => c.textContent).join(""); }
  set textContent(value) { this._text = String(value); this.children = []; }
  setAttribute(n, v) { if (n === "class") return; this.attrs.set(n, String(v)); }
  getAttribute(n) { return this.attrs.has(n) ? this.attrs.get(n) : null; }
  hasAttribute(n) { return this.attrs.has(n); }
  removeAttribute(n) { this.attrs.delete(n); }
  append(...kids) { kids.forEach((k) => { k.parent = this; this.children.push(k); }); return this; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter((c) => c !== this); this.parent = null; }
  contains(node) { for (let n = node; n; n = n.parent) if (n === this) return true; return false; }
  descendants() { const out = []; const walk = (e) => e.children.forEach((c) => { out.push(c); walk(c); }); walk(this); return out; }
  querySelectorAll(selector) { return this.descendants().filter((e) => matchesList(e, selector)); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  matches(selector) { return matchesList(this, selector); }
  closest(selector) { for (let n = this; n; n = n.parent) if (n.matches && n.matches(selector)) return n; return null; }
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  dispatch(type, event) { (this.listeners[type] || []).forEach((fn) => fn(event)); }
  click() { const event = { type: "click", target: this }; for (let n = this; n; n = n.parent) n.dispatch("click", event); }
  focus() { this.ownerDocument.activeElement = this; }
  scrollIntoView() { this.scrolled = (this.scrolled || 0) + 1; }
}

function compoundMatches(el, compound) {
  const re = /\.([\w-]+)|\[([\w-]+)(?:=["']?([^"'\]]*)["']?)?\]|^([a-z]+)/gi;
  let m;
  let consumed = 0;
  while ((m = re.exec(compound))) {
    consumed += m[0].length;
    if (m[1] && !el.classList.contains(m[1])) return false;
    if (m[2] && (!el.hasAttribute(m[2]) || (m[3] !== undefined && el.getAttribute(m[2]) !== m[3]))) return false;
    if (m[4] && el.tagName !== m[4].toUpperCase()) return false;
  }
  if (consumed !== compound.length) throw new Error("unsupported selector: " + compound);
  return true;
}

function matchesOne(el, selector) {
  const parts = selector.trim().split(/\s+(?![^[]*\])/);
  if (!compoundMatches(el, parts[parts.length - 1])) return false;
  let node = el.parent;
  for (let i = parts.length - 2; i >= 0; i--) {
    while (node && !(node.matches && compoundMatches(node, parts[i]))) node = node.parent;
    if (!node) return false;
    node = node.parent;
  }
  return true;
}

function matchesList(el, selector) {
  return selector.split(",").some((s) => matchesOne(el, s));
}

/** Whether an element is actually shown: neither it nor an ancestor is hidden. */
function isShown(el) {
  for (let n = el; n; n = n.parent) if (n.hidden) return false;
  return true;
}

const REAL_ORDERS = {
  titleOne: "A real order arrived while you were training",
  titleMany: "{0} real orders arrived while you were training",
  bodyOne: "You can continue training. You can manage the order from your platform panel.",
  bodyMany: "You can continue training. You can manage the orders from your platform panel."
};

/** One Live Screen page view with the training panel, mirroring _GuidedTrainingPanel.cshtml's hooks. */
function page(options = {}) {
  const doc = new FakeElement(null, "#document");
  doc.ownerDocument = doc;
  doc.readyState = "complete";
  doc.activeElement = null;
  doc.getElementById = (id) => doc.querySelectorAll("[id]").find((e) => e.getAttribute("id") === id) || null;
  const el = (tag, attrs, ...kids) => new FakeElement(doc, tag, attrs).append(...kids);
  const practiceSteps = STEPS.slice(1).join(" ");
  const step = options.step || "intro";
  const hiddenFor = (when) => !(" " + when + " ").includes(" " + step + " ");

  const section = el("section", { id: "waslaGuidedTraining", "data-guided-training": "" },
    el("div", { "data-training-panel": "" },
      el("p", { "data-training-progress": "" }),
      el("h2", { "data-training-title": "" }),
      el("p", { "data-training-body": "" }),
      el("p", { "data-training-waiting": "", hidden: true }),
      el("div", { "data-training-actions": "" },
        el("div", { "data-slot": "practice", "data-training-when": "intro", hidden: hiddenFor("intro") },
          el("button", { type: "submit", "data-control": "start-practice" })),
        el("div", { "data-slot": "complete", "data-training-when": "practice-delivered", hidden: hiddenFor("practice-delivered") },
          el("button", { type: "submit", "data-control": "complete" })),
        el("button", { "data-training-show-practice": "", "data-training-needs-practice": "", "data-training-when": practiceSteps, hidden: hiddenFor(practiceSteps) }),
        el("button", { "data-training-hide": "" }),
        el("button", { "data-end": "", "data-bs-toggle": "modal" })),
      el("div", { role: "status", "aria-live": "polite", "aria-atomic": "true", "data-training-real-orders": "" },
        el("div", { "data-training-real-orders-box": "", hidden: true },
          el("p", { "data-training-real-orders-title": "" }),
          el("p", { "data-training-real-orders-body": "" }))),
      el("p", { "data-note": "courier", "data-training-when": "practice-ready practice-on-the-way" })),
    el("button", { "data-training-reopen": "", hidden: true }),
    el("p", { "data-training-announce": "" }));

  const host = el("div", { id: "ordersLiveScreenHost" });
  const body = el("body", {}, section, host);
  doc.append(body);
  doc.body = body;

  const timers = [];
  const selected = [];
  let view = options.view || "board";
  const win = {
    setTimeout: (fn) => { timers.push(fn); return timers.length; },
    clearTimeout: () => {},
    requestAnimationFrame: (fn) => fn(),
    matchMedia: () => ({ matches: false }),
    WaslaOrders: {
      liveStore: { selectOrder: (id, fromUser) => selected.push([id, fromUser]) },
      liveView: { getView: () => view }
    }
  };

  const copy = {};
  STEPS.forEach((key, i) => {
    copy[key] = {
      title: "Title " + key,
      body: "Body " + key,
      progress: "Step " + (i + 1) + " of " + STEPS.length,
      action: { "practice-new": "approve", "practice-accepted": "start-preparing", "practice-preparing": "mark-ready" }[key] || null
    };
  });
  const config = { step, steps: STEPS, copy, realOrders: REAL_ORDERS };

  const announcements = [];
  const announcer = section.querySelector("[data-training-announce]");
  const flush = () => {
    // One batch: the waiting hint re-arms its own timer, which must not run in a loop here.
    timers.splice(0).forEach((fn) => fn());
    if (announcer.textContent) announcements.push(announcer.textContent);
    announcer.textContent = "";
  };

  function demoCard(status, id = "demo-1") {
    const card = el("article", { "data-order-id": id, "data-order-status": status, "data-wasla-demo": "true", "data-wasla-demo-card": "true" });
    const action = { New: "approve", Accepted: "start-preparing", Preparing: "mark-ready" }[status];
    if (action) card.append(el("button", { "data-order-action": action, "data-order-id": id, "data-wasla-demo": "true" }));
    return card;
  }

  function realCard(id, status) {
    return el("article", { "data-order-id": id, "data-order-status": status },
      el("button", { "data-order-action": "approve", "data-order-id": id }));
  }

  /**
   * Renders a snapshot as the Live Screen does: cards default to one per order, and the event carries the snapshot's
   * training section (null unless the server isolated this trainee).
   */
  function render(orders, cards, trainingSection = null) {
    host.children.slice().forEach((c) => c.remove());
    const list = cards || orders.map((o) => (o.isDemo ? demoCard(o.status, o.id) : realCard(o.id, o.status)));
    list.forEach((c) => host.append(c));
    doc.dispatch("wasla:live-rendered", { detail: { orders, training: trainingSection } });
    flush();
  }

  const controller = training.createController(doc, win, config);
  const q = (selector) => section.querySelector(selector);
  const banner = () => ({
    box: q("[data-training-real-orders-box]"),
    region: q("[data-training-real-orders]"),
    title: q("[data-training-real-orders-title]").textContent,
    body: q("[data-training-real-orders-body]").textContent
  });
  return {
    doc, win, section, host, controller, announcements, selected, render, demoCard, realCard, flush, q, banner,
    setView: (v) => { view = v; },
    el
  };
}

const demo = (status, id = "demo-1") => ({ id, status, isDemo: true });
const real = (id, status) => ({ id, status, isDemo: false });
const isolated = (count) => ({ isolated: true, realOrdersReceived: count });

// Pure rules ------------------------------------------------------------------------------------

test("training moves only forward, so a late or stale snapshot never resets it", () => {
  assert.equal(training.nextStep(STEPS, "intro", demo("New")), "practice-new");
  assert.equal(training.nextStep(STEPS, "practice-preparing", demo("Accepted")), "practice-preparing");
  assert.equal(training.nextStep(STEPS, "practice-ready", demo("OnTheWay")), "practice-on-the-way");
  assert.equal(training.nextStep(STEPS, "practice-on-the-way", null), "practice-on-the-way", "a missing demo changes nothing");
  assert.equal(training.nextStep(STEPS, "practice-new", demo("Cancelled")), "practice-new");
  assert.equal(training.nextStep(STEPS, "practice-on-the-way", demo("Delivered")), "practice-delivered");
});

test("before the practice starts, an earlier delivered demo cannot jump to completion", () => {
  assert.equal(training.nextStep(STEPS, "intro", demo("Delivered")), "intro");
});

test("the real-order count comes only from an isolated trainee's snapshot", () => {
  assert.equal(training.realOrderCount(isolated(3)), 3);
  assert.equal(training.realOrderCount(isolated(0)), 0);
  assert.equal(training.realOrderCount(null), 0, "a normal snapshot (e.g. after the tenant went live)");
  assert.equal(training.realOrderCount({ isolated: false, realOrdersReceived: 4 }), 0);
  assert.equal(training.realOrderCount({ isolated: true, realOrdersReceived: "4" }), 0);
  assert.equal(training.realOrderCount({ isolated: true, realOrdersReceived: -1 }), 0);
});

test("the real-orders copy has a singular, a plural with the count filled in, and nothing at zero", () => {
  assert.equal(training.realOrdersText(REAL_ORDERS, 0), null);
  assert.deepEqual(training.realOrdersText(REAL_ORDERS, 1), { title: REAL_ORDERS.titleOne, body: REAL_ORDERS.bodyOne });
  assert.deepEqual(training.realOrdersText(REAL_ORDERS, 4), { title: "4 real orders arrived while you were training", body: REAL_ORDERS.bodyMany });
});

test("Escape closes the panel only when no dialog, menu or text field owns the key", () => {
  const doc = { querySelector: () => null };
  const openModal = { querySelector: (s) => (s.includes(".modal.show") ? {} : null) };
  assert.equal(training.escapeClosesPanel({ key: "Escape", target: { tagName: "BUTTON" } }, doc), true);
  assert.equal(training.escapeClosesPanel({ key: "Enter", target: { tagName: "BUTTON" } }, doc), false);
  assert.equal(training.escapeClosesPanel({ key: "Escape", target: { tagName: "INPUT" } }, doc), false);
  assert.equal(training.escapeClosesPanel({ key: "Escape", target: { tagName: "BUTTON" }, defaultPrevented: true }, doc), false);
  assert.equal(training.escapeClosesPanel({ key: "Escape", target: { tagName: "BUTTON" } }, openModal), false);
});

test("step-scoped controls follow data-training-when", () => {
  assert.equal(training.shownOn("intro", "intro"), true);
  assert.equal(training.shownOn("practice-new practice-ready", "practice-ready"), true);
  assert.equal(training.shownOn("practice-new", "practice-new-extra"), false);
  assert.equal(training.shownOn(null, "anything"), true);
});

test("the script never posts, fetches, stores, pauses for real orders or mounts the legacy tour", () => {
  const source = fs.readFileSync(scriptPath, "utf8");
  for (const forbidden of ["fetch(", "XMLHttpRequest", "sendBeacon", "ProductTour", "wasla-tour", "data-wasla-tour-replay", "/orders/demo",
    "hand-to-courier", "mark-delivered", ".submit(", "localStorage", "sessionStorage", "data-training-pause", "data-training-resume",
    "data-training-show-active", "acknowledge", "state.paused", "applyPause", "real-order-arrived", "HubConnection", "EventSource", "WebSocket"]) {
    assert.equal(source.includes(forbidden), false, forbidden);
  }
});

// Steps, pointer and announcements -----------------------------------------------------------------

test("snapshots advance the visible step, update the copy and announce each step once", () => {
  const p = page({ step: "intro" });
  p.render([]);
  assert.equal(p.section.getAttribute("data-step"), "intro");
  assert.equal(isShown(p.q("[data-slot='practice']")), true);
  assert.equal(isShown(p.q("[data-slot='complete']")), false);

  for (let i = 0; i < 3; i++) p.render([demo("New")]);
  assert.equal(p.section.getAttribute("data-step"), "practice-new");
  assert.equal(p.q("[data-training-title]").textContent, "Title practice-new");
  assert.equal(p.q("[data-training-progress]").textContent, "Step 2 of 7");
  assert.deepEqual(p.announcements, ["Title practice-new"], "polling the same state does not repeat the announcement");

  p.render([demo("ReadyForPickup")]);
  p.render([demo("Accepted")]);
  assert.equal(p.section.getAttribute("data-step"), "practice-ready", "a stale snapshot cannot move training back");

  p.render([]);
  assert.equal(p.section.getAttribute("data-step"), "practice-ready", "a snapshot without the demo does not reset training");

  p.render([demo("OnTheWay")]);
  p.render([demo("Delivered")]);
  assert.equal(isShown(p.q("[data-slot='complete']")), true);
  assert.equal(isShown(p.q("[data-slot='practice']")), false);
  assert.deepEqual(p.announcements, ["Title practice-new", "Title practice-ready", "Title practice-on-the-way", "Title practice-delivered"]);
});

test("the step actions are available at once: nothing waits for real orders any more", () => {
  const p = page({ step: "intro" });
  assert.equal(isShown(p.q("[data-slot='practice']")), true, "Start practice is there before the first snapshot");
  assert.equal(isShown(p.q("[data-training-body]")), true);
});

test("the pointer marks the practice order and the action it needs, and survives DOM replacement", () => {
  const p = page({ step: "practice-new" });
  const first = p.demoCard("New");
  p.render([demo("New"), real("r1", "Delivered")], [first, p.realCard("r1", "Delivered")]);
  assert.equal(first.classList.contains("wasla-guided-training-target"), true);
  assert.equal(first.querySelector("[data-order-action]").classList.contains("wasla-guided-training-target--action"), true);
  assert.equal(p.host.querySelector("[data-order-id='r1']").classList.contains("wasla-guided-training-target"), false, "real orders are never marked");

  const next = p.demoCard("Accepted");
  p.render([demo("Accepted")], [next]);
  assert.equal(p.host.contains(first), false);
  assert.equal(next.classList.contains("wasla-guided-training-target"), true);
  assert.equal(next.querySelector("[data-order-action='start-preparing']").classList.contains("wasla-guided-training-target--action"), true);
  assert.equal(p.host.querySelectorAll(".wasla-guided-training-target").length, 1);

  const ready = p.demoCard("ReadyForPickup");
  p.render([demo("ReadyForPickup")], [ready]);
  assert.equal(ready.classList.contains("wasla-guided-training-target"), true);
  assert.equal(p.host.querySelectorAll(".wasla-guided-training-target--action").length, 0, "no restaurant action after Ready");
});

test("Focus view marks the queue entry and Show practice order selects it", () => {
  const p = page({ step: "practice-new", view: "focus" });
  const entry = p.el("button", { "data-focus-select": "", "data-wasla-demo": "true", "data-order-id": "demo-1" });
  p.render([demo("New")], [entry]);
  assert.equal(entry.classList.contains("wasla-guided-training-target"), true);

  p.q("[data-training-show-practice]").click();
  assert.deepEqual(p.selected, [["demo-1", true]]);
  assert.equal(p.doc.activeElement, entry);
});

// Real orders during training: counted, never interrupting ---------------------------------------------

test("an automation status change alone never moves, resets or announces training", () => {
  const p = page({ step: "practice-accepted" });
  const orders = [demo("Accepted")];
  p.render(orders);
  const step = p.section.getAttribute("data-step");
  const title = p.q("[data-training-title]").textContent;
  const announced = p.announcements.slice();

  const pending = { orderSync: "Active", autoApprove: "PendingSetup", autoReceipt: "PendingSetup" };
  const active = { orderSync: "Active", autoApprove: "Active", autoReceipt: "Active" };
  for (const automation of [pending, active, null, active]) {
    p.doc.dispatch("wasla:live-rendered", { detail: { orders, training: null, automation } });
    p.flush();
  }

  assert.equal(p.section.getAttribute("data-step"), step);
  assert.equal(p.q("[data-training-title]").textContent, title);
  assert.deepEqual(p.announcements, announced, "nothing is announced for a status change");
  assert.equal(isShown(p.banner().box), false, "no real-order status line appears");
});

test("real orders never pause or move training, whatever their status", () => {
  for (const status of ["New", "Accepted", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered", "Cancelled"]) {
    const p = page({ step: "practice-accepted" });
    const card = p.demoCard("Accepted");
    p.render([demo("Accepted"), real("r1", status)], [card, p.realCard("r1", status)]);
    p.render([demo("Accepted"), real("r1", status), real("r2", "New")], [card, p.realCard("r1", status), p.realCard("r2", "New")]);

    assert.equal(p.section.getAttribute("data-step"), "practice-accepted", status);
    assert.equal(p.q("[data-training-title]").textContent, "Title practice-accepted", status);
    assert.equal(isShown(p.q("[data-training-show-practice]")), true, status);
    assert.equal(card.classList.contains("wasla-guided-training-target"), true, "the pointer stays on the practice order");
    assert.deepEqual(p.announcements, [], "nothing is announced for real orders");
  }
});

test("at zero the status line is not shown at all", () => {
  const p = page({ step: "practice-new" });
  p.render([demo("New")], null, isolated(0));

  const banner = p.banner();
  assert.equal(isShown(banner.box), false);
  assert.equal(banner.title, "");
  assert.equal(banner.body, "");
});

test("one real order shows the singular status line, as a polite status that never takes focus", () => {
  const p = page({ step: "practice-new" });
  const start = p.q("[data-training-hide]");
  start.focus();

  p.render([demo("New")], null, isolated(1));

  const banner = p.banner();
  assert.equal(isShown(banner.box), true);
  assert.equal(banner.title, REAL_ORDERS.titleOne);
  assert.equal(banner.body, REAL_ORDERS.bodyOne);
  assert.equal(banner.region.getAttribute("role"), "status");
  assert.equal(banner.region.getAttribute("aria-live"), "polite");
  assert.equal(p.doc.activeElement, start, "focus stays where the user left it");
  assert.equal(p.section.getAttribute("data-step"), "practice-new");
  assert.deepEqual(p.announcements, [], "the step announcer is not used; the live region reads the line itself");
});

test("more real orders update the same status line in place, never adding another", () => {
  const p = page({ step: "practice-new" });
  p.render([demo("New")], null, isolated(1));
  const box = p.banner().box;
  const boxesBefore = p.section.querySelectorAll("[data-training-real-orders-box]").length;

  p.render([demo("New")], null, isolated(2));
  assert.equal(p.banner().title, "2 real orders arrived while you were training");
  assert.equal(p.banner().body, REAL_ORDERS.bodyMany);
  p.render([demo("New")], null, isolated(5));
  p.render([demo("New")], null, isolated(5));

  assert.equal(p.banner().box, box, "the same element");
  assert.equal(p.section.querySelectorAll("[data-training-real-orders-box]").length, boxesBefore);
  assert.equal(p.section.querySelectorAll("[role='status']").length, 1, "one live region for the count");
  assert.equal(p.banner().title, "5 real orders arrived while you were training");
});

test("the status line shows only the count: no customer or order detail ever reaches it", () => {
  const p = page({ step: "practice-new" });
  const snapshotOrder = { id: "r-secret", status: "New", isDemo: false, customerName: "Ayşe Yılmaz", displayNumber: "TY-77" };
  p.render([demo("New"), snapshotOrder], null, isolated(1));

  const text = p.banner().title + " " + p.banner().body;
  for (const detail of ["Ayşe", "TY-77", "r-secret"]) assert.equal(text.includes(detail), false, detail);
});

test("once the tenant is live the snapshot is no longer isolated and the status line disappears", () => {
  const p = page({ step: "practice-preparing" });
  p.render([demo("Preparing")], null, isolated(3));
  assert.equal(isShown(p.banner().box), true);

  p.render([demo("Preparing"), real("r1", "New"), real("r2", "Preparing")], null, null);
  assert.equal(isShown(p.banner().box), false);
  assert.equal(p.banner().title, "");
  assert.equal(p.section.getAttribute("data-step"), "practice-preparing");
});

test("a hidden guide keeps its status line hidden with it, and reopening shows the latest count", () => {
  const p = page({ step: "practice-new" });
  p.render([demo("New")], null, isolated(1));
  p.q("[data-training-hide]").click();
  assert.equal(isShown(p.banner().box), false);

  p.render([demo("New")], null, isolated(2));
  p.q("[data-training-reopen]").click();
  assert.equal(isShown(p.banner().box), true);
  assert.equal(p.banner().title, "2 real orders arrived while you were training");
});

// Escape and focus --------------------------------------------------------------------------------

test("Escape hides the guide for this page view only and the resume button brings it back", () => {
  const p = page({ step: "practice-preparing" });
  const card = p.demoCard("Preparing");
  p.render([demo("Preparing")], [card]);
  const hideButton = p.q("[data-training-hide]");
  hideButton.focus();

  p.doc.dispatch("keydown", { key: "Escape", target: hideButton });
  assert.equal(p.q("[data-training-panel]").hidden, true);
  assert.equal(p.q("[data-training-reopen]").hidden, false);
  assert.equal(p.doc.activeElement, p.q("[data-training-reopen]"), "focus is not lost");
  assert.equal(card.classList.contains("wasla-guided-training-target"), false);
  assert.equal(p.section.getAttribute("data-step"), "practice-preparing", "the journey step is untouched");

  p.q("[data-training-reopen]").click();
  assert.equal(p.q("[data-training-panel]").hidden, false);
  assert.equal(card.classList.contains("wasla-guided-training-target"), true);

  // A fresh page view shows the guide again.
  const reloaded = page({ step: "practice-preparing" });
  reloaded.render([demo("Preparing")]);
  assert.equal(reloaded.q("[data-training-panel]").hidden, false);
});

test("Escape leaves an open dialog alone", () => {
  const p = page({ step: "practice-new" });
  p.doc.querySelector("body").append(p.el("div", { class: "modal show" }));
  p.doc.dispatch("keydown", { key: "Escape", target: p.doc });
  assert.equal(p.q("[data-training-panel]").hidden, false);
});

test("Escape that closes a Bootstrap dialog never hides the guide, even after Bootstrap removed \"show\"", () => {
  const p = page({ step: "practice-new" });
  p.render([demo("New")]);
  // Bootstrap 5.3 handles Escape on the dialog first: "show" is already gone when the key reaches the document.
  const cancel = p.el("button", { "data-guided-setup-cancel": "" });
  const dialog = p.el("div", { class: "modal fade", id: "waslaGuidedSetupEndDialog" }, cancel);
  p.doc.body.append(dialog);
  cancel.focus();

  p.doc.dispatch("keydown", { key: "Escape", target: cancel });
  assert.equal(p.q("[data-training-panel]").hidden, false, "cancelling the End dialog keeps the guide");

  // While Bootstrap is still hiding (body keeps "modal-open"), Escape belongs to the dialog too.
  p.doc.body.classList.add("modal-open");
  p.doc.dispatch("keydown", { key: "Escape", target: p.doc.body });
  assert.equal(p.q("[data-training-panel]").hidden, false);
  assert.equal(training.escapeClosesPanel({ key: "Escape", target: dialog }, { querySelector: () => null }), false);
});

test("a step change never leaves keyboard focus on a control that disappeared, and never takes focus from the page", () => {
  const p = page({ step: "intro" });
  p.render([]);
  const start = p.q("[data-slot='practice'] button");
  start.focus();

  // The practice order was started in another tab: Start practice disappears under the keyboard.
  p.render([demo("New")]);
  assert.equal(p.doc.activeElement, p.q("[data-training-title]"), "focus moves to the guide's title");

  // Focus elsewhere on the page is never taken by the guide, by a step change or by real orders.
  const card = p.el("button", {});
  p.host.append(card);
  card.focus();
  p.render([demo("Accepted")], null, isolated(4));
  assert.equal(p.doc.activeElement, card);
});

test("Show the practice order appears only while a practice order is on the screen", () => {
  const p = page({ step: "practice-accepted" });
  p.render([real("old", "Delivered")]);
  assert.equal(isShown(p.q("[data-training-show-practice]")), false, "nothing to show yet");

  p.render([demo("Accepted")]);
  assert.equal(isShown(p.q("[data-training-show-practice]")), true);

  p.render([]);
  assert.equal(isShown(p.q("[data-training-show-practice]")), false, "the practice order left the screen");
  assert.equal(p.section.getAttribute("data-step"), "practice-accepted", "the step itself is kept");
});

test("courier steps explain a paused background service only after a long wait", () => {
  const p = page({ step: "practice-ready" });
  p.render([demo("ReadyForPickup")]);
  assert.equal(p.q("[data-training-waiting]").hidden, true);
  assert.ok(training.WAITING_HINT_AFTER_MS >= 30000, "longer than a Worker cycle plus the courier delay");
});
