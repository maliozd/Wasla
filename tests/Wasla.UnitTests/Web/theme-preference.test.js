const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const root = path.join(__dirname, "..", "..", "..");
const themes = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "theme-preference.js"));

const TENANT_KEY = "Wasla.tenant.theme";
const ADMIN_KEY = "Wasla.theme";

// localStorage is shared by every page of one origin and survives reloads and new browser sessions.
function originStorage(initial) {
  const values = new Map(Object.entries(initial || {}));
  return {
    getItem: (key) => (values.has(key) ? values.get(key) : null),
    setItem: (key, value) => values.set(key, String(value)),
    removeItem: (key) => values.delete(key),
    clear: () => values.clear(),
    snapshot: () => Object.fromEntries(values)
  };
}

function systemTheme(dark) {
  const listeners = [];
  return {
    matches: dark,
    addEventListener: (type, listener) => {
      assert.equal(type, "change");
      listeners.push(listener);
    },
    change(nextDark) {
      this.matches = nextDark;
      listeners.forEach((listener) => listener({ matches: nextDark }));
    }
  };
}

function element() {
  const attributes = {};
  return {
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = String(value); }
  };
}

// One page load: a fresh document and script run against the origin's storage and the system theme.
function load({ scope, storage, system, bodyLater = false }) {
  const html = element();
  if (scope) html.setAttribute("data-wasla-theme-scope", scope);
  const documentListeners = {};
  const windowListeners = {};
  const observers = [];
  const doc = {
    documentElement: html,
    body: bodyLater ? null : element(),
    addEventListener: (type, listener) => (documentListeners[type] = documentListeners[type] || []).push(listener)
  };
  const win = {
    document: doc,
    localStorage: storage,
    addEventListener: (type, listener) => (windowListeners[type] = windowListeners[type] || []).push(listener),
    MutationObserver: class {
      constructor(callback) { this.callback = callback; this.connected = false; observers.push(this); }
      observe(target, options) { assert.equal(target, html); assert.deepEqual(options, { childList: true }); this.connected = true; }
      disconnect() { this.connected = false; }
    }
  };
  if (system !== undefined) {
    win.matchMedia = (query) => {
      assert.equal(query, "(prefers-color-scheme: dark)");
      return system;
    };
  }

  const controller = themes.start(win);
  return {
    controller,
    doc,
    observers,
    theme: () => html.getAttribute("data-bs-theme"),
    bodyTheme: () => (doc.body ? doc.body.getAttribute("data-bs-theme") : null),
    insertBody() {
      doc.body = element();
      observers.filter((o) => o.connected).forEach((o) => o.callback([]));
    },
    domContentLoaded: () => (documentListeners.DOMContentLoaded || []).forEach((listener) => listener()),
    storageEvent: (key) => (windowListeners.storage || []).forEach((listener) => listener({ key }))
  };
}

test("precedence: an explicit choice wins; otherwise the system theme; otherwise light", () => {
  assert.deepEqual(themes.resolve("dark", false, true), { theme: "dark", source: "explicit" });
  assert.deepEqual(themes.resolve("light", true, true), { theme: "light", source: "explicit" });
  assert.deepEqual(themes.resolve(null, true, true), { theme: "dark", source: "system" });
  assert.deepEqual(themes.resolve(null, false, true), { theme: "light", source: "system" });
  assert.deepEqual(themes.resolve(null, null, true), { theme: "light", source: "default" });
  // Central Admin does not follow the system theme.
  assert.deepEqual(themes.resolve(null, true, false), { theme: "light", source: "default" });
  assert.deepEqual(themes.resolve("dark", false, false), { theme: "dark", source: "explicit" });
  // Anything but the two exact values is no choice at all.
  for (const value of ["Dark", "auto", "system", "", " dark", undefined, null]) {
    assert.deepEqual(themes.resolve(value, true, true), { theme: "dark", source: "system" }, String(value));
  }
});

test("the tenant app and Central Admin never share a storage key", () => {
  assert.equal(themes.SCOPES.tenant.storageKey, TENANT_KEY);
  assert.equal(themes.SCOPES.admin.storageKey, ADMIN_KEY);
  assert.notEqual(themes.SCOPES.tenant.storageKey, themes.SCOPES.admin.storageKey);
  assert.equal(themes.SCOPES.tenant.followSystem, true);
  assert.equal(themes.SCOPES.admin.followSystem, false);
});

test("tenant without a choice: system dark gives dark, system light gives light, and nothing is stored", () => {
  for (const dark of [true, false]) {
    const storage = originStorage();
    const page = load({ scope: "tenant", storage, system: systemTheme(dark) });
    const expected = dark ? "dark" : "light";
    assert.equal(page.theme(), expected);
    assert.equal(page.bodyTheme(), expected);
    assert.deepEqual(page.controller.current(), { theme: expected, source: "system" });
    assert.deepEqual(storage.snapshot(), {});
  }
});

test("tenant explicit choices beat the opposite system theme", () => {
  let page = load({ scope: "tenant", storage: originStorage({ [TENANT_KEY]: "light" }), system: systemTheme(true) });
  assert.deepEqual(page.controller.current(), { theme: "light", source: "explicit" });
  assert.equal(page.theme(), "light");

  page = load({ scope: "tenant", storage: originStorage({ [TENANT_KEY]: "dark" }), system: systemTheme(false) });
  assert.deepEqual(page.controller.current(), { theme: "dark", source: "explicit" });
  assert.equal(page.bodyTheme(), "dark");
});

test("an explicit choice persists across navigation, reloads and new browser sessions", () => {
  const storage = originStorage();
  const system = systemTheme(false);
  const first = load({ scope: "tenant", storage, system });
  first.controller.choose("dark");
  assert.equal(first.theme(), "dark");
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "dark" });

  // Every later page load (another page, a reload, a new session) reads the same stored choice.
  for (let load_ = 0; load_ < 3; load_++) {
    const next = load({ scope: "tenant", storage, system });
    assert.deepEqual(next.controller.current(), { theme: "dark", source: "explicit" });
  }

  const back = load({ scope: "tenant", storage, system: systemTheme(true) });
  back.controller.choose("light");
  assert.equal(load({ scope: "tenant", storage, system: systemTheme(true) }).theme(), "light");
});

test("a later system change follows the system only until the user makes a choice", () => {
  const storage = originStorage();
  const system = systemTheme(false);
  const page = load({ scope: "tenant", storage, system });
  const seen = [];
  page.controller.subscribe((state) => seen.push(`${state.theme}/${state.source}`));

  system.change(true);
  assert.equal(page.theme(), "dark");
  system.change(false);
  assert.equal(page.theme(), "light");

  page.controller.choose("light");
  system.change(true);
  assert.deepEqual(page.controller.current(), { theme: "light", source: "explicit" });
  assert.equal(page.theme(), "light");
  assert.equal(page.bodyTheme(), "light");
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "light" });

  page.controller.choose("dark");
  system.change(false);
  assert.equal(page.theme(), "dark");

  assert.deepEqual(seen, ["dark/system", "light/system", "light/explicit", "dark/explicit"]);
});

test("the toggle turns the shown theme into the opposite explicit choice", () => {
  const storage = originStorage();
  const page = load({ scope: "tenant", storage, system: systemTheme(true) });
  page.controller.toggle();
  assert.deepEqual(page.controller.current(), { theme: "light", source: "explicit" });
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "light" });
  page.controller.toggle();
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "dark" });

  page.controller.choose("blue");
  assert.deepEqual(page.controller.current(), { theme: "dark", source: "explicit" });
});

test("tenant and Central Admin on one origin never overwrite each other", () => {
  const storage = originStorage();
  const system = systemTheme(true);

  const tenant = load({ scope: "tenant", storage, system });
  tenant.controller.choose("light");
  const admin = load({ scope: "admin", storage, system });
  assert.deepEqual(admin.controller.current(), { theme: "light", source: "default" });
  admin.controller.choose("dark");
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "light", [ADMIN_KEY]: "dark" });

  assert.equal(load({ scope: "tenant", storage, system }).theme(), "light");
  assert.equal(load({ scope: "admin", storage, system }).theme(), "dark");

  load({ scope: "tenant", storage, system }).controller.choose("dark");
  load({ scope: "admin", storage, system }).controller.choose("light");
  assert.deepEqual(storage.snapshot(), { [TENANT_KEY]: "dark", [ADMIN_KEY]: "light" });
});

test("a value under the old shared key does not decide the tenant theme", () => {
  // Not a reliable tenant choice: the previous script wrote "light" there whenever nothing was stored, and the
  // tenant app and Central Admin both wrote to it.
  for (const legacy of ["light", "dark"]) {
    const storage = originStorage({ [ADMIN_KEY]: legacy });
    const page = load({ scope: "tenant", storage, system: systemTheme(true) });
    assert.deepEqual(page.controller.current(), { theme: "dark", source: "system" }, legacy);
  }
});

test("Central Admin keeps its existing behavior: its stored choice, else light, whatever the system says", () => {
  assert.equal(load({ scope: "admin", storage: originStorage(), system: systemTheme(true) }).theme(), "light");
  assert.equal(load({ scope: "admin", storage: originStorage({ [ADMIN_KEY]: "dark" }), system: systemTheme(false) }).theme(), "dark");

  const system = systemTheme(false);
  const admin = load({ scope: "admin", storage: originStorage(), system });
  system.change(true);
  assert.equal(admin.theme(), "light");
});

test("another tab's choice applies to open pages of the same app only", () => {
  const storage = originStorage();
  const system = systemTheme(false);
  const tenant = load({ scope: "tenant", storage, system });
  const admin = load({ scope: "admin", storage, system });

  storage.setItem(TENANT_KEY, "dark");
  tenant.storageEvent(TENANT_KEY);
  admin.storageEvent(TENANT_KEY);
  assert.deepEqual(tenant.controller.current(), { theme: "dark", source: "explicit" });
  assert.equal(admin.theme(), "light");

  storage.setItem(ADMIN_KEY, "dark");
  tenant.storageEvent(ADMIN_KEY);
  assert.equal(tenant.theme(), "dark");

  // Cleared site data: back to the system theme.
  storage.clear();
  tenant.storageEvent(null);
  assert.deepEqual(tenant.controller.current(), { theme: "light", source: "system" });
});

test("blocked storage falls back to the system theme and still applies a choice on the page", () => {
  const blocked = {
    getItem() { throw new Error("SecurityError"); },
    setItem() { throw new Error("SecurityError"); }
  };
  const page = load({ scope: "tenant", storage: blocked, system: systemTheme(true) });
  assert.deepEqual(page.controller.current(), { theme: "dark", source: "system" });
  page.controller.choose("light");
  assert.equal(page.theme(), "light");

  const noMatchMedia = load({ scope: "tenant", storage: originStorage() });
  assert.deepEqual(noMatchMedia.controller.current(), { theme: "light", source: "default" });
});

test("a throwing localStorage getter or matchMedia does not break the page", () => {
  const html = element();
  html.setAttribute("data-wasla-theme-scope", "tenant");
  const win = {
    document: { documentElement: html, body: element(), addEventListener() {} },
    addEventListener() {},
    matchMedia() { throw new Error("not supported"); }
  };
  Object.defineProperty(win, "localStorage", { get() { throw new Error("SecurityError"); } });

  const controller = themes.start(win);
  assert.deepEqual(controller.current(), { theme: "light", source: "default" });
  controller.choose("dark");
  assert.deepEqual(controller.current(), { theme: "dark", source: "explicit" });
  assert.equal(html.getAttribute("data-bs-theme"), "dark");
});

test("browsers with only the legacy media-query addListener still follow system changes", () => {
  const listeners = [];
  const legacy = { matches: false, addListener: (listener) => listeners.push(listener) };
  const page = load({ scope: "tenant", storage: originStorage(), system: legacy });
  assert.equal(listeners.length, 1);
  legacy.matches = true;
  listeners.forEach((listener) => listener({ matches: true }));
  assert.deepEqual(page.controller.current(), { theme: "dark", source: "system" });
});

test("the body is tagged as soon as the parser inserts it, before the first frame", () => {
  const page = load({ scope: "tenant", storage: originStorage({ [TENANT_KEY]: "dark" }), system: systemTheme(false), bodyLater: true });
  assert.equal(page.theme(), "dark");
  assert.equal(page.bodyTheme(), null);
  assert.equal(page.observers.length, 1);
  page.insertBody();
  assert.equal(page.bodyTheme(), "dark");
  assert.equal(page.observers[0].connected, false);
  page.doc.body.setAttribute("data-bs-theme", "light");
  page.domContentLoaded();
  assert.equal(page.bodyTheme(), "dark");
});

test("a page without a known theme scope is left alone", () => {
  for (const scope of [null, "public", "toString", "__proto__"]) {
    const page = load({ scope, storage: originStorage({ [TENANT_KEY]: "dark" }), system: systemTheme(true) });
    assert.equal(page.controller, null, String(scope));
    assert.equal(page.theme(), null);
    assert.equal(page.bodyTheme(), null);
  }
});
