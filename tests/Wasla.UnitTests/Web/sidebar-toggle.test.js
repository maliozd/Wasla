const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "..", "..", "..");
const source = fs.readFileSync(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "sidebar-toggle.js"), "utf8");

const KEY = "Wasla.sidebarCollapsed";
const MOBILE_QUERY = "(max-width: 991.98px)";
const UNRELATED = { "Wasla.tenant.theme": "dark", "Wasla.theme": "light", "Wasla.liveView.v1": "cards" };

function securityError() {
  const error = new Error("The operation is insecure.");
  error.name = "SecurityError";
  return error;
}

// One origin's localStorage. Every call is recorded; `fail` names the methods that throw as a blocked browser does.
function originStorage(initial, fail = []) {
  const values = new Map(Object.entries(initial || {}));
  const calls = [];
  const guard = (method) => {
    calls.push(method);
    if (fail.includes(method)) throw securityError();
  };
  return {
    calls,
    getItem(key) { guard("getItem"); return values.has(key) ? values.get(key) : null; },
    setItem(key, value) { guard("setItem"); values.set(key, String(value)); },
    removeItem(key) { guard("removeItem"); values.delete(key); },
    clear() { guard("clear"); values.clear(); },
    snapshot: () => Object.fromEntries(values)
  };
}

// The storage situations a page can meet. `storage` is shared across page loads of one origin.
const FAILURES = {
  "localStorage property access throws": () => ({ storage: originStorage({ ...UNRELATED, [KEY]: "true" }), blockProperty: true }),
  "localStorage is unavailable": () => ({ storage: null }),
  "getItem throws": () => ({ storage: originStorage({ ...UNRELATED, [KEY]: "true" }, ["getItem"]) }),
  "setItem throws": () => ({ storage: originStorage({ ...UNRELATED }, ["setItem"]) }),
  "removeItem throws": () => ({ storage: originStorage({ ...UNRELATED }, ["removeItem"]) }),
  "every storage method throws": () => ({ storage: originStorage({ ...UNRELATED, [KEY]: "true" }, ["getItem", "setItem", "removeItem", "clear"]) })
};

function classes() {
  const set = new Set();
  return {
    add: (name) => set.add(name),
    remove: (name) => set.delete(name),
    contains: (name) => set.has(name),
    toggle(name, force) {
      const on = force === undefined ? !set.has(name) : !!force;
      if (on) set.add(name); else set.delete(name);
      return on;
    }
  };
}

// The tenant layout's navigation markup (_TenantLayout.cshtml) as a minimal DOM, with listener isolation as in a
// browser: an exception in one listener is reported as a page error and does not stop the other listeners.
function tenantPage({ width, rtl = false }) {
  const errors = [];
  const doc = { listeners: {}, activeElement: null, documentElement: null, body: null };

  function element(tag, id, attributes = {}, parent = null) {
    const el = {
      tag, id, parent, children: [], hidden: false, inert: false, listeners: {},
      attributes: { ...attributes },
      classList: classes(),
      style: { removed: [], removeProperty(name) { this.removed.push(name); } },
      getAttribute: (name) => (name in el.attributes ? el.attributes[name] : null),
      setAttribute: (name, value) => { el.attributes[name] = String(value); },
      removeAttribute: (name) => { delete el.attributes[name]; },
      addEventListener: (type, listener) => (el.listeners[type] = el.listeners[type] || []).push(listener),
      // As in a browser, an element inside an inert subtree cannot take focus.
      focus: () => {
        for (let node = el; node; node = node.parent) if (node.inert) return;
        doc.activeElement = el;
      },
      blur: () => { if (doc.activeElement === el) doc.activeElement = doc.body; },
      contains(other) {
        for (let node = other; node; node = node.parent) if (node === el) return true;
        return false;
      },
      closest(selector) {
        assert.equal(selector, "a[href]");
        for (let node = el; node; node = node.parent) if (node.tag === "a" && node.getAttribute("href")) return node;
        return null;
      },
      querySelector(selector) {
        assert.equal(selector, "a, button");
        const queue = [...el.children];
        while (queue.length) {
          const node = queue.shift();
          if (node.tag === "a" || node.tag === "button") return node;
          queue.push(...node.children);
        }
        return null;
      }
    };
    if (parent) parent.children.push(el);
    return el;
  }

  function run(listeners, event) {
    for (const listener of (listeners || []).slice()) {
      try {
        listener(event);
      } catch (error) {
        errors.push(error);
      }
    }
  }

  function dispatch(target, type, init = {}) {
    const event = {
      type, target, defaultPrevented: false, ...init,
      preventDefault() { this.defaultPrevented = true; }
    };
    for (let node = target; node; node = node.parent) run(node.listeners[type], event);
    if (type !== "DOMContentLoaded") run(doc.listeners[type], event);
    return event;
  }

  const html = element("html", null, rtl ? { dir: "rtl", lang: "ar-SA" } : { dir: "ltr", lang: "en-US" });
  const body = element("body", null, {}, html);
  body.classList.add("wasla-tenant");
  body.classList.add("wasla-tenant-shell");
  const wrapper = element("div", null, { class: "app-wrapper" }, body);
  const sidebar = element("aside", "app-sidebar-tenant", { "data-wasla-nav": "sidebar", role: "navigation" }, wrapper);
  const collapse = element("button", "tenantSidebarToggle", {
    type: "button",
    "data-title-collapse": "Collapse sidebar",
    "data-title-expand": "Expand sidebar",
    "data-label-toggle": "Toggle sidebar",
    title: "Collapse sidebar",
    "aria-expanded": "true",
    "aria-controls": "app-sidebar-tenant"
  }, sidebar);
  const close = element("button", "tenantNavClose", { type: "button", "data-wasla-nav": "close", "aria-label": "Close navigation" }, sidebar);
  const link = element("a", null, { href: "/orders" }, sidebar);
  const linkText = element("span", null, { class: "nav-text" }, link);
  const main = element("main", "tenant-main", { role: "main" }, wrapper);
  const open = element("button", "tenantNavOpen", {
    type: "button", "data-wasla-nav": "trigger", "aria-controls": "app-sidebar-tenant", "aria-expanded": "false", "aria-label": "Open navigation"
  }, main);
  const backdrop = element("div", "tenantNavBackdrop", { "data-wasla-nav": "backdrop" }, body);
  backdrop.hidden = true;

  doc.documentElement = html;
  doc.body = body;
  doc.activeElement = body;
  doc.addEventListener = (type, listener) => (doc.listeners[type] = doc.listeners[type] || []).push(listener);
  const byId = { "app-sidebar-tenant": sidebar, tenantSidebarToggle: collapse, tenantNavClose: close, "tenant-main": main, tenantNavOpen: open, tenantNavBackdrop: backdrop };
  doc.getElementById = (id) => byId[id] || null;
  doc.querySelector = (selector) => {
    assert.equal(selector, ".app-wrapper");
    return wrapper;
  };

  const media = {
    width,
    listeners: [],
    get matches() { return this.width <= 991.98; },
    addEventListener(type, listener) { assert.equal(type, "change"); this.listeners.push(listener); }
  };

  return {
    doc, media, errors, dispatch,
    els: { html, body, sidebar, collapse, close, link, linkText, main, open, backdrop },
    resize(nextWidth) {
      const before = media.matches;
      media.width = nextWidth;
      if (before !== media.matches) run(media.listeners, { matches: media.matches });
    },
    key(key) { return dispatch(doc.activeElement || body, "keydown", { key }); },
    click(target) { return dispatch(target, "click"); },
    collapsed: () => body.classList.contains("sidebar-collapsed"),
    drawerOpen: () => body.classList.contains("wasla-nav-open")
  };
}

// One page load: the script runs, then DOMContentLoaded fires, as with the layout's deferred-at-end <script>.
function load({ storage, blockProperty = false, width = 1366, rtl = false } = {}) {
  const page = tenantPage({ width, rtl });
  const win = {
    document: page.doc,
    matchMedia(query) {
      assert.equal(query, MOBILE_QUERY);
      return page.media;
    },
    scrollTo() {},
    scrollY: 0,
    pageYOffset: 0
  };
  win.window = win;
  if (blockProperty) {
    Object.defineProperty(win, "localStorage", { get() { throw securityError(); } });
  } else {
    win.localStorage = storage;
  }
  vm.runInContext(source, vm.createContext(win), { filename: "sidebar-toggle.js" });
  for (const listener of (page.doc.listeners.DOMContentLoaded || []).slice()) {
    try {
      listener({ type: "DOMContentLoaded" });
    } catch (error) {
      page.errors.push(error);
    }
  }
  return page;
}

function assertDesktopState(page, collapsed, label) {
  const { collapse } = page.els;
  assert.equal(page.collapsed(), collapsed, `${label}: sidebar-collapsed`);
  assert.equal(collapse.getAttribute("aria-expanded"), collapsed ? "false" : "true", `${label}: aria-expanded`);
  assert.equal(collapse.getAttribute("title"), collapsed ? "Expand sidebar" : "Collapse sidebar", `${label}: title`);
  assert.equal(collapse.getAttribute("aria-label"), "Toggle sidebar", `${label}: aria-label`);
}

function assertNoErrors(page, label) {
  assert.deepEqual(page.errors.map((error) => `${error.name}: ${error.message}`), [], `${label}: page errors`);
}

// The full navigation contract on a phone: open, focus, close by every route, focus restoration, inert state.
function exerciseMobileDrawer(page, label) {
  const { body, sidebar, main, open, close, backdrop, link, linkText } = page.els;
  assert.ok(body.classList.contains("wasla-shell-mobile"), `${label}: mobile shell`);
  assert.equal(sidebar.getAttribute("aria-hidden"), "true", `${label}: closed drawer hidden from assistive technology`);
  assert.equal(sidebar.inert, true, `${label}: closed drawer inert`);
  assert.equal(open.getAttribute("data-wasla-nav-owner"), "sidebar-toggle", `${label}: trigger bound`);

  const routes = [
    ["Escape", () => page.key("Escape")],
    ["close button", () => page.click(close)],
    // Pressing on the (non-focusable) backdrop first moves focus to <body>.
    ["backdrop", () => { page.doc.activeElement = body; page.click(backdrop); }],
    ["trigger", () => page.click(open)],
    ["navigation link", () => page.click(linkText)]
  ];
  for (const [route, closeBy] of routes) {
    open.focus();
    const opened = page.click(open);
    assert.ok(opened.defaultPrevented, `${label}: trigger click handled`);
    assert.ok(page.drawerOpen(), `${label}: drawer opens before closing by ${route}`);
    assert.ok(body.classList.contains("wasla-nav-lock"), `${label}: scroll lock`);
    assert.equal(backdrop.hidden, false, `${label}: backdrop shown`);
    assert.equal(open.getAttribute("aria-expanded"), "true", `${label}: trigger expanded`);
    assert.equal(open.getAttribute("aria-label"), "Close navigation", `${label}: trigger label while open`);
    assert.equal(sidebar.getAttribute("aria-hidden"), null, `${label}: open drawer exposed`);
    assert.equal(sidebar.inert, false, `${label}: open drawer interactive`);
    assert.equal(main.inert, true, `${label}: content behind the drawer inert`);
    assert.equal(page.doc.activeElement, close, `${label}: focus moves into the drawer`);

    if (route === "navigation link") link.focus();
    closeBy();
    assert.ok(!page.drawerOpen(), `${label}: drawer closes by ${route}`);
    assert.ok(!body.classList.contains("wasla-nav-lock"), `${label}: scroll lock released by ${route}`);
    assert.equal(backdrop.hidden, true, `${label}: backdrop hidden by ${route}`);
    assert.equal(open.getAttribute("aria-expanded"), "false", `${label}: trigger collapsed by ${route}`);
    assert.equal(open.getAttribute("aria-label"), "Open navigation", `${label}: trigger label restored by ${route}`);
    assert.equal(sidebar.getAttribute("aria-hidden"), "true", `${label}: drawer hidden by ${route}`);
    assert.equal(sidebar.inert, true, `${label}: drawer inert by ${route}`);
    assert.equal(main.inert, false, `${label}: content interactive by ${route}`);
    assert.ok(!sidebar.contains(page.doc.activeElement), `${label}: focus left the closed drawer after ${route}`);
    if (route !== "backdrop") {
      // Existing behavior: after a backdrop press focus stays on <body> (the trigger is still inert when it is focused).
      assert.equal(page.doc.activeElement, open, `${label}: focus restored to the trigger by ${route}`);
    }
  }

  // Escape with the drawer closed is left to the page.
  assert.equal(page.key("Escape").defaultPrevented, false, `${label}: Escape ignored while closed`);
}

test("a stored preference is read on load, written on toggle, and read again on the next load", () => {
  const storage = originStorage({ ...UNRELATED, [KEY]: "true" });
  let page = load({ storage });
  assertNoErrors(page, "collapsed load");
  assertDesktopState(page, true, "stored true");
  assert.ok(page.els.body.classList.contains("wasla-shell-desktop"));

  assert.ok(page.click(page.els.collapse).defaultPrevented);
  assertDesktopState(page, false, "after expanding");
  assert.equal(storage.snapshot()[KEY], "false");

  page.click(page.els.collapse);
  assertDesktopState(page, true, "after collapsing");
  assert.equal(storage.snapshot()[KEY], "true");

  page = load({ storage });
  assertDesktopState(page, true, "next page load");
  page.click(page.els.collapse);
  page = load({ storage });
  assertDesktopState(page, false, "next page load after expanding");
  assertNoErrors(page, "reloads");
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "false" });
  assert.ok(!storage.calls.includes("removeItem") && !storage.calls.includes("clear"));
});

test("a missing preference uses the expanded default and writes nothing until the user toggles", () => {
  const storage = originStorage({ ...UNRELATED });
  const page = load({ storage });
  assertNoErrors(page, "missing value");
  assertDesktopState(page, false, "missing value");
  assert.deepEqual(storage.calls.filter((call) => call !== "getItem"), []);
  assert.deepEqual(storage.snapshot(), UNRELATED);
});

for (const invalid of ["false", "", "TRUE", "1", "yes", "null", "undefined", " true", "{\"collapsed\":true}"]) {
  test(`an invalid stored value ${JSON.stringify(invalid)} uses the expanded default and is left as it is`, () => {
    const storage = originStorage({ ...UNRELATED, [KEY]: invalid });
    const page = load({ storage });
    assertNoErrors(page, invalid);
    assertDesktopState(page, false, invalid);
    assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: invalid });
    assert.deepEqual(storage.calls.filter((call) => call !== "getItem"), []);
  });
}

test("throwing localStorage property access: the default applies and desktop collapse works for the page", () => {
  const { storage, blockProperty } = FAILURES["localStorage property access throws"]();
  const page = load({ storage, blockProperty });
  assertNoErrors(page, "blocked property");
  assertDesktopState(page, false, "blocked property");
  page.click(page.els.collapse);
  assertDesktopState(page, true, "collapsed without storage");
  page.click(page.els.collapse);
  assertDesktopState(page, false, "expanded without storage");
  assertNoErrors(page, "blocked property toggles");
  assert.deepEqual(storage.calls, []);
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "true" });
});

test("throwing getItem: the default applies, and a toggle is still saved", () => {
  const { storage } = FAILURES["getItem throws"]();
  const page = load({ storage });
  assertNoErrors(page, "getItem");
  assertDesktopState(page, false, "getItem throws");
  page.click(page.els.collapse);
  assertDesktopState(page, true, "after collapsing");
  assert.equal(storage.snapshot()[KEY], "true");
  page.click(page.els.collapse);
  assert.equal(storage.snapshot()[KEY], "false");
  assertNoErrors(page, "getItem toggles");
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "false" });
});

test("throwing setItem: a stored preference is still read, and toggling works for the page without saving", () => {
  const storage = originStorage({ ...UNRELATED, [KEY]: "true" }, ["setItem"]);
  const page = load({ storage });
  assertDesktopState(page, true, "stored true");
  page.click(page.els.collapse);
  assertDesktopState(page, false, "expanded although the save failed");
  page.click(page.els.collapse);
  assertDesktopState(page, true, "collapsed again");
  assertNoErrors(page, "setItem");
  assert.ok(storage.calls.includes("setItem"), "the save was attempted");
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "true" });
});

test("throwing removeItem: nothing calls it, and the preference still round-trips", () => {
  const { storage } = FAILURES["removeItem throws"]();
  let page = load({ storage });
  assertDesktopState(page, false, "missing value");
  page.click(page.els.collapse);
  page = load({ storage });
  assertDesktopState(page, true, "saved and read back");
  assertNoErrors(page, "removeItem");
  assert.ok(!storage.calls.includes("removeItem"));
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "true" });
});

for (const [failure, situation] of Object.entries(FAILURES)) {
  for (const rtl of [false, true]) {
    const direction = rtl ? "Arabic RTL" : "English LTR";

    test(`${failure} (${direction}): the phone drawer is bound and opens, closes and restores focus`, () => {
      const { storage, blockProperty } = situation();
      const page = load({ storage, blockProperty, width: 390, rtl });
      assertNoErrors(page, failure);
      exerciseMobileDrawer(page, `${failure} ${direction}`);
      assertNoErrors(page, `${failure} after the drawer`);
      assert.equal(page.els.html.getAttribute("dir"), rtl ? "rtl" : "ltr");
    });

    test(`${failure} (${direction}): desktop collapse is bound and survives breakpoint changes`, () => {
      const { storage, blockProperty } = situation();
      const page = load({ storage, blockProperty, width: 1366, rtl });
      assertNoErrors(page, failure);
      const startCollapsed = page.collapsed();
      page.click(page.els.collapse);
      assert.equal(page.collapsed(), !startCollapsed, `${failure}: collapse toggles`);
      assert.equal(page.els.collapse.getAttribute("aria-expanded"), startCollapsed ? "true" : "false");

      // Phone width, the drawer, and back to desktop: the shell is restored without errors.
      page.resize(820);
      assert.ok(page.els.body.classList.contains("wasla-shell-mobile"), `${failure}: tablet uses the drawer`);
      exerciseMobileDrawer(page, `${failure} ${direction} 820px`);
      page.click(page.els.open);
      page.resize(1366);
      assert.ok(page.els.body.classList.contains("wasla-shell-desktop"), `${failure}: desktop shell restored`);
      assert.equal(page.collapsed(), !startCollapsed, `${failure}: the page's choice survives the breakpoint changes`);
      assert.ok(!page.drawerOpen(), `${failure}: drawer closed on desktop`);
      assert.equal(page.els.sidebar.getAttribute("aria-hidden"), null);
      assert.equal(page.els.sidebar.inert, false);
      assert.equal(page.els.main.inert, false);
      page.click(page.els.collapse);
      assertNoErrors(page, `${failure} after breakpoint changes`);
    });
  }
}

test("with the drawer at phone width, the desktop collapse control leaves the preference alone", () => {
  const storage = originStorage({ ...UNRELATED, [KEY]: "true" });
  const page = load({ storage, width: 390 });
  page.click(page.els.collapse);
  assert.deepEqual(storage.snapshot(), { ...UNRELATED, [KEY]: "true" });
  assert.ok(!storage.calls.includes("setItem"));
  assertNoErrors(page, "phone collapse click");
});

test("no storage situation clears or changes another key", () => {
  for (const [failure, situation] of Object.entries(FAILURES)) {
    const { storage, blockProperty } = situation();
    if (!storage) continue;
    const before = storage.snapshot();
    const page = load({ storage, blockProperty, width: 1366 });
    page.click(page.els.collapse);
    page.click(page.els.collapse);
    page.resize(390);
    page.click(page.els.open);
    page.key("Escape");
    page.resize(1366);
    load({ storage, blockProperty, width: 390 });
    assertNoErrors(page, failure);

    const after = storage.snapshot();
    for (const key of Object.keys(UNRELATED)) {
      assert.equal(after[key], before[key], `${failure}: ${key}`);
    }
    assert.deepEqual(Object.keys(after).filter((key) => key !== KEY).sort(), Object.keys(before).filter((key) => key !== KEY).sort(), failure);
    assert.ok(!storage.calls.includes("removeItem") && !storage.calls.includes("clear"), `${failure}: nothing removed`);
  }
});
