const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const vm = require("node:vm");

const viewSource = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-view.js"), "utf8");
const storeSource = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/js/orders/orders-live-store.js"), "utf8");
const cssSource = fs.readFileSync(require.resolve("../../../src/Wasla.Web/wwwroot/css/wasla-theme.css"), "utf8");

function classes() {
  const names = new Set();
  return {
    names: names,
    add: function () { for (let i = 0; i < arguments.length; i++) names.add(arguments[i]); },
    remove: function () { for (let i = 0; i < arguments.length; i++) names.delete(arguments[i]); },
    contains: function (name) { return names.has(name); },
    toggle: function (name, on) {
      if (on) names.add(name);
      else names.delete(name);
    }
  };
}

function control(view) {
  const el = {
    classList: classes(),
    attrs: { "data-live-screen-view": view },
    getAttribute: function (name) { return el.attrs[name]; },
    setAttribute: function (name, value) { el.attrs[name] = value; },
    closest: function (selector) {
      if (selector === "button[data-live-screen-view]") return el;
      return null;
    }
  };
  return el;
}

function load(options) {
  const reduced = !!(options && options.reducedMotion);
  const host = {
    id: "ordersLiveScreenHost",
    classList: classes(),
    attrs: { "data-live-screen-view": "board" },
    offsetWidth: 1,
    listeners: [],
    getAttribute: function (name) { return host.attrs[name]; },
    setAttribute: function (name, value) { host.attrs[name] = value; },
    addEventListener: function (type, fn) { host.listeners.push({ type: type, fn: fn }); },
    removeEventListener: function (type, fn) {
      host.listeners = host.listeners.filter(function (listener) {
        return listener.type !== type || listener.fn !== fn;
      });
    }
  };
  const buttons = {
    board: control("board"),
    list: control("list"),
    focus: control("focus")
  };
  const nodes = [host, buttons.board, buttons.list, buttons.focus];
  let init;
  let refreshes = 0;
  const O = {
    opts: { pageMode: "liveDisplay" },
    liveStore: { refreshView: function () { refreshes += 1; } }
  };
  const document = {
    getElementById: function (id) { return id === "ordersLiveScreenHost" ? host : null; },
    querySelectorAll: function () { return nodes; },
    addEventListener: function (name, fn) { if (name === "DOMContentLoaded") init = fn; else document.clicks = fn; }
  };
  const window = {
    WaslaOrders: O,
    matchMedia: function () { return { matches: reduced }; }
  };
  vm.runInNewContext(viewSource, {
    window: window,
    document: document,
    localStorage: { getItem: function () { return "board"; }, setItem: function () {} }
  });
  init();
  const afterInit = refreshes;
  return {
    O: O,
    host: host,
    click: function (view) { document.clicks({ target: buttons[view] }); },
    refreshes: function () { return refreshes - afterInit; }
  };
}

function motion(host) {
  return {
    enter: host.classList.contains("wasla-live-view-enter"),
    forward: host.classList.contains("wasla-live-view-enter--forward"),
    back: host.classList.contains("wasla-live-view-enter--back")
  };
}

test("switching Board to List then List to Focus enters forward", function () {
  const h = load();
  assert.equal(motion(h.host).enter, false);
  h.click("list");
  assert.equal(h.O.liveView.getView(), "list");
  assert.deepEqual(motion(h.host), { enter: true, forward: true, back: false });
  h.click("focus");
  assert.equal(h.O.liveView.getView(), "focus");
  assert.deepEqual(motion(h.host), { enter: true, forward: true, back: false });
});

test("switching backward uses the opposite direction", function () {
  const h = load();
  h.click("focus");
  assert.equal(motion(h.host).forward, true);
  h.click("list");
  assert.deepEqual(motion(h.host), { enter: true, forward: false, back: true });
  h.click("board");
  assert.equal(motion(h.host).back, true);
});

test("clicking the active view does not retrigger the transition", function () {
  const h = load();
  h.click("list");
  h.host.classList.remove("wasla-live-view-enter", "wasla-live-view-enter--forward", "wasla-live-view-enter--back");
  const refreshes = h.refreshes();
  h.click("list");
  assert.equal(h.O.liveView.getView(), "list");
  assert.equal(motion(h.host).enter, false);
  assert.equal(h.refreshes(), refreshes);
});

test("polling refresh does not start a view transition", function () {
  const h = load();
  h.click("list");
  h.host.classList.remove("wasla-live-view-enter", "wasla-live-view-enter--forward", "wasla-live-view-enter--back");
  h.O.liveStore.refreshView();
  h.O.liveView.setView("list");
  assert.equal(motion(h.host).enter, false);
  assert.equal(storeSource.includes("wasla-live-view-enter"), false);
});

test("reduced motion switches immediately without a transition class", function () {
  const h = load({ reducedMotion: true });
  h.click("list");
  assert.equal(h.O.liveView.getView(), "list");
  assert.equal(motion(h.host).enter, false);
  h.click("board");
  assert.equal(h.O.liveView.getView(), "board");
  assert.equal(motion(h.host).enter, false);
});

test("a child animation end does not cancel the view transition", function () {
  const h = load();
  h.click("list");
  const listener = h.host.listeners.find(function (item) { return item.type === "animationend"; });
  listener.fn({ target: { id: "card" } });
  assert.equal(motion(h.host).enter, true);
  listener.fn({ target: h.host });
  assert.equal(motion(h.host).enter, false);
});

test("view transition css stays short, directional, and disabled for reduced motion", function () {
  assert.match(cssSource, /animation:\s*wasla-live-view-enter\s+160ms\s+ease-out\s+both/);
  assert.match(cssSource, /opacity:\s*0\.96/);
  assert.match(cssSource, /--wasla-live-view-shift:\s*12px/);
  assert.match(cssSource, /--wasla-live-view-shift:\s*-12px/);
  assert.match(cssSource, /\[dir="rtl"\].*wasla-live-view-enter--back/s);
  assert.match(cssSource, /prefers-reduced-motion:\s*reduce[\s\S]*wasla-live-screen-host\.wasla-live-view-enter[\s\S]*animation:\s*none/);
});
