const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const status = require("../../../src/Wasla.Web/wwwroot/js/orders/orders-platform-sync-status.js");

// Localized copy comes from the page's messages; the test uses markers so it never depends on a culture.
const MESSAGES = {
  syncStatusLive: "[live]",
  syncStatusNotConnected: "[not connected]",
  syncStatusSyncDisabled: "[sync off]",
  syncStatusIssue: "[issue]",
  syncStatusIssueWithPlatforms: "[issue: {0}]",
  platformYemeksepeti: "[Yemeksepeti]",
  platformGetirYemek: "[Getir]",
  platformTrendyolYemek: "[Trendyol]"
};
// As O.getMessage: an unknown key falls back to the generic error text.
const message = (key) => MESSAGES[key] || "[generic error]";
const OPTIONS = { culture: "en-US", timeZone: "Europe/Istanbul" };
const SERVER_NOW = "2026-10-13T09:30:00Z";

function element(id, className) {
  const classes = new Set(className ? className.split(" ") : []);
  const attributes = new Map();
  const el = {
    id: id,
    textContent: "",
    hidden: false,
    children: {},
    classList: {
      add: (...names) => names.forEach((name) => classes.add(name)),
      remove: (...names) => names.forEach((name) => classes.delete(name)),
      contains: (name) => classes.has(name)
    },
    get className() { return [...classes].join(" "); },
    set className(value) { classes.clear(); String(value).split(" ").filter(Boolean).forEach((name) => classes.add(name)); },
    classes: classes,
    setAttribute: (name, value) => attributes.set(name, String(value)),
    getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null),
    removeAttribute: (name) => attributes.delete(name),
    querySelector: (selector) => el.children[selector] || null
  };
  return el;
}

/** The header as LiveDisplay.cshtml renders it: a pending badge saying "Loading…" and a hidden last-sync item. */
function header() {
  const badge = element("ordersLiveDisplaySyncStatus", "wasla-live-display-status wasla-live-display-status--pending");
  badge.setAttribute("data-sync-state", "pending");
  badge.setAttribute("role", "status");
  const dot = element(null, "wasla-live-display-status__dot");
  const icon = element(null, "wasla-live-display-status__icon bi");
  icon.hidden = true;
  const text = element(null, "");
  text.textContent = "[loading]";
  badge.children[".wasla-live-display-status__dot"] = dot;
  badge.children[".wasla-live-display-status__icon"] = icon;
  badge.children["[data-sync-status-text]"] = text;

  const item = element("ordersLiveDisplayLastSyncItem", "wasla-live-display-last-sync");
  item.hidden = true;
  const time = element("ordersLiveDisplayLastSync", "");
  const els = { ordersLiveDisplaySyncStatus: badge, ordersLiveDisplayLastSyncItem: item, ordersLiveDisplayLastSync: time };
  const listeners = {};
  return {
    els: els,
    badge: badge,
    dot: dot,
    icon: icon,
    text: text,
    item: item,
    time: time,
    getElementById: (id) => els[id] || null,
    addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
    dispatch: (type, detail) => (listeners[type] || []).forEach((fn) => fn({ detail: detail }))
  };
}

const MODIFIERS = ["wasla-live-display-status--pending", "wasla-live-display-status--issue", "wasla-live-display-status--off"];
function modifiers(el) {
  return MODIFIERS.filter((name) => el.classes.has(name));
}

function apply(doc, sync) {
  return status.apply(doc, sync, SERVER_NOW, message, OPTIONS);
}

test("Live only when the server says so: the green look with the dot, and its own words", () => {
  const doc = header();
  assert.equal(apply(doc, { state: "Live", lastSuccessfulSyncUtc: "2026-10-13T09:29:20Z", platforms: [{ platform: "Yemeksepeti", state: "Live" }] }), true);

  assert.equal(doc.text.textContent, "[live]");
  assert.equal(doc.badge.getAttribute("data-sync-state"), "live");
  assert.deepEqual(modifiers(doc.badge), [], "the existing Live look");
  assert.equal(doc.dot.hidden, false);
  assert.equal(doc.icon.hidden, true);
  assert.equal(doc.item.hidden, false);
  assert.equal(doc.time.getAttribute("datetime"), "2026-10-13T09:29:20Z");
  assert.equal(doc.time.textContent, status.formatSyncTime("2026-10-13T09:29:20Z", SERVER_NOW, "en-US", "Europe/Istanbul"));
  assert.match(doc.time.textContent, /12:29/, "the time in the restaurant's time zone");
});

test("not connected and sync off: muted, each with its own words and icon, never Live", () => {
  const cases = [
    [{ state: "NotConnected", lastSuccessfulSyncUtc: null, platforms: [] }, "[not connected]", "not-connected", "bi-plug"],
    [{ state: "SyncDisabled", lastSuccessfulSyncUtc: null, platforms: [{ platform: "Yemeksepeti", state: "SyncDisabled" }] }, "[sync off]", "sync-disabled", "bi-pause-circle"]
  ];
  for (const [sync, text, key, icon] of cases) {
    const doc = header();
    apply(doc, sync);
    assert.equal(doc.text.textContent, text, key);
    assert.equal(doc.badge.getAttribute("data-sync-state"), key);
    assert.deepEqual(modifiers(doc.badge), ["wasla-live-display-status--off"], key);
    assert.equal(doc.icon.hidden, false, key);
    assert.equal(doc.icon.classes.has(icon), true, key);
    assert.equal(doc.dot.hidden, true, key);
    assert.equal(doc.item.hidden, true, "no last successful sync to show");
  }
});

test("mixed health: an issue that names only the affected platforms, never an overall Live", () => {
  const doc = header();
  apply(doc, {
    state: "Issue",
    lastSuccessfulSyncUtc: "2026-10-13T06:30:00Z",
    platforms: [
      { platform: "Yemeksepeti", state: "Live" },
      { platform: "TrendyolYemek", state: "Issue" },
      { platform: "GetirYemek", state: "SyncDisabled" }
    ]
  });

  assert.equal(doc.text.textContent, "[issue: [Trendyol]]");
  assert.equal(doc.badge.getAttribute("data-sync-state"), "issue");
  assert.deepEqual(modifiers(doc.badge), ["wasla-live-display-status--issue"]);
  assert.equal(doc.icon.classes.has("bi-exclamation-triangle-fill"), true);
  assert.equal(doc.dot.hidden, true);
  assert.equal(doc.item.hidden, false, "the oldest last successful sync is still shown");

  apply(doc, { state: "Issue", lastSuccessfulSyncUtc: null, platforms: [{ platform: "TrendyolYemek", state: "Issue" }, { platform: "Yemeksepeti", state: "Issue" }] });
  assert.equal(doc.text.textContent, "[issue: [Trendyol], [Yemeksepeti]]");
  assert.equal(doc.item.hidden, true, "unknown while a platform has never synced");
});

test("an issue without a platform the page has words for falls back to the plain issue text", () => {
  const doc = header();
  apply(doc, { state: "Issue", lastSuccessfulSyncUtc: null, platforms: [{ platform: "SomeFuturePlatform", state: "Issue" }] });
  assert.equal(doc.text.textContent, "[issue]");
  assert.equal(doc.text.textContent.includes("[generic error]"), false);
});

test("a snapshot without a usable sync section is an issue, not a stale or invented Live", () => {
  for (const sync of [null, undefined, "Live", { state: "Paused" }, { state: 2 }]) {
    const doc = header();
    apply(doc, { state: "Live", lastSuccessfulSyncUtc: "2026-10-13T09:29:50Z", platforms: [] });
    apply(doc, sync);
    assert.equal(doc.text.textContent, "[issue]", JSON.stringify(sync));
    assert.equal(doc.badge.getAttribute("data-sync-state"), "issue");
  }
});

test("a failed poll turns Live into an issue; the next rendered snapshot shows the server's state again", () => {
  const doc = header();
  status.boot(doc, { getMessage: message, opts: { displayCulture: "en-US", timeZoneId: "Europe/Istanbul" } });
  const live = { state: "Live", lastSuccessfulSyncUtc: "2026-10-13T09:29:20Z", platforms: [{ platform: "Yemeksepeti", state: "Live" }] };

  doc.dispatch("wasla:live-rendered", { orders: [], training: null, automation: null, sync: live, serverTimeUtc: SERVER_NOW });
  assert.equal(doc.text.textContent, "[live]");

  for (const kind of ["stale", "session"]) {
    doc.dispatch("wasla:live-poll-status", { kind: kind });
    assert.equal(doc.text.textContent, "[issue]", kind);
    assert.equal(doc.badge.getAttribute("data-sync-state"), "issue", kind);
    assert.equal(doc.item.hidden, false, "the last known successful sync stays visible");
  }

  doc.dispatch("wasla:live-poll-status", { kind: "ok" });
  assert.equal(doc.text.textContent, "[issue]", "a successful request alone is no evidence");
  doc.dispatch("wasla:live-rendered", { orders: [], sync: live, serverTimeUtc: SERVER_NOW });
  assert.equal(doc.text.textContent, "[live]");
});

test("an unchanged state is not rewritten, so the polite status region announces a change once", () => {
  const doc = header();
  const sync = { state: "Issue", lastSuccessfulSyncUtc: null, platforms: [{ platform: "TrendyolYemek", state: "Issue" }] };
  apply(doc, sync);
  let writes = 0;
  let value = doc.text.textContent;
  Object.defineProperty(doc.text, "textContent", { get: () => value, set: (v) => { writes += 1; value = v; } });
  apply(doc, sync);
  apply(doc, sync);
  assert.equal(writes, 0);
});

test("a sync on an earlier day shows its date too", () => {
  const sameDay = status.formatSyncTime("2026-10-13T05:00:00Z", SERVER_NOW, "en-US", "Europe/Istanbul");
  const earlier = status.formatSyncTime("2026-10-12T05:00:00Z", SERVER_NOW, "en-US", "Europe/Istanbul");
  assert.equal(/2026|26/.test(sameDay), false, sameDay);
  assert.match(earlier, /10\/12\/26|12\/10\/26|2026/, earlier);
  assert.equal(status.formatSyncTime("not a date", SERVER_NOW, "en-US", "Europe/Istanbul"), "");
});

test("the script only displays the server's answer: no request, no realtime transport, no page inference, no copy", () => {
  const source = fs.readFileSync(path.join(__dirname, "../../../src/Wasla.Web/wwwroot/js/orders/orders-platform-sync-status.js"), "utf8");
  for (const forbidden of ["fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "signalR", "HubConnection", "localStorage", "querySelectorAll", "Date.now"])
    assert.equal(source.includes(forbidden), false, forbidden);
  for (const copy of ["Canlı", "Connection issue", "Bağlantı", "Sync off", "Not connected", "مباشر", "В эфире"])
    assert.equal(source.includes(copy), false, copy);
});
