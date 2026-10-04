const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// The Print Bridge Devices page: one device snapshot path (initial state, 15-second refresh, page visibility, toggles,
// presence expiry and a future push), UTC-safe times shown in the restaurant's time zone and the UI culture.
// Everything runs on a virtual clock: no test depends on real time.

const source = fs.readFileSync(
  path.join(__dirname, "..", "..", "..", "src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-page.js"), "utf8");

const DEVICES_URL = "/print-bridge/devices/list";
const JOBS_URL = "/print-bridge/print-jobs";
const T0 = Date.parse("2026-10-01T08:49:30.000Z");
const iso = (ms) => new Date(ms).toISOString();
const OPTIONS = { timeZone: "Europe/Istanbul", dateStyle: "short", timeStyle: "medium" };
const expected = (culture, ms) => new Intl.DateTimeFormat(culture, OPTIONS).format(new Date(ms));

const MESSAGES = {
  ordersActive: "Active", ordersPassive: "Passive", statusOnline: "Online", statusOffline: "Offline", statusUnknown: "Unknown",
  statusInactive: "Inactive", lastSeen: "Last seen", lastSeenNever: "Never", versionUnknown: "Unknown version", details: "Details",
  emptyValue: "—", lastConnectedNone: "None", noDevicesTitle: "No devices yet", noDevicesDescription: "Set one up.",
  setupNewDevice: "Set up a new device", machineName: "Computer", printerName: "Printer", deviceActive: "Active",
  deviceUpdateFailed: "Update failed", reprintFailed: "Request failed"
};

function device(overrides = {}) {
  return Object.assign({
    id: "dev-1", name: "Kitchen", isActive: true, connectionStatus: "Connected", connectionStatusLabelKey: "PrintBridge.StatusConnected",
    isConnected: true, lastSeenAtUtc: iso(T0), machineName: "KITCHEN-PC", localAlias: null, printerName: "POS-58", appVersion: "1.4.0",
    detailsUrl: "/print-bridge/devices/dev-1"
  }, overrides);
}

function snapshot({ serverMs = T0 + 5000, devices = [device()], quota = {} } = {}) {
  const seen = devices.map((d) => Date.parse(d.lastSeenAtUtc || "")).filter((v) => !isNaN(v));
  const latest = seen.length ? Math.max(...seen) : null;
  return {
    devices,
    quota: Object.assign({
      allowedActiveDeviceCount: 3,
      activeDeviceCount: devices.filter((d) => d.isActive).length,
      canCreateActiveDevice: true,
      activeCountExceedsLimit: false,
      connectedDeviceCount: devices.filter((d) => d.connectionStatus === "Connected").length,
      latestLastSeenAtUtc: latest === null ? null : iso(latest),
      lastConnectedDeviceName: latest === null ? null : devices.find((d) => Date.parse(d.lastSeenAtUtc) === latest).name
    }, quota),
    serverTimeUtc: iso(serverMs),
    connectedThresholdSeconds: 60,
    recentlySeenThresholdSeconds: 300
  };
}

function el(id) {
  const classes = new Set(id.endsWith("Alert") ? ["d-none"] : []);
  let html = "";
  const node = {
    id, textContent: "", writes: 0, attributes: {},
    get innerHTML() { return html; },
    set innerHTML(v) { html = String(v); node.writes++; },
    classList: {
      add: (...c) => c.forEach((x) => classes.add(x)), remove: (...c) => c.forEach((x) => classes.delete(x)),
      toggle: (c, on) => ((on === undefined ? !classes.has(c) : on) ? classes.add(c) : classes.delete(c)), contains: (c) => classes.has(c)
    },
    getAttribute: (n) => (n in node.attributes ? node.attributes[n] : null),
    setAttribute: (n, v) => { node.attributes[n] = String(v); },
    addEventListener: () => {},
    contains: () => false,
    querySelector: () => null,
    querySelectorAll: () => []
  };
  return node;
}

/** One Devices page on a virtual clock. <clientOffsetMs> is the browser clock minus the server clock. */
function page({ initial = snapshot(), clientOffsetMs = 0, culture = "tr-TR", visibility = "visible", timeZoneId = "Europe/Istanbul" } = {}) {
  const clock = { now: Date.parse(initial.serverTimeUtc) + clientOffsetMs };
  class FakeDate extends Date {
    constructor(...args) { if (args.length === 0) super(clock.now); else super(...args); }
    static now() { return clock.now; }
  }

  const timers = [];
  let nextId = 1;
  const addTimer = (fn, ms, repeat) => { const id = nextId++; timers.push({ id, fn, at: clock.now + Math.max(0, ms || 0), ms, repeat }); return id; };
  const clearTimer = (id) => { const i = timers.findIndex((t) => t.id === id); if (i >= 0) timers.splice(i, 1); };

  const ids = ["printBridgeDevicesPanel", "printBridgeSummaryActive", "printBridgeSummaryIncluded", "printBridgeSummaryLastDevice",
    "printBridgeSummaryLastSeen", "printBridgeLimitExceededAlert", "printBridgeLimitReachedAlert", "printBridgeMessageHost",
    "printBridgeJobsPanel", "printBridgeRefreshJobsBtn"];
  const els = Object.fromEntries(ids.map((id) => [id, el(id)]));
  els.printBridgeAntiForgery = Object.assign(el("printBridgeAntiForgery"), { querySelector: () => ({ value: "antiforgery" }) });

  // Toggles as the browser would build them from the table markup; a re-render creates new elements.
  let toggleCache = { html: null, toggles: [] };
  function toggles() {
    const html = els.printBridgeDevicesPanel.innerHTML;
    if (toggleCache.html === html) return toggleCache.toggles;
    const list = [];
    for (const m of html.matchAll(/<tr data-device-id="([^"]+)">([\s\S]*?)<\/tr>/g)) {
      const listeners = {};
      const badge = Object.assign(el("badge-" + m[1]), { textContent: (m[2].match(/pb-device-active-badge">([^<]*)</) || [])[1] });
      const row = { querySelector: (s) => (s === ".pb-device-active-badge" ? badge : null) };
      const toggle = Object.assign(el("toggle-" + m[1]), {
        checked: /pb-device-active-toggle[^>]*checked/.test(m[2]),
        addEventListener: (n, fn) => { (listeners[n] ||= []).push(fn); },
        closest: () => row,
        change(value) { toggle.checked = value; (listeners.change || []).forEach((fn) => fn({})); },
        badge
      });
      toggle.setAttribute("data-device-id", m[1]);
      list.push(toggle);
    }
    toggleCache = { html, toggles: list };
    return list;
  }

  const docListeners = {};
  const winListeners = {};
  const document = {
    visibilityState: visibility,
    activeElement: null,
    documentElement: { lang: culture },
    getElementById: (id) => els[id] || null,
    querySelectorAll: (s) => (s === ".pb-device-active-toggle" ? toggles() : []),
    addEventListener: (n, fn) => { (docListeners[n] ||= []).push(fn); }
  };

  const requests = [];
  const pending = [];
  function fetch(url, init = {}) {
    const request = { url, method: init.method || "GET", body: init.body ? String(init.body) : "", cache: init.cache };
    requests.push(request);
    if (url === JOBS_URL) return Promise.resolve({ ok: true, text: () => Promise.resolve("<p>jobs</p>") });
    return new Promise((resolve, reject) => {
      pending.push({ request, resolve, reject });
    });
  }

  const window = {
    confirm: () => true,
    addEventListener: (n, fn) => { (winListeners[n] ||= []).push(fn); },
    WaslaPrintBridge: {
      messages: MESSAGES, devicesUrl: DEVICES_URL, devicesPollIntervalMs: 15000, setActiveUrlTemplate: "/print-bridge/devices/{id}/set-active",
      printJobsUrl: JOBS_URL, reprintUrlTemplate: "/print-bridge/print-jobs/{id}/reprint", printJobsPollIntervalMs: 3000,
      setupUrl: "/print-bridge/setup", displayCulture: culture, timeZoneId, initialState: initial
    }
  };

  vm.runInNewContext(source, {
    window, document, fetch, URLSearchParams, Intl, Date: FakeDate, AbortController,
    setTimeout: (fn, ms) => addTimer(fn, ms, false), clearTimeout: clearTimer,
    setInterval: (fn, ms) => addTimer(fn, ms, true), clearInterval: clearTimer,
    console
  });
  (docListeners.DOMContentLoaded || []).forEach((fn) => fn());

  const settle = async () => { for (let i = 0; i < 6; i++) await new Promise((r) => setImmediate(r)); };
  return {
    els, requests, clock, window,
    cfg: window.WaslaPrintBridge,
    deviceRequests: () => requests.filter((r) => r.url === DEVICES_URL),
    pendingCount: () => pending.length,
    /** Answers the oldest open device request. */
    async respond(body, { ok = true, status = 200 } = {}) {
      const next = pending.shift();
      if (!next) throw new Error("no open device request");
      next.resolve({ ok, status, json: () => (body instanceof Error ? Promise.reject(body) : Promise.resolve(body)) });
      await settle();
    },
    async fail() { const next = pending.shift(); next.reject(new TypeError("network down")); await settle(); },
    /** Moves the virtual clock forward, running timers in order. */
    async advance(ms) {
      const end = clock.now + ms;
      for (;;) {
        timers.sort((a, b) => a.at - b.at);
        const due = timers.find((t) => t.at <= end);
        if (!due) break;
        clock.now = due.at;
        if (due.repeat) due.at += due.ms; else clearTimer(due.id);
        due.fn();
        await settle();
      }
      clock.now = end;
      await settle();
    },
    async setVisibility(state) { document.visibilityState = state; (docListeners.visibilitychange || []).forEach((fn) => fn()); await settle(); },
    async pagehide() { (winListeners.pagehide || []).forEach((fn) => fn({})); await settle(); },
    serverNow: () => clock.now - clientOffsetMs,
    toggles,
    panel: () => els.printBridgeDevicesPanel.innerHTML,
    badge: () => (els.printBridgeDevicesPanel.innerHTML.match(/<span class="badge [^"]+">([^<]*)<\/span><span class="badge [^"]+ pb-device-active-badge">/) || [])[1],
    badges: () => [...els.printBridgeDevicesPanel.innerHTML.matchAll(/<span class="badge [^"]+">([^<]*)<\/span><span class="badge [^"]+ pb-device-active-badge">/g)].map((m) => m[1]),
    settle
  };
}

test("the initial state renders the rows and the summary cards in Turkey time and the UI culture", () => {
  const p = page();

  assert.match(p.panel(), /Kitchen/);
  assert.equal(p.badge(), "Online");
  assert.match(p.panel(), /Active/);
  assert.match(p.panel(), /1\.4\.0/);
  assert.match(p.panel(), /Computer: KITCHEN-PC/);
  assert.match(p.panel(), /Printer: POS-58/);
  assert.ok(p.panel().includes(expected("tr-TR", T0)), "last seen in tr-TR, Europe/Istanbul");
  assert.ok(p.panel().includes("1.10.2026 11:49:30"), "08:49:30Z is 11:49:30 in Türkiye");
  assert.doesNotMatch(p.panel(), /AM|PM|08:49/);
  assert.equal(p.els.printBridgeSummaryActive.textContent, "1");
  assert.equal(p.els.printBridgeSummaryIncluded.textContent, "3");
  assert.equal(p.els.printBridgeSummaryLastDevice.textContent, "Kitchen");
  assert.equal(p.els.printBridgeSummaryLastSeen.textContent, "Last seen: " + expected("tr-TR", T0));
  assert.deepEqual(p.deviceRequests(), [], "the initial state needs no request");
  assert.equal(typeof p.cfg.refreshDevices, "function", "one refresh entry point for a future push channel");
});

test("every 15 seconds the page reads a fresh snapshot, and a Bridge that started shows Online without a reload", async () => {
  const offline = device({ connectionStatus: "Disconnected", isConnected: false, lastSeenAtUtc: iso(T0 - 3_600_000) });
  const p = page({ initial: snapshot({ devices: [offline] }) });
  assert.equal(p.badge(), "Offline");

  await p.advance(14_999);
  assert.equal(p.deviceRequests().length, 0);
  await p.advance(1);
  assert.equal(p.deviceRequests().length, 1);
  assert.equal(p.deviceRequests()[0].cache, "no-store");

  await p.respond(snapshot({ serverMs: T0 + 20_000, devices: [device({ lastSeenAtUtc: iso(T0 + 18_000) })] }));
  assert.equal(p.badge(), "Online");
  assert.equal(p.els.printBridgeSummaryLastSeen.textContent, "Last seen: " + expected("tr-TR", T0 + 18_000));

  await p.advance(15_000);
  assert.equal(p.deviceRequests().length, 2);
});

test("a hidden tab skips its turn and refreshes as soon as it is visible again", async () => {
  const p = page({ visibility: "hidden" });

  await p.advance(45_000);
  assert.equal(p.deviceRequests().length, 0);

  await p.setVisibility("visible");
  assert.equal(p.deviceRequests().length, 1);
});

test("only one snapshot request runs at a time, and triggers meanwhile fold into a single follow-up", async () => {
  const p = page();

  p.cfg.refreshDevices();
  p.cfg.refreshDevices();
  await p.setVisibility("visible");
  await p.advance(15_000);
  assert.equal(p.deviceRequests().length, 1, "still one request in flight");

  await p.respond(snapshot({ serverMs: T0 + 16_000 }));
  assert.equal(p.deviceRequests().length, 2, "exactly one follow-up");
  await p.respond(snapshot({ serverMs: T0 + 16_500 }));
  assert.equal(p.pendingCount(), 0);
});

test("an older snapshot never replaces a newer one", async () => {
  const p = page();

  p.cfg.refreshDevices();
  await p.respond(snapshot({ serverMs: T0 + 40_000, devices: [device({ name: "Newer", lastSeenAtUtc: iso(T0 + 39_000) })] }));
  p.cfg.refreshDevices();
  await p.respond(snapshot({ serverMs: T0 + 30_000, devices: [device({ name: "Older", connectionStatus: "Disconnected", isConnected: false })] }));

  assert.match(p.panel(), /Newer/);
  assert.doesNotMatch(p.panel(), /Older/);
  assert.equal(p.badge(), "Online");
});

test("a failed refresh keeps the table quietly, and the next one recovers", async () => {
  const p = page();
  const writes = p.els.printBridgeDevicesPanel.writes;
  const table = p.panel();

  p.cfg.refreshDevices();
  await p.respond({}, { ok: false, status: 500 });
  p.cfg.refreshDevices();
  await p.fail();
  p.cfg.refreshDevices();
  await p.respond(new Error("login page instead of JSON"));

  assert.equal(p.panel(), table);
  assert.equal(p.els.printBridgeDevicesPanel.writes, writes);
  assert.equal(p.els.printBridgeMessageHost.innerHTML, "", "no toast for a background refresh");

  await p.advance(15_000);
  await p.respond(snapshot({ serverMs: T0 + 20_000, devices: [device({ name: "Recovered" })] }));
  assert.match(p.panel(), /Recovered/);
});

test("rows and every summary card change together, including the empty state", async () => {
  const p = page();

  p.cfg.refreshDevices();
  await p.respond(snapshot({
    serverMs: T0 + 30_000,
    devices: [
      device({ id: "dev-1", name: "Kitchen", isActive: false, connectionStatus: "Inactive", isConnected: false, lastSeenAtUtc: iso(T0) }),
      device({ id: "dev-2", name: "Bar", appVersion: "2.0.0", printerName: "EPSON", lastSeenAtUtc: iso(T0 + 29_000) })
    ]
  }));

  assert.match(p.panel(), /Bar/);
  assert.match(p.panel(), /2\.0\.0/);
  assert.match(p.panel(), /Printer: EPSON/);
  assert.match(p.panel(), /Passive/);
  assert.equal(p.els.printBridgeSummaryActive.textContent, "1");
  assert.equal(p.els.printBridgeSummaryLastDevice.textContent, "Bar");
  assert.equal(p.els.printBridgeSummaryLastSeen.textContent, "Last seen: " + expected("tr-TR", T0 + 29_000));

  p.cfg.refreshDevices();
  await p.respond(snapshot({ serverMs: T0 + 40_000, devices: [] }));
  assert.match(p.panel(), /No devices yet/);
  assert.equal(p.els.printBridgeSummaryActive.textContent, "0");
  assert.equal(p.els.printBridgeSummaryLastDevice.textContent, "None");
  assert.equal(p.els.printBridgeSummaryLastSeen.textContent, "Last seen: Never");
});

test("Online ends at the server's threshold by the server's clock, then the server is asked to confirm", async () => {
  // Last heartbeat at T0, snapshot taken 10 s later; this browser's clock runs 2 minutes behind the server's.
  const p = page({ initial: snapshot({ serverMs: T0 + 10_000 }), clientOffsetMs: -120_000 });
  assert.equal(p.badge(), "Online");

  for (let i = 0; i < 3; i++) {
    await p.advance(15_000);
    await p.respond(snapshot({ serverMs: p.serverNow() }));
  }
  assert.equal(p.serverNow(), T0 + 55_000);
  assert.equal(p.badge(), "Online", "55 s after the last heartbeat");
  assert.equal(p.deviceRequests().length, 3, "only the regular 15-second refreshes so far");

  await p.advance(5_000);
  assert.equal(p.badge(), "Online", "exactly 60 s still counts as connected, as on the server");
  assert.equal(p.deviceRequests().length, 3);

  await p.advance(400);
  assert.equal(p.badge(), "Offline", "just past 60 s, without a reload");
  assert.equal(p.deviceRequests().length, 4, "the expiry asks the server to confirm right away");
  await p.respond(snapshot({ serverMs: p.serverNow(), devices: [device({ connectionStatus: "RecentlySeen", isConnected: false })] }));
  assert.equal(p.badge(), "Offline");
});

test("the expiry follows the server clock, not the browser clock", async () => {
  // Browser 30 minutes ahead: with its own clock it would expire at once.
  const ahead = page({ initial: snapshot({ serverMs: T0 + 10_000 }), clientOffsetMs: 1_800_000 });
  await ahead.advance(49_000);
  assert.equal(ahead.badge(), "Online");
  await ahead.advance(1_500);
  assert.equal(ahead.badge(), "Offline");
});

test("missing, invalid or zone-less times show the placeholder and are never used to expire a device", async () => {
  const p = page({
    initial: snapshot({
      devices: [
        device({ id: "a", name: "NoTime", lastSeenAtUtc: null, connectionStatus: "NeverConnected", isConnected: false }),
        device({ id: "b", name: "Garbage", lastSeenAtUtc: "not-a-date" }),
        device({ id: "c", name: "Zoneless", lastSeenAtUtc: "2026-10-01T08:49:30" })
      ],
      quota: { latestLastSeenAtUtc: "2026-10-01T08:49:30", lastConnectedDeviceName: "Zoneless" }
    })
  });

  const cells = [...p.panel().matchAll(/<td class="text-muted small text-nowrap">([^<]*)<\/td>/g)].map((m) => m[1]);
  assert.deepEqual(cells, ["Never", "Never", "Never"]);
  assert.equal(p.els.printBridgeSummaryLastSeen.textContent, "Last seen: Never");
  assert.deepEqual(p.badges(), ["Offline", "Online", "Online"]);

  // Read as local time, the zone-less value would already be past the threshold; it must not drive the status.
  await p.advance(61_000);
  assert.deepEqual(p.badges(), ["Offline", "Online", "Online"], "the server's status stays until the next snapshot");
});

test("times use the supplied culture and the restaurant's time zone, whatever the browser's settings", () => {
  for (const culture of ["tr-TR", "en-US", "ar-SA", "ru-RU"]) {
    const p = page({ culture });
    assert.ok(p.panel().includes(expected(culture, T0)), culture);
  }
  const en = page({ culture: "en-US" });
  assert.ok(en.panel().includes("11:49:30 AM"), "Istanbul time, not UTC or the machine's zone");
  const ru = page({ culture: "ru-RU" });
  assert.ok(ru.panel().includes("01.10.2026, 11:49:30"));
});

test("times follow the configured zone, not the machine's (Türkiye has no daylight saving time)", () => {
  // In January most of Europe is on winter time while Türkiye stays at UTC+3.
  const winter = Date.parse("2026-01-15T08:49:30.000Z");
  const p = page({ initial: snapshot({ serverMs: winter + 5000, devices: [device({ lastSeenAtUtc: iso(winter) })] }) });
  assert.ok(p.panel().includes("15.01.2026 11:49:30"));

  const tokyo = page({ timeZoneId: "Asia/Tokyo" });
  assert.ok(tokyo.panel().includes("1.10.2026 17:49:30"), "the zone comes from the page configuration");
});


test("a toggle still posts once, applies its own snapshot, and a refresh during it waits", async () => {
  const p = page();
  const [toggle] = p.toggles();

  p.cfg.refreshDevices();
  toggle.change(false);
  assert.equal(toggle.badge.textContent, "Passive", "the badge follows the switch at once");
  const post = p.requests.find((r) => r.method === "POST");
  assert.equal(post.url, "/print-bridge/devices/dev-1/set-active");
  assert.match(post.body, /isActive=false/);
  assert.match(post.body, /__RequestVerificationToken=antiforgery/);

  // The refresh answers first with the state before the toggle: it must not undo the switch.
  await p.respond(snapshot({ serverMs: T0 + 6_000 }));
  assert.match(p.panel(), /pb-device-active-toggle[^>]*checked/, "nothing re-rendered yet");

  await p.respond(Object.assign(snapshot({ serverMs: T0 + 7_000, devices: [device({ isActive: false, connectionStatus: "Inactive", isConnected: false })] }), { success: true, isActive: false }));
  assert.equal(p.badge(), "Unknown", "an inactive device's connection badge");
  assert.match(p.panel(), /Passive/);
  assert.equal(p.deviceRequests().length, 2, "the refresh that waited runs once the toggle has landed");
  await p.respond(snapshot({ serverMs: T0 + 8_000, devices: [device({ isActive: false, connectionStatus: "Inactive", isConnected: false })] }));
  assert.match(p.panel(), /Passive/);
});

test("a failed toggle reverts the switch, shows the error and refreshes", async () => {
  const p = page();
  const [toggle] = p.toggles();

  toggle.change(false);
  await p.respond({ success: false, message: "Limit reached" }, { ok: false, status: 400 });

  assert.equal(toggle.checked, true);
  assert.equal(toggle.badge.textContent, "Active");
  assert.match(p.els.printBridgeMessageHost.innerHTML, /Limit reached/);
  assert.equal(p.deviceRequests().length, 1);
});

test("the 3-second print-job refresh keeps running alongside", async () => {
  const p = page();

  await p.advance(3_000);
  await p.advance(3_000);
  assert.equal(p.requests.filter((r) => r.url === JOBS_URL).length, 2);
  assert.equal(p.els.printBridgeJobsPanel.innerHTML, "<p>jobs</p>");
});

test("refreshing only reads: no device, token, setup session or print job is created", async () => {
  const p = page({ initial: snapshot({ serverMs: T0 + 10_000 }) });

  await p.advance(15_000);
  await p.respond(snapshot({ serverMs: T0 + 25_000 }));
  await p.setVisibility("visible");
  await p.respond(snapshot({ serverMs: T0 + 26_000 }));
  await p.advance(40_000);
  while (p.pendingCount() > 0) await p.respond(snapshot({ serverMs: T0 + 70_000, devices: [device({ connectionStatus: "RecentlySeen", isConnected: false })] }));

  assert.ok(p.requests.length > 0);
  for (const r of p.requests) {
    assert.equal(r.method, "GET", r.url);
    assert.ok(r.url === DEVICES_URL || r.url === JOBS_URL, r.url);
    assert.doesNotMatch(r.url, /setup|session|token|manual-device|regenerate|reprint|create/);
  }
});

test("leaving the page stops the device refresh", async () => {
  const p = page();

  await p.pagehide();
  await p.advance(120_000);
  p.cfg.refreshDevices();

  assert.equal(p.deviceRequests().length, 0);
});

test("an unchanged snapshot does not rewrite the table", async () => {
  const p = page();
  const writes = p.els.printBridgeDevicesPanel.writes;

  p.cfg.refreshDevices();
  await p.respond(snapshot({ serverMs: T0 + 20_000 }));

  assert.equal(p.els.printBridgeDevicesPanel.writes, writes);
});
