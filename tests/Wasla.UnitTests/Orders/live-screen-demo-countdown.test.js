const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..", "..", "..");
const countdown = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "orders", "orders-demo-countdown.js"));
const store = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-store.js"));

// A small DOM: elements with attributes, children and compound attribute selectors ("tag[attr][attr=\"v\"]").
function matches(el, selector) {
  const m = selector.match(/^([a-z]*)((?:\[[^\]]+\])*)$/);
  if (!m) throw new Error("unsupported selector " + selector);
  if (m[1] && el.tagName !== m[1]) return false;
  const parts = m[2].match(/\[[^\]]+\]/g) || [];
  return parts.every((part) => {
    const a = part.slice(1, -1).match(/^([\w-]+)(?:="([^"]*)")?$/);
    const value = el.getAttribute(a[1]);
    return a[2] === undefined ? value !== null : value === a[2];
  });
}

function element(tagName, attrs = {}) {
  const attributes = new Map(Object.entries(attrs));
  const el = {
    tagName, children: [], parentNode: null, hidden: false, style: {}, textContent: "", title: "",
    get id() { return attributes.get("id") || ""; },
    set id(v) { attributes.set("id", v); },
    get className() { return attributes.get("class") || ""; },
    set className(v) { attributes.set("class", v); },
    getAttribute: (n) => (attributes.has(n) ? attributes.get(n) : null),
    setAttribute: (n, v) => { attributes.set(n, String(v)); },
    removeAttribute: (n) => { attributes.delete(n); },
    appendChild(child) { child.parentNode = el; el.children.push(child); return child; },
    insertBefore(child, ref) {
      child.parentNode = el;
      const at = ref ? el.children.indexOf(ref) : -1;
      if (at < 0) el.children.push(child); else el.children.splice(at, 0, child);
      return child;
    },
    removeChild(child) { el.children.splice(el.children.indexOf(child), 1); child.parentNode = null; return child; },
    get nextSibling() { const p = el.parentNode; return p ? p.children[p.children.indexOf(el) + 1] || null : null; },
    matches: (s) => matches(el, s),
    querySelectorAll: (s) => all(el).filter((c) => c !== el && matches(c, s)),
    querySelector: (s) => el.querySelectorAll(s)[0] || null,
    closest(s) { for (let n = el; n; n = n.parentNode) if (n.matches && n.matches(s)) return n; return null; }
  };
  return el;
}

function all(el) { return [el].concat(...el.children.map(all)); }

function page() {
  const body = element("body");
  const dispatched = [];
  const doc = {
    body,
    createElement: (tag) => element(tag),
    querySelectorAll: (s) => body.querySelectorAll(s),
    querySelector: (s) => body.querySelector(s),
    dispatchEvent: (e) => { dispatched.push(e); return true; }
  };
  return { doc, body, dispatched };
}

/** A practice-order card as the Live Screen renders it: head row (with the status badge), then the order code. */
function demoCard(id, status) {
  const card = element("article", { "data-order-id": id, "data-order-status": status, "data-wasla-demo-card": "true", "data-live-signature": "sig-" + status });
  const head = element("div", { class: "wasla-live-card__head" });
  head.appendChild(element("span", { "data-status-badge": "" }));
  card.appendChild(head);
  card.appendChild(element("div", { class: "orders-card-code" }));
  return card;
}

function realCard(id, status) {
  const card = element("article", { "data-order-id": id, "data-order-status": status, "data-live-signature": "real" });
  const head = element("div");
  head.appendChild(element("span", { "data-status-badge": "" }));
  card.appendChild(head);
  return card;
}

const MESSAGES = {
  demoCountdownPickUp: "[pickup in {0}]",
  demoCountdownDeliver: "[deliver in {0}]",
  demoCountdownLeave: "[leave in {0}]",
  demoCountdownWaiting: "[waiting]",
  demoCountdownSeconds: "{0} s",
  demoCountdownLabel: "[time left]",
  demoCountdownHint: "[automatic]"
};

/** Fake clock and interval: nothing waits in real time. */
function clock(startMs) {
  let now = startMs;
  const intervals = new Map();
  let seq = 0;
  return {
    now: () => now,
    advance(ms) { now += ms; },
    setInterval: (fn, ms) => { seq += 1; intervals.set(seq, { fn, ms }); return seq; },
    clearInterval: (id) => { intervals.delete(id); },
    tick() { for (const { fn } of [...intervals.values()]) fn(); },
    active: () => intervals.size
  };
}

const ID = "11111111-2222-4333-8444-555555555555";
const SERVER = Date.parse("2026-10-03T09:00:00Z");
const iso = (ms) => new Date(ms).toISOString();

function demoOrder(status, action, dueMs, extra) {
  return Object.assign({ id: ID, status, isDemo: true, demoAutomation: { action, dueAtUtc: iso(dueMs), durationSeconds: 20 } }, extra || {});
}

function setup(localStart) {
  const p = page();
  const time = clock(localStart === undefined ? SERVER : localStart);
  const controller = countdown.createController({
    document: p.doc, now: time.now, setInterval: time.setInterval, clearInterval: time.clearInterval,
    message: (key) => MESSAGES[key]
  });
  return Object.assign(p, { time, controller });
}

function view(card) {
  const box = card.querySelector("[data-demo-countdown]");
  if (!box) return null;
  const bar = box.querySelector("[data-demo-countdown-bar]");
  return {
    box,
    text: box.querySelector("[data-demo-countdown-text]").textContent,
    seconds: box.querySelector("[data-demo-countdown-seconds]"),
    bar,
    fill: box.querySelector("[data-demo-countdown-fill]").style.inlineSize,
    now: bar.getAttribute("aria-valuenow")
  };
}

// Stages and messages -------------------------------------------------------------------------------------------

test("each automatic stage shows its own action, inside the practice order's card below its status row", () => {
  for (const [status, action, text] of [["ReadyForPickup", "PickUp", "[pickup in 20]"], ["OnTheWay", "Deliver", "[deliver in 20]"], ["Delivered", "Leave", "[leave in 20]"]]) {
    const t = setup();
    const card = demoCard(ID, status);
    t.body.appendChild(card);
    t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder(status, action, SERVER + 20000)] });

    const v = view(card);
    assert.equal(v.text, text, status);
    assert.equal(card.children[1], v.box, "right after the head row that holds the status badge");
    assert.equal(v.box.getAttribute("data-demo-countdown-action"), action);
    assert.equal(v.bar.getAttribute("role"), "progressbar");
    assert.equal(v.bar.getAttribute("aria-valuemin"), "0");
    assert.equal(v.bar.getAttribute("aria-valuemax"), "20");
    assert.equal(v.now, "20");
    assert.equal(v.fill, "100.00%", "the bar starts full");
  }
});

test("the countdown starts from the server's remaining time, not from 20, and a reload continues it", () => {
  const t = setup();
  const card = demoCard(ID, "ReadyForPickup");
  t.body.appendChild(card);
  // The stage began 6 seconds before this snapshot.
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 14000)] });
  assert.equal(view(card).text, "[pickup in 14]");
  assert.equal(view(card).fill, "70.00%");

  // Reload two seconds later: a new page, a new controller, the same persisted deadline.
  const reload = setup(SERVER + 2000);
  const again = demoCard(ID, "ReadyForPickup");
  reload.body.appendChild(again);
  reload.controller.update({ serverTimeUtc: iso(SERVER + 2000), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 14000)] });
  assert.equal(view(again).text, "[pickup in 12]");
});

test("the browser's clock offset does not matter: the deadline is measured against the server's time", () => {
  for (const skew of [-3600000, 0, 90000, 3600000]) {
    const t = setup(SERVER + skew);
    const card = demoCard(ID, "OnTheWay");
    t.body.appendChild(card);
    t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("OnTheWay", "Deliver", SERVER + 9000)] });
    assert.equal(view(card).text, "[deliver in 9]", "skew " + skew);
    t.time.advance(4000);
    t.time.tick();
    assert.equal(view(card).text, "[deliver in 5]", "skew " + skew);
  }
});

test("repeated and stale snapshots never restart or lengthen a stage", () => {
  const t = setup();
  const card = demoCard(ID, "ReadyForPickup");
  t.body.appendChild(card);
  const snapshot = { serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 20000)] };
  t.controller.update(snapshot);

  t.time.advance(5000);
  t.controller.update(snapshot); // the same (cached) response again, 5 s later
  assert.equal(view(card).text, "[pickup in 15]");

  t.time.advance(1000);
  t.controller.update({ serverTimeUtc: iso(SERVER + 6000), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 20000)] });
  assert.equal(view(card).text, "[pickup in 14]", "a fresh poll agrees");

  t.controller.update({ serverTimeUtc: iso(SERVER + 1000), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 20000)] });
  assert.equal(view(card).text, "[pickup in 14]", "an older response cannot add time");
});

test("an older stage arriving late is ignored; the next stage starts its own countdown", () => {
  const t = setup();
  const card = demoCard(ID, "ReadyForPickup");
  t.body.appendChild(card);
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 3000)] });

  // The Worker picked it up: the Live Screen renders OnTheWay and a new 20-second stage begins.
  t.time.advance(5000);
  card.setAttribute("data-order-status", "OnTheWay");
  t.controller.update({ serverTimeUtc: iso(SERVER + 5000), orders: [demoOrder("OnTheWay", "Deliver", SERVER + 25000)] });
  assert.equal(view(card).text, "[deliver in 20]");
  assert.match(view(card).box.className, /wasla-demo-countdown--on-the-way/);

  // A late Ready snapshot cannot take it back.
  t.controller.update({ serverTimeUtc: iso(SERVER + 1000), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 3000)] });
  assert.equal(view(card).text, "[deliver in 20]");
});

test("a background tab recalculates from the deadline when it comes back", () => {
  const t = setup();
  const card = demoCard(ID, "Delivered");
  t.body.appendChild(card);
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("Delivered", "Leave", SERVER + 20000)] });

  // Hidden: the browser ran no ticks for 13 seconds.
  t.time.advance(13000);
  t.controller.refresh();
  assert.equal(view(card).text, "[leave in 7]");
});

// Zero ------------------------------------------------------------------------------------------------------------

test("at zero it waits for the platform: no fake status, no request, no event, no more ticking", () => {
  const t = setup();
  const card = demoCard(ID, "ReadyForPickup");
  t.body.appendChild(card);
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 2000)] });
  assert.equal(t.time.active(), 1);

  t.time.advance(2500);
  t.time.tick();
  const v = view(card);
  assert.equal(v.text, "[waiting]");
  assert.equal(v.seconds.hidden, true);
  assert.equal(v.now, "0");
  assert.equal(v.fill, "0.00%");
  assert.equal(v.bar.getAttribute("aria-valuetext"), "[waiting]");
  assert.equal(card.getAttribute("data-order-status"), "ReadyForPickup", "the card keeps the server's status");
  assert.equal(t.time.active(), 0, "nothing ticks while it waits");
  assert.deepEqual(t.dispatched, []);

  // Polling goes on: the next snapshot still waiting changes nothing.
  t.controller.update({ serverTimeUtc: iso(SERVER + 9000), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 2000)] });
  assert.equal(view(card).text, "[waiting]");
});

test("the script only displays and re-reads: no request of its own, status change, navigation, event, sound or announcement", () => {
  const source = fs.readFileSync(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "orders", "orders-demo-countdown.js"), "utf8");
  for (const forbidden of ["fetch(", "XMLHttpRequest", "sendBeacon", ".submit(", "method", "POST", "dispatchEvent", "location", "aria-live", "role\", \"status", "Audio", "Notification", "data-live-signature", "beginMutation"])
    assert.equal(source.includes(forbidden), false, forbidden);
  // Its only way to the server is the Live Screen store's own read-only, coalesced refresh.
  assert.match(source, /return live && typeof live\.requestRefresh === "function" \? live\.requestRefresh\(\) : null;/);
  assert.equal((source.match(/options\.requestRefresh\(\)/g) || []).length, 1, "one deadline call");
});

// Isolation ---------------------------------------------------------------------------------------------------

test("countdown ticks change nothing the Live Screen uses for new orders, sound or highlight", () => {
  const t = setup();
  const card = demoCard(ID, "OnTheWay");
  t.body.appendChild(card);
  const order = demoOrder("OnTheWay", "Deliver", SERVER + 20000);
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [order] });
  for (let i = 0; i < 5; i++) { t.time.advance(1000); t.time.tick(); }

  assert.equal(card.getAttribute("data-live-signature"), "sig-OnTheWay");
  assert.equal(card.getAttribute("data-order-status"), "OnTheWay");
  assert.deepEqual(t.dispatched, []);
  // The countdown deadline is not part of the order's content signature, and a practice order is never "new".
  const without = Object.assign({}, order, { demoAutomation: null });
  const later = Object.assign({}, order, { demoAutomation: { action: "Deliver", dueAtUtc: iso(SERVER + 99000), durationSeconds: 20 } });
  assert.equal(store.orderContentSignature(order), store.orderContentSignature(without));
  assert.equal(store.orderContentSignature(order), store.orderContentSignature(later));
  assert.deepEqual(store.collectNewIds(new Set(), [order], true), []);
});

test("real orders never get a countdown, even if a snapshot carried one", () => {
  const t = setup();
  const real = realCard("22222222-2222-4333-8444-555555555555", "ReadyForPickup");
  t.body.appendChild(real);
  const fake = { id: "22222222-2222-4333-8444-555555555555", status: "ReadyForPickup", isDemo: false, demoAutomation: { action: "PickUp", dueAtUtc: iso(SERVER + 5000), durationSeconds: 20 } };
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [fake] });

  assert.equal(real.querySelector("[data-demo-countdown]"), null);
  assert.equal(countdown.plan(fake, iso(SERVER), SERVER), null);
  assert.equal(t.controller.size(), 0);
  assert.equal(t.time.active(), 0);
  // A practice order in a stage the restaurant moves itself has no countdown either.
  assert.equal(countdown.plan({ id: ID, status: "Preparing", isDemo: true, demoAutomation: null }, iso(SERVER), SERVER), null);
});

// Cleanup -----------------------------------------------------------------------------------------------------

test("no countdown or timer survives the practice order leaving, the page hiding or the page being disposed", () => {
  const t = setup();
  const card = demoCard(ID, "Delivered");
  t.body.appendChild(card);
  t.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("Delivered", "Leave", SERVER + 20000)] });
  assert.equal(t.time.active(), 1);

  // The delivered stage ended on the server: the next snapshot no longer has it.
  t.controller.update({ serverTimeUtc: iso(SERVER + 21000), orders: [] });
  assert.equal(card.querySelector("[data-demo-countdown]"), null);
  assert.equal(t.controller.size(), 0);
  assert.equal(t.time.active(), 0);

  const again = setup();
  const other = demoCard(ID, "ReadyForPickup");
  again.body.appendChild(other);
  again.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 20000)] });
  again.controller.suspend();
  assert.equal(again.time.active(), 0, "a hidden page does not tick");
  again.controller.resume();
  assert.equal(again.time.active(), 1);
  again.controller.dispose();
  assert.equal(again.time.active(), 0);
  assert.equal(other.querySelector("[data-demo-countdown]"), null);
  again.controller.update({ serverTimeUtc: iso(SERVER), orders: [demoOrder("ReadyForPickup", "PickUp", SERVER + 20000)] });
  assert.equal(other.querySelector("[data-demo-countdown]"), null, "a disposed controller does nothing");
});

test("a card rebuilt by the Live Screen gets its countdown back on the next render, only one per card", () => {
  const t = setup();
  const card = demoCard(ID, "OnTheWay");
  t.body.appendChild(card);
  const snapshot = { serverTimeUtc: iso(SERVER), orders: [demoOrder("OnTheWay", "Deliver", SERVER + 20000)] };
  t.controller.update(snapshot);
  t.controller.update(snapshot);
  t.time.tick();
  assert.equal(card.querySelectorAll("[data-demo-countdown]").length, 1);

  t.body.removeChild(card);
  const rebuilt = demoCard(ID, "OnTheWay");
  t.body.appendChild(rebuilt);
  t.controller.update(snapshot);
  assert.equal(rebuilt.querySelectorAll("[data-demo-countdown]").length, 1);
});

// Page and styling contracts ----------------------------------------------------------------------------------

test("the Live Screen loads it with localized copy; the styles are logical, dark-aware and motion-safe", () => {
  const live = fs.readFileSync(path.join(root, "src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml"), "utf8");
  assert.match(live, /<script src="~\/js\/orders\/orders-demo-countdown\.js" asp-append-version="true"><\/script>/);
  for (const key of ["PickUp", "Deliver", "Leave", "Waiting", "Seconds", "Label", "Hint"])
    assert.match(live, new RegExp(`\\["demoCountdown${key}"\\] = L\\["Orders\\.DemoCountdown\\.${key}"\\]\\.Value`));
  assert.equal(/2 ?0 ?sn|20 seconds/.test(live), false, "no number in the page copy");

  const css = fs.readFileSync(path.join(root, "src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css"), "utf8");
  const block = css.slice(css.indexOf("/* Practice-order countdown"));
  for (const rule of ["inline-size", "margin-block", "padding-inline", "text-align: end", "--wasla-status-ready", "--wasla-status-on-the-way", "--wasla-status-completed", "[data-bs-theme=\"dark\"] .wasla-demo-countdown", "@media (max-width: 575.98px)"])
    assert.ok(block.includes(rule), rule);
  assert.match(block, /@media \(prefers-reduced-motion: no-preference\) \{\s*\.wasla-demo-countdown__fill \{\s*transition: inline-size 1s linear;/);
  assert.equal((block.match(/transition/g) || []).length, 1, "the only motion is inside the no-preference query");
  for (const physical of ["margin-left", "margin-right", "padding-left", "padding-right", "border-left", "border-right", " left:", " right:", "text-align: left", "text-align: right"])
    assert.equal(block.includes(physical), false, physical);
  assert.equal(/[\s{;](width|height)\s*:/.test(block), false, "inline-size and block-size, not width and height");
});
