// Shared test harness (not a test file): one Print Bridge setup page with the real print-bridge-setup.js and the real
// wasla-guided-setup.js panel logic, a fake DOM, a fake network and manual timers.
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "..", "..", "..", "src", "Wasla.Web", "wwwroot", "js");
const setupSource = fs.readFileSync(path.join(root, "print-bridge", "print-bridge-setup.js"), "utf8");
const guided = require(path.join(root, "wasla-guided-setup.js"));

const WEB_PANEL_URL = "https://tenant.wasla.local/";
const TOKEN = "raw-device-token-shown-once";
const NEW_DEVICE_URL = "/print-bridge/setup/manual-device";
const REGENERATE_URL = "/print-bridge/devices/dev-2/regenerate-token";
const READINESS_URL = "/guided-setup/section-status?section=print-bridge";
const SESSION_URL = "/print-bridge/setup/session";
const STATUS_URL = "/print-bridge/setup/session/s1/status";
const DOWNLOAD_URL = "/print-bridge/download/package";
const PROTOCOL_URL = "wasla-printbridge://setup?server=x&code=hidden";
const WINDOWS = { platform: "Win32", userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" };

const MESSAGES = {
  copied: "Copied",
  copyFailed: "Copy failed",
  manualTokenFailed: "The token action could not be completed.",
  regenerateConfirm: "Regenerate token for {0}? The old token will stop working.",
  createNewDeviceToken: "Create a device token",
  createReconnectToken: "Create a new token for the selected device",
  tokenCreatedFor: "Device token created for {0}.",
  oldTokenInvalidAfterRegenerate: "The old token is no longer valid.",
  webPanelUrlCopied: "Wasla Web Panel URL copied.",
  deviceTokenCopied: "Device token copied.",
  showToken: "Show token",
  hideToken: "Hide token",
  manualNotConnectedYet: "Creating or copying the token doesn't connect Print Bridge.",
  manualVerified: "Wasla verified the Print Bridge connection.",
  manualStillWaiting: "Wasla hasn't verified the connection yet.",
  downloadStarted: "Your download should start now.",
  connected: "Print Bridge connected successfully.",
  savedUnverified: "Saved, not verified.",
  sessionError: "Could not start.",
  openButton: "Open and connect Wasla Print Bridge",
  waiting: "Waiting…",
  starting: "Opening…"
};

class FakeEvent {
  constructor(type, init) { this.type = type; this.detail = init && init.detail; }
}

class FakeFormData {
  constructor() { this.entries = []; }
  append(name, value) { this.entries.push([name, String(value)]); }
  toString() { return this.entries.map(([n, v]) => n + "=" + v).join("&"); }
}

function element(id, focused, init = {}) {
  const listeners = {};
  const classes = new Set(init.classes || []);
  const attributes = {};
  const el = {
    id,
    value: init.value || "",
    textContent: "",
    innerHTML: init.innerHTML || "",
    href: init.href || "",
    disabled: false,
    checked: !!init.checked,
    hidden: !!init.hidden,
    attributes,
    get className() { return [...classes].join(" "); },
    set className(v) { classes.clear(); String(v).split(/\s+/).filter(Boolean).forEach((c) => classes.add(c)); },
    classList: {
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
      toggle: (c, on) => ((on === undefined ? !classes.has(c) : on) ? classes.add(c) : classes.delete(c)),
      contains: (c) => classes.has(c)
    },
    addEventListener: (name, fn) => { (listeners[name] ||= []).push(fn); },
    /** Fires the listeners; returns whether one of them prevented the default action. */
    fire: (name) => {
      let prevented = false;
      (listeners[name] || []).forEach((fn) => fn({ preventDefault() { prevented = true; } }));
      return prevented;
    },
    click() { return el.fire("click"); },
    focus() { focused.push(id); },
    scrollIntoView() {},
    querySelector: (selector) => (init.children && init.children[selector]) || null,
    setAttribute: (n, v) => { attributes[n] = String(v); },
    getAttribute: (n) => (n in attributes ? attributes[n] : null)
  };
  return el;
}

/** The guided Print Bridge panel exactly as the partial renders it (unconnected). */
function guidedPanel() {
  const attributes = {
    "data-guided-setup-panel": "print-bridge",
    "data-guided-setup-state": "not-ready",
    "data-guided-setup-status-url": READINESS_URL,
    "data-guided-setup-refresh-on": "wasla:print-bridge-setup-completed",
    "data-guided-setup-watch-on": "wasla:print-bridge-manual-setup-started"
  };
  const parts = [
    { when: "not-ready", hidden: false, label: "set-up-later" },
    { when: "ready", hidden: true, label: "get-to-know-your-device" }
  ].map((p) => Object.assign(p, { getAttribute: (n) => (n === "data-guided-setup-when" ? p.when : null) }));
  return {
    parts,
    getAttribute: (n) => (n in attributes ? attributes[n] : null),
    setAttribute: (n, v) => { attributes[n] = String(v); },
    classList: { toggle() {} },
    querySelectorAll: () => parts
  };
}

/**
 * One Print Bridge setup page. <guidedFirstInstall> renders it as the server does for a user whose guided setup is at
 * Print Bridge: the linear first install, no setup-type choice and no reconnect, and the manual section hidden.
 */
function page({
  readiness = [], statuses = [], issue = null, confirm = () => true, devices = [], canManageDevices = true,
  guidedFirstInstall = false, packageAvailable = true, navigator = WINDOWS, session = null, hash = ""
} = {}) {
  const focused = [];
  const ids = [
    "printBridgeSetupMessageHost", "pbSetupServerUrl", "pbSetupCopyServerUrlBtn", "pbManualTokenActionBtn", "pbManualTokenActionText",
    "pbManualTokenResult", "pbManualTokenTitle", "pbManualTokenWarning", "pbManualTokenValue", "pbManualTokenToggleBtn",
    "pbManualTokenToggleText", "pbManualTokenToggleIcon", "pbManualCopyTokenBtn",
    "pbManualConnectionStatus", "pbManualCopyStatus", "pbAutoOpenBtn", "pbAutoOpenBtnText", "pbAutoRetryBtn",
    "pbAutoStatus", "pbAutoStatusText", "pbAutoSpinner", "pbAutoFallback", "pbManualSetupCollapse", "pbAutoUseManualLink"
  ];
  const firstInstallIds = [
    "pbStepDownload", "pbStepDownloadTitle", "pbNotWindowsNotice", "pbAlreadyInstalledBtn", "pbDownloadStatus",
    "pbStepPrepare", "pbStepPrepareTitle", "pbPreparedBtn", "pbStepConnect", "pbStepConnectTitle", "pbChooseManualBtn",
    "pbStepPrinter"
  ];
  if (guidedFirstInstall) {
    ids.push(...firstInstallIds);
    ids.push(packageAvailable ? "pbDownloadBtn" : "pbPackageMissingNotice");
  } else {
    // Only the ordinary page has the setup-type choice, the reconnect picker and the "open manual setup" link.
    ids.push("pbReconnectDeviceSection", "pbReconnectDeviceSelect", "pbReconnectDeviceGuidance", "pbOpenManualSetupLink");
  }
  // The server renders the device page link only for users that page's policy admits.
  if (canManageDevices) ids.push("pbManualManageDeviceLink");
  const hiddenClass = new Set(["pbManualTokenResult", "pbManualTokenWarning", "pbManualManageDeviceLink", "pbAutoStatus", "pbAutoFallback", "pbReconnectDeviceSection", "pbNotWindowsNotice"]);
  const hiddenAttribute = new Set(["pbStepPrepare", "pbStepConnect", "pbStepPrinter"]);
  const els = Object.fromEntries(ids.map((id) => [id, element(id, focused, {
    classes: hiddenClass.has(id) ? ["d-none"] : [],
    hidden: hiddenAttribute.has(id)
  })]));
  els.pbSetupServerUrl.value = WEB_PANEL_URL;
  els.pbManualCopyTokenBtn.className = "btn btn-outline-secondary";
  els.pbManualTokenValue.type = "password";
  els.pbManualTokenToggleText.textContent = MESSAGES.showToken;
  els.pbManualTokenToggleIcon.className = "bi bi-eye";
  if (els.pbDownloadBtn) els.pbDownloadBtn.href = DOWNLOAD_URL;
  if (els.pbOpenManualSetupLink) els.pbOpenManualSetupLink.href = "#pbManualSetup";
  els.pbAutoUseManualLink.href = "#pbManualSetup";
  const manualHeader = element("pbManualSetupHeader", focused);
  els.pbManualSetup = element("pbManualSetup", focused, { hidden: guidedFirstInstall, children: { ".accordion-button": manualHeader } });
  els.printBridgeSetupConfig = {
    textContent: JSON.stringify({
      serverUrl: WEB_PANEL_URL,
      sessionCreateUrl: SESSION_URL,
      manualDeviceCreateUrl: NEW_DEVICE_URL,
      guidedFirstInstall,
      regenerateTokenUrlTemplate: "/print-bridge/devices/{id}/regenerate-token",
      devices,
      messages: MESSAGES
    })
  };
  const radios = guidedFirstInstall ? {} : {
    new: element("pbSetupModeNew", focused, { checked: true }),
    reconnect: element("pbSetupModeReconnect", focused)
  };
  if (radios.new) { radios.new.value = "new"; radios.reconnect.value = "reconnect"; }

  const panel = guidedPanel();
  const listeners = {};
  const events = [];
  const doc = {
    readyState: "loading",
    getElementById: (id) => els[id] || null,
    querySelector: (selector) => {
      if (selector === 'input[name="__RequestVerificationToken"]') return { value: "antiforgery" };
      if (selector === 'input[name="pbSetupMode"]:checked') return Object.values(radios).find((r) => r.checked) || null;
      return null;
    },
    querySelectorAll: (selector) => {
      if (selector.includes("data-guided-setup-panel")) return [panel];
      if (selector === 'input[name="pbSetupMode"]') return Object.values(radios);
      if (selector === 'a[href="#pbManualSetup"]') return [els.pbOpenManualSetupLink, els.pbAutoUseManualLink].filter(Boolean);
      return [];
    },
    addEventListener: (name, fn) => { (listeners[name] ||= []).push(fn); },
    dispatchEvent: (event) => {
      events.push({ type: event.type, detail: event.detail });
      (listeners[event.type] || []).forEach((fn) => fn(event));
      return true;
    }
  };

  const requests = [];
  const reply = (ok, body, status = ok ? 200 : 400) => Promise.resolve({ ok, status, json: () => Promise.resolve(body) });
  const fetch = (url, init = {}) => {
    requests.push({ url, method: init.method || "GET", headers: init.headers || {}, body: init.body ? String(init.body) : "" });
    if (url === NEW_DEVICE_URL || url === REGENERATE_URL) {
      const result = issue ? issue(url) : { ok: true, body: { success: true, deviceId: "dev-1", deviceName: "Print Bridge", token: TOKEN, detailsUrl: "/print-bridge/devices/dev-1" } };
      return reply(result.ok, result.body);
    }
    if (url === READINESS_URL) {
      const next = readiness.length ? readiness.shift() : { current: true, ready: false };
      return next instanceof Error ? Promise.reject(next) : reply(true, next);
    }
    if (url === SESSION_URL) {
      const result = session ? session() : { ok: true, body: { success: true, protocolUrl: PROTOCOL_URL, statusUrl: STATUS_URL } };
      return reply(result.ok, result.body);
    }
    if (url === STATUS_URL) return reply(true, statuses.length ? statuses.shift() : { success: true, status: "Pending" });
    throw new Error("unexpected request " + url);
  };

  const timers = new Map();
  let nextTimer = 1;
  const pageTimers = [];
  const clipboard = [];
  const confirms = [];
  const win = {
    location: { hash, href: "" },
    confirm: (text) => { confirms.push(text); return confirm(text); },
    fetch,
    navigator,
    CustomEvent: FakeEvent,
    setTimeout: (fn) => { const id = nextTimer++; timers.set(id, fn); return id; },
    clearTimeout: (id) => timers.delete(id)
  };
  const intervals = new Map();

  vm.runInNewContext(setupSource, {
    window: win, document: doc, fetch, CustomEvent: FakeEvent, URLSearchParams, FormData: FakeFormData,
    setInterval: (fn) => { const id = nextTimer++; intervals.set(id, fn); return id; },
    clearInterval: (id) => intervals.delete(id),
    setTimeout: (fn, ms) => { pageTimers.push({ fn, ms }); return pageTimers.length; },
    clearTimeout: () => {},
    navigator: { clipboard: { writeText: (text) => { clipboard.push(text); return Promise.resolve(); } } }
  });
  guided.watchPanels(doc, win);
  (listeners.DOMContentLoaded || []).forEach((fn) => fn());

  const settle = async () => { for (let i = 0; i < 4; i++) await new Promise((resolve) => setImmediate(resolve)); };
  return {
    els, panel, manualHeader, requests, events, win, clipboard, confirms, focused, radios,
    async issueToken() { els.pbManualTokenActionBtn.click(); await settle(); },
    /** Runs every pending guided-panel timer (one readiness read each). */
    async tick() { const fns = [...timers.values()]; timers.clear(); for (const fn of fns) fn(); await settle(); },
    async tickIntervals() { for (const fn of [...intervals.values()]) fn(); await settle(); },
    /** Runs the setup script's own timeouts (e.g. the automatic flow's "didn't open?" fallback after 6 s). */
    async runPageTimers(ms) { for (const t of pageTimers.splice(0).filter((t) => ms === undefined || t.ms === ms)) t.fn(); await settle(); },
    async raise(type) { doc.dispatchEvent(new FakeEvent(type)); await settle(); },
    async chooseReconnect(deviceId) {
      radios.new.checked = false;
      radios.reconnect.checked = true;
      els.pbReconnectDeviceSelect.value = deviceId || "";
      radios.reconnect.fire("change");
      await settle();
    },
    pendingTimers: () => timers.size,
    readinessReads: () => requests.filter((r) => r.url === READINESS_URL).length,
    panelState: () => panel.getAttribute("data-guided-setup-state"),
    visible: () => panel.parts.filter((p) => !p.hidden).map((p) => p.label),
    status: () => els.pbManualConnectionStatus.textContent,
    message: () => els.printBridgeSetupMessageHost.innerHTML,
    settle
  };
}

module.exports = {
  guided, page, FakeEvent, MESSAGES, WINDOWS,
  WEB_PANEL_URL, TOKEN, NEW_DEVICE_URL, REGENERATE_URL, READINESS_URL, SESSION_URL, STATUS_URL, DOWNLOAD_URL, PROTOCOL_URL
};
