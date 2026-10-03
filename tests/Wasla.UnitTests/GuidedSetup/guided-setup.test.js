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

  assert.equal(/XMLHttpRequest|\.submit\(\)|requestSubmit|method:\s*"POST"/.test(source), false);
  assert.equal(/hide\.bs\.modal|hidden\.bs\.modal|keydown/.test(source), false);
  // The only network call is the setup panel's read-only readiness check (a GET), shared by the re-check and the watch.
  assert.equal((source.match(/fetch\(/g) || []).length, 1);
  const read = source.slice(source.indexOf("function readStatus"), source.indexOf("function recheckPanel"));
  assert.match(read, /win\.fetch\(url, \{ headers: \{ Accept: "application\/json" \}, credentials: "same-origin", cache: "no-store" \}\)/);
});

// Setup panel (Print Bridge): shows the connected state when the page's own setup flow reports success ------

function fakePanel(state) {
  const attributes = {
    "data-guided-setup-state": state,
    "data-guided-setup-status-url": "/guided-setup/section-status?section=print-bridge",
    "data-guided-setup-refresh-on": "wasla:print-bridge-setup-completed"
  };
  const classes = new Set(state === "ready" ? ["is-ready"] : []);
  const parts = [
    { when: "not-ready", hidden: state === "ready" },
    { when: "not-ready", hidden: state === "ready" },
    { when: "ready", hidden: state !== "ready" }
  ].map((part) => Object.assign(part, { getAttribute: (name) => (name === "data-guided-setup-when" ? part.when : null) }));
  return {
    parts,
    classes,
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); },
    classList: { toggle: (name, on) => (on ? classes.add(name) : classes.delete(name)) },
    querySelectorAll: () => parts
  };
}

function fakeWindow(responses) {
  const calls = [];
  return {
    calls,
    fetch: (url, init) => {
      calls.push({ url, init });
      const next = responses.length ? responses.shift() : { current: true, ready: false };
      if (next instanceof Error) return Promise.reject(next);
      return Promise.resolve({ ok: true, json: () => Promise.resolve(next) });
    },
    setTimeout: (fn) => { fn(); return 1; }
  };
}

test("panel state follows the server's readiness; a section that is no longer current is left alone", () => {
  assert.equal(guided.panelStateFor({ current: true, ready: true }), "ready");
  assert.equal(guided.panelStateFor({ current: true, ready: false }), "not-ready");
  assert.equal(guided.panelStateFor({ current: false, ready: false }), null);
  assert.equal(guided.panelStateFor(null), null);
});

test("the connected state hides set-up-later and the unconnected copy, and shows Continue", () => {
  const panel = fakePanel("not-ready");

  assert.equal(guided.applyPanelState(panel, "ready"), true);

  assert.equal(panel.getAttribute("data-guided-setup-state"), "ready");
  assert.ok(panel.classes.has("is-ready"));
  assert.deepEqual(panel.parts.map((p) => [p.when, p.hidden]), [["not-ready", true], ["not-ready", true], ["ready", false]]);
  assert.equal(guided.applyPanelState(panel, "ready"), false, "applying the same state again changes nothing");
});

test("after the setup page reports success, the panel re-reads readiness until it is connected", async () => {
  const panel = fakePanel("not-ready");
  const win = fakeWindow([{ current: true, ready: false }, new Error("offline"), { current: true, ready: true }, { current: true, ready: true }]);

  const state = await guided.recheckPanel(panel, win, 5, 2000);

  assert.equal(state, "ready");
  assert.equal(win.calls.length, 3, "stops as soon as the server confirms the connection");
  assert.ok(win.calls.every((c) => c.url === "/guided-setup/section-status?section=print-bridge"));
  assert.ok(win.calls.every((c) => !c.init.method || c.init.method === "GET"), "only reads");
  assert.equal(panel.getAttribute("data-guided-setup-state"), "ready");
});

test("the re-check is bounded and stops when the journey moved on", async () => {
  const stillWaiting = fakeWindow([]);
  assert.equal(await guided.recheckPanel(fakePanel("not-ready"), stillWaiting, 5, 2000), "not-ready");
  assert.equal(stillWaiting.calls.length, 5);

  const movedOn = fakeWindow([{ current: false, ready: false }]);
  const panel = fakePanel("not-ready");
  assert.equal(await guided.recheckPanel(panel, movedOn, 5, 2000), null);
  assert.equal(movedOn.calls.length, 1);
  assert.equal(panel.getAttribute("data-guided-setup-state"), "not-ready");
});

test("only the setup page's success event starts a re-check; nothing polls on its own", async () => {
  const panel = fakePanel("not-ready");
  const listeners = {};
  const doc = {
    querySelectorAll: () => [panel],
    addEventListener: (name, fn) => { listeners[name] = fn; }
  };
  const win = fakeWindow([{ current: true, ready: true }]);

  guided.watchPanels(doc, win);
  assert.equal(win.calls.length, 0, "no request until the page reports success");
  assert.deepEqual(Object.keys(listeners), ["wasla:print-bridge-setup-completed"]);

  listeners["wasla:print-bridge-setup-completed"]();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(win.calls.length, 1);
  assert.equal(panel.getAttribute("data-guided-setup-state"), "ready");
});

test("a manual-connection watch starts only on its own event and never for a connected panel", async () => {
  const panel = fakePanel("not-ready");
  panel.setAttribute("data-guided-setup-watch-on", "wasla:print-bridge-manual-setup-started");
  const listeners = {};
  const doc = { querySelectorAll: () => [panel], addEventListener: (name, fn) => { listeners[name] = fn; } };
  const timers = [];
  const win = Object.assign(fakeWindow([{ current: true, ready: false }]), {
    setTimeout: (fn) => { timers.push(fn); return timers.length; },
    clearTimeout: () => {}
  });

  guided.watchPanels(doc, win);
  assert.equal(win.calls.length, 0, "nothing is read on load");
  assert.deepEqual(Object.keys(listeners).sort(), ["wasla:print-bridge-manual-setup-started", "wasla:print-bridge-setup-completed"]);

  listeners["wasla:print-bridge-manual-setup-started"]();
  listeners["wasla:print-bridge-manual-setup-started"]();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(win.calls.length, 1, "one watch, however often the page asks");
  assert.equal(timers.length, 1);
  assert.deepEqual(guided.stopWatches(), [panel]);

  assert.equal(guided.watchPanel(fakePanel("ready"), doc, win, 5, 2000), null, "a connected panel has nothing to watch");
});

// Print Bridge connected: the panel's connected state fixed to the viewport -------------------------------------

/**
 * The Print Bridge section panel as _GuidedSetupSectionPanel.cshtml renders it: the unconnected parts, the status
 * line's connected item, the connected actions and the fixed panel (the last "ready" part). Only the server's
 * confirmed readiness shows the "ready" parts.
 */
function printBridgePanel(state) {
  const attributes = {
    "data-guided-setup-panel": "print-bridge",
    "data-guided-setup-state": state,
    "data-guided-setup-status-url": "/guided-setup/section-status?section=print-bridge",
    "data-guided-setup-refresh-on": "wasla:print-bridge-setup-completed",
    "data-guided-setup-watch-on": "wasla:print-bridge-manual-setup-started"
  };
  const ready = state === "ready";
  const part = (when, name) => {
    const p = { name, when, hidden: when === "ready" ? !ready : ready };
    p.getAttribute = (attr) => (attr === "data-guided-setup-when" ? p.when : null);
    return p;
  };
  const parts = [part("not-ready", "later"), part("not-ready", "status-not-ready"), part("ready", "status-ready"),
    part("not-ready", "set-up-later"), part("ready", "meet-device"), part("ready", "dock")];
  const classes = new Set(ready ? ["is-ready"] : []);
  return {
    parts,
    dock: () => parts.filter((p) => p.name === "dock"),
    dockVisible: () => parts.some((p) => p.name === "dock" && !p.hidden),
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); },
    classList: { toggle: (name, on) => (on ? classes.add(name) : classes.delete(name)) },
    querySelectorAll: () => parts
  };
}

/** A page with that panel: records listeners, dispatched events and requests; timers run only when asked. */
function connectedPage(state, responses) {
  const panel = printBridgePanel(state);
  const listeners = {};
  const dispatched = [];
  const timers = [];
  const doc = {
    querySelectorAll: () => [panel],
    addEventListener: (name, fn) => { (listeners[name] = listeners[name] || []).push(fn); },
    dispatchEvent: (event) => { dispatched.push(event); return true; }
  };
  const calls = [];
  const win = {
    CustomEvent: function (type, init) { this.type = type; this.detail = init && init.detail; },
    fetch: (url, init) => {
      calls.push({ url, init });
      const next = responses.length ? responses.shift() : { current: true, ready: false };
      if (next instanceof Error) return Promise.reject(next);
      return Promise.resolve({ ok: true, json: () => Promise.resolve(next) });
    },
    setTimeout: (fn) => { timers.push(fn); return timers.length; },
    clearTimeout: () => {}
  };
  return {
    panel, doc, win, calls, timers, dispatched,
    fire: (name) => (listeners[name] || []).forEach((fn) => fn({ type: name })),
    listenerNames: () => Object.keys(listeners).sort(),
    announcements: () => dispatched.filter((e) => e.type === "wasla:guided-setup-state-changed"),
    settle: async () => { for (let i = 0; i < 5; i++) await new Promise((resolve) => setImmediate(resolve)); },
    runTimers: async function () { while (timers.length) { timers.shift()(); await this.settle(); } }
  };
}

test("before a verified connection the fixed panel is not shown, and nothing is read", async () => {
  const page = connectedPage("not-ready", []);
  guided.watchPanels(page.doc, page.win);
  await page.settle();

  assert.equal(page.panel.dockVisible(), false);
  assert.equal(page.calls.length, 0);
});

test("automatic setup: the verified success shows the fixed panel as soon as the server confirms the device", async () => {
  const page = connectedPage("not-ready", [{ current: true, ready: true }]);
  guided.watchPanels(page.doc, page.win);

  page.fire("wasla:print-bridge-setup-completed");
  await page.settle();

  assert.equal(page.panel.dockVisible(), true);
  assert.equal(page.calls.length, 1, "one read-only check, no waiting");
  assert.equal(page.announcements().length, 1);
});

test("manual setup: the panel appears when the device's first heartbeat is confirmed, not when the token is issued", async () => {
  const page = connectedPage("not-ready", [{ current: true, ready: false }, { current: true, ready: true }]);
  guided.watchPanels(page.doc, page.win);

  page.fire("wasla:print-bridge-manual-setup-started");
  await page.settle();
  assert.equal(page.panel.dockVisible(), false, "a token alone proves nothing");

  await page.runTimers();
  assert.equal(page.panel.dockVisible(), true);
  assert.equal(page.calls.length, 2);
  guided.stopWatches();
});

test("a page loaded with the device already connected shows the panel and reads nothing", async () => {
  const page = connectedPage("ready", []);
  guided.watchPanels(page.doc, page.win);
  page.fire("wasla:print-bridge-setup-completed");
  page.fire("wasla:print-bridge-manual-setup-started");
  await page.settle();

  assert.equal(page.panel.dockVisible(), true);
  assert.equal(page.calls.length, 0);
  assert.equal(page.announcements().length, 0, "nothing new to announce on load");
});

test("unverified, failed or expired setups never show it, nor does a journey that moved on", async () => {
  for (const responses of [
    [{ current: true, ready: false }, { current: true, ready: false }, { current: true, ready: false }, { current: true, ready: false }, { current: true, ready: false }],
    [new Error("offline"), new Error("offline"), new Error("offline"), new Error("offline"), new Error("offline")],
    [{ current: false, ready: true }],
    [{ current: true }]
  ]) {
    const page = connectedPage("not-ready", responses);
    guided.watchPanels(page.doc, page.win);
    page.fire("wasla:print-bridge-setup-completed");
    await page.runTimers();

    assert.equal(page.panel.dockVisible(), false, JSON.stringify(responses[0] instanceof Error ? "error" : responses[0]));
    assert.equal(page.announcements().length, 0);
  }
});

test("duplicate connection events leave one panel and one announcement", async () => {
  const page = connectedPage("not-ready", [{ current: true, ready: true }, { current: true, ready: true }, { current: true, ready: true }]);
  guided.watchPanels(page.doc, page.win);

  page.fire("wasla:print-bridge-setup-completed");
  await page.settle();
  page.fire("wasla:print-bridge-setup-completed");
  page.fire("wasla:print-bridge-manual-setup-started");
  page.fire("wasla:print-bridge-setup-completed");
  await page.runTimers();

  assert.equal(page.panel.dock().length, 1);
  assert.equal(page.panel.dockVisible(), true);
  assert.equal(page.announcements().length, 1, "the status line changes once, so it is announced once");
  assert.equal(page.calls.length, 1, "a connected panel reads nothing more");
});

test("connecting never scrolls, navigates, clicks, posts or moves the journey; scrolling never hides the panel", async () => {
  const page = connectedPage("not-ready", [{ current: true, ready: true }]);
  guided.watchPanels(page.doc, page.win);
  page.fire("wasla:print-bridge-setup-completed");
  await page.settle();
  page.fire("scroll");
  page.fire("resize");

  assert.equal(page.panel.dockVisible(), true);
  assert.ok(page.calls.every((c) => c.url === "/guided-setup/section-status?section=print-bridge" && (!c.init.method || c.init.method === "GET")));
  assert.ok(!page.listenerNames().some((name) => /scroll|resize|intersect/i.test(name)), "no scroll handling at all");
  assert.equal(page.dispatched.length, 1, "only the state-change notification, no synthetic click or submit");
});

// The two visible continuation links share one guard -------------------------------------------------------------

function continueLink() {
  const attributes = { href: "/guided-setup/print-bridge/device", "data-guided-setup-continue": "" };
  const link = {
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); },
    removeAttribute: (name) => { delete attributes[name]; },
    matches: (selector) => selector === "a[data-guided-setup-continue]",
    closest: (selector) => (selector === "a[data-guided-setup-continue]" ? link : null)
  };
  return link;
}

function linkPage() {
  const top = continueLink();
  const fixed = continueLink();
  const doc = { querySelectorAll: (selector) => (selector === "a[data-guided-setup-continue]" ? [top, fixed] : []) };
  const timers = [];
  const win = { setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {} };
  return { top, fixed, doc, win, timers };
}

/** A click as the browser delivers it; "requests" counts the navigations the browser would start (not prevented). */
function click(target, extra) {
  return Object.assign({ target, button: 0, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }, extra || {});
}

test("the fixed and the panel link are the same device-guide GET, and one activation is at most one request", () => {
  const page = linkPage();
  assert.equal(page.top.getAttribute("href"), page.fixed.getAttribute("href"));

  const events = [click(page.fixed), click(page.fixed), click(page.top), click(page.fixed)];
  events.forEach((event) => guided.handleContinueClick(event, page.doc, page.win));

  assert.equal(events.filter((e) => !e.defaultPrevented).length, 1, "one navigation, whichever link is clicked again");
  for (const link of [page.top, page.fixed]) {
    assert.equal(link.getAttribute("aria-disabled"), "true", "both links wait for it");
    assert.equal(link.getAttribute("data-guided-setup-navigating"), "true");
  }
});

test("a navigation that never happens, or a page restored from the back/forward cache, makes both links usable again", () => {
  const page = linkPage();
  guided.handleContinueClick(click(page.top), page.doc, page.win);
  assert.equal(page.timers.length, 1);
  assert.equal(page.timers[0].ms, guided.CONTINUE_RESTORE_MS);

  page.timers[0].fn();
  for (const link of [page.top, page.fixed]) {
    assert.equal(link.getAttribute("aria-disabled"), null);
    assert.equal(link.getAttribute("data-guided-setup-navigating"), null);
  }
  const again = click(page.fixed);
  guided.handleContinueClick(again, page.doc, page.win);
  assert.equal(again.defaultPrevented, false);

  guided.resetAll(Object.assign({}, page.doc, { querySelectorAll: (s) => (s === "form[data-guided-setup-form]" ? [] : page.doc.querySelectorAll(s)) }));
  assert.equal(page.fixed.getAttribute("aria-disabled"), null);
});

test("opening the guide in a new tab neither waits nor blocks; other links are never touched", () => {
  const page = linkPage();
  for (const extra of [{ ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { button: 1 }]) {
    const event = click(page.fixed, extra);
    assert.equal(guided.handleContinueClick(event, page.doc, page.win), true);
    assert.equal(event.defaultPrevented, false);
  }
  assert.equal(page.fixed.getAttribute("aria-disabled"), null);

  const other = { matches: () => false, closest: () => null };
  const event = click(other);
  assert.equal(guided.handleContinueClick(event, page.doc, page.win), true);
  assert.equal(event.defaultPrevented, false);
  assert.equal(page.top.getAttribute("aria-disabled"), null);
});
