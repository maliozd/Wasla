const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// The device details page's "Regenerate token" result: the new token is shown once, masked, in a read-only field.
// Show/Hide only changes the field's type (never a second copy), Copy works while it stays masked, a later token starts
// masked again, and dismissing removes the token from the page. The page's inline script runs here against a small DOM.

const view = fs.readFileSync(
  path.join(__dirname, "..", "..", "..", "src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "DeviceDetails.cshtml"), "utf8");

function inlineScript() {
  const section = view.indexOf("@section Scripts {");
  const start = view.indexOf("<script>", section) + "<script>".length;
  const end = view.indexOf("</script>", start);
  assert.ok(section >= 0 && start > section && end > start, "the device details inline script was not found");
  const script = view.slice(start, end);
  assert.ok(script.includes("@Html.Raw(deviceDetailsConfigJson)"), "the script's only Razor expression changed");
  return script.replace("@Html.Raw(deviceDetailsConfigJson)", JSON.stringify({
    endpoints: { regenerate: "/print-bridge/devices/dev-1/regenerate-token", setActive: "/print-bridge/devices/dev-1/set-active", nextIsActive: false },
    messages: {
      confirmRegenerateToken: "Regenerate?", tokenRegenerateWarning: "The old token stops.", tokenRegenerateFailed: "Failed",
      tokenMasked: "Masked", tokenCopied: "Token copied", tokenCopyFailed: "Copy failed", copied: "Copied",
      showToken: "Show token", hideToken: "Hide token", deviceUpdateFailed: "Update failed"
    }
  }));
}

function element(id, extra = {}) {
  const listeners = {};
  const classes = new Set(extra.classes || []);
  const attributes = {};
  return Object.assign({
    id, type: extra.type || "", value: "", textContent: "", innerHTML: "", className: "", disabled: false,
    attributes,
    classList: {
      add: (c) => classes.add(c), remove: (c) => classes.delete(c), contains: (c) => classes.has(c),
      toggle: (c, force) => { const on = force === undefined ? !classes.has(c) : !!force; on ? classes.add(c) : classes.delete(c); return on; }
    },
    setAttribute: (name, value) => { attributes[name] = String(value); },
    getAttribute: (name) => attributes[name] ?? null,
    addEventListener: (type, handler) => { (listeners[type] = listeners[type] || []).push(handler); },
    click() { (listeners.click || []).forEach((h) => h({ preventDefault() {} })); },
    scrollIntoView() {},
    querySelector: () => null
  }, extra.props || {});
}

function page({ responses }) {
  const els = {};
  const add = (id, extra) => { els[id] = element(id, extra); };
  add("printBridgeDeviceAntiForgery", { props: { querySelector: () => ({ value: "antiforgery" }) } });
  add("printBridgeDeviceMessageHost");
  add("printBridgeDetailTokenBox", { classes: ["d-none"] });
  add("printBridgeDetailTokenValue", { type: "password" });
  ["printBridgeDetailTokenTitle", "printBridgeDetailTokenNotice", "printBridgeDetailTokenToggleText",
    "printBridgeDetailRegenerateTokenBtn", "printBridgeDetailCopyTokenBtn", "printBridgeDetailDismissTokenBtn",
    "printBridgeDetailTokenToggleBtn", "printBridgeDetailSetActiveBtn"].forEach((id) => add(id));
  add("printBridgeDetailTokenWarning", { classes: ["d-none"] });
  add("printBridgeDetailTokenToggleIcon", { props: { className: "bi bi-eye" } });

  const documentListeners = {};
  const clipboard = [];
  const posts = [];
  const queue = responses.slice();
  const sandbox = {
    document: {
      getElementById: (id) => els[id] || null,
      addEventListener: (type, handler) => { (documentListeners[type] = documentListeners[type] || []).push(handler); }
    },
    window: { confirm: () => true, location: { reload() {} } },
    navigator: { clipboard: { writeText: (text) => { clipboard.push(text); return Promise.resolve(); } } },
    fetch: (url) => { posts.push(url); const body = queue.shift(); return Promise.resolve({ ok: true, json: () => Promise.resolve(body) }); },
    URLSearchParams, setTimeout: (fn) => fn(), Promise
  };
  vm.runInNewContext(inlineScript(), sandbox);
  (documentListeners.DOMContentLoaded || []).forEach((h) => h());
  return { els, clipboard, posts };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));
const issued = (token) => ({ token, tokenTitle: "New token for Kitchen", tokenNotice: "Shown once.", tokenWarning: "", message: "Token regenerated." });

test("a regenerated token is shown masked in a read-only field, once", async () => {
  const { els, posts } = page({ responses: [issued("tok-first-0123456789")] });
  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();

  const value = els.printBridgeDetailTokenValue;
  assert.deepEqual(posts, ["/print-bridge/devices/dev-1/regenerate-token"]);
  assert.equal(els.printBridgeDetailTokenBox.classList.contains("d-none"), false);
  assert.equal(value.type, "password");
  assert.equal(value.value, "tok-first-0123456789");
  assert.equal(value.textContent, "", "the token is not written as visible text");
  assert.equal(els.printBridgeDetailTokenToggleText.textContent, "Show token");
  const attributeCopies = Object.values(els).flatMap((e) => Object.values(e.attributes)).filter((v) => v.includes("tok-first"));
  assert.equal(attributeCopies.length, 0, "the token is never placed in an attribute");
});

test("Show/Hide only changes the field type and never creates a second copy", async () => {
  const { els } = page({ responses: [issued("tok-first-0123456789")] });
  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();

  els.printBridgeDetailTokenToggleBtn.click();
  assert.equal(els.printBridgeDetailTokenValue.type, "text");
  assert.equal(els.printBridgeDetailTokenToggleText.textContent, "Hide token");
  assert.equal(els.printBridgeDetailTokenToggleIcon.className, "bi bi-eye-slash");
  const copies = Object.values(els).filter((e) => e.value === "tok-first-0123456789" || e.textContent.includes("tok-first") || e.innerHTML.includes("tok-first"));
  assert.deepEqual(copies.map((e) => e.id), ["printBridgeDetailTokenValue"]);

  els.printBridgeDetailTokenToggleBtn.click();
  assert.equal(els.printBridgeDetailTokenValue.type, "password");
  assert.equal(els.printBridgeDetailTokenToggleText.textContent, "Show token");
});

test("Copy copies the exact token while the field stays masked", async () => {
  const { els, clipboard } = page({ responses: [issued("tok-first-0123456789")] });
  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();

  els.printBridgeDetailCopyTokenBtn.click();
  await flush();
  assert.deepEqual(clipboard, ["tok-first-0123456789"]);
  assert.equal(els.printBridgeDetailTokenValue.type, "password");
});

test("a later token starts masked even if the previous one was shown", async () => {
  const { els } = page({ responses: [issued("tok-first-0123456789"), issued("tok-second-9876543210")] });
  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();
  els.printBridgeDetailTokenToggleBtn.click();
  assert.equal(els.printBridgeDetailTokenValue.type, "text");

  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();
  assert.equal(els.printBridgeDetailTokenValue.type, "password");
  assert.equal(els.printBridgeDetailTokenValue.value, "tok-second-9876543210");
  assert.equal(els.printBridgeDetailTokenToggleText.textContent, "Show token");
});

test("dismissing removes the token from the page, not only from view", async () => {
  const { els, clipboard } = page({ responses: [issued("tok-first-0123456789")] });
  els.printBridgeDetailRegenerateTokenBtn.click();
  await flush();
  els.printBridgeDetailTokenToggleBtn.click();

  els.printBridgeDetailDismissTokenBtn.click();
  assert.equal(els.printBridgeDetailTokenBox.classList.contains("d-none"), true);
  assert.equal(els.printBridgeDetailTokenValue.value, "");
  assert.equal(els.printBridgeDetailTokenValue.type, "password");
  const leftovers = Object.values(els).filter((e) => [e.value, e.textContent, e.innerHTML].some((v) => String(v).includes("tok-first")));
  assert.deepEqual(leftovers.map((e) => e.id), [], "no element keeps the token after dismissing");

  els.printBridgeDetailCopyTokenBtn.click();
  await flush();
  assert.deepEqual(clipboard, [], "nothing is left to copy after dismissing");
});
