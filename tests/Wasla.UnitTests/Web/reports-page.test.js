const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const root = path.join(__dirname, "..", "..", "..");
const reports = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "reports", "reports-page.js"));

function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function response(status, body, redirected) {
  return { status, ok: status >= 200 && status < 300, redirected: !!redirected, text: async () => body || "" };
}

function harness(fetchImpl) {
  const events = [];
  const loader = reports.createLoader({
    fetch: fetchImpl,
    onStart: () => events.push("start"),
    onResult: (html, status) => events.push(`result:${status}:${html}`),
    onFailure: () => events.push("failure"),
    onSignedOut: () => events.push("signed-out")
  });
  return { loader, events };
}

test("the empty query is the page default", () => {
  assert.deepEqual(reports.readState(""), { preset: "last30days", startDate: "", endDate: "", platform: "", compare: false, section: "overview" });
  assert.equal(reports.toQuery(reports.readState("")), "");
  assert.equal(reports.isDefaultFilter(reports.readState("?section=platforms")), true);
  assert.equal(reports.isDefaultFilter(reports.readState("?compare=1")), false);
});

test("URLs round-trip in the order the server writes them", () => {
  for (const query of [
    "?preset=yesterday&platform=TrendyolYemek",
    "?preset=custom&startDate=2026-10-01&endDate=2026-10-05&compare=1",
    "?preset=last7days&section=platforms"
  ]) {
    assert.equal(reports.toQuery(reports.readState(query)), query);
  }
  // Dates without a preset mean custom on the server too, and the repeated value still refuses the query.
  assert.equal(
    reports.toQuery(reports.readState("?startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05")),
    "?preset=custom&startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05"
  );
});

test("dates without a preset are a custom range, and a preset choice that clears them writes none", () => {
  assert.equal(reports.readState("?startDate=2026-10-01").preset, "custom");
  assert.equal(reports.toQuery({ preset: "today", startDate: "", endDate: "", platform: "", compare: false, section: "overview" }), "?preset=today");
});

test("a custom preset keeps both dates in any letter case", () => {
  for (const preset of ["CUSTOM", "Custom", "cUsToM"]) {
    const query = `?preset=${preset}&startDate=2026-10-01&endDate=2026-10-05`;
    const state = Object.assign(reports.readState(query), { section: "platforms" });
    assert.equal(reports.toQuery(state), `${query}&section=platforms`);
  }
});

test("the default preset in another letter case is still the default", () => {
  const state = reports.readState("?preset=LAST30DAYS");
  assert.equal(reports.isDefaultFilter(state), true);
  assert.equal(reports.toQuery(state), "");
});

test("a refused query is written back as submitted, so a section change cannot make it valid", () => {
  for (const query of [
    "?platform=1&platform=2",
    "?preset=today&preset=yesterday",
    "?preset=custom&startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05",
    "?preset=today&startDate=2026-10-01"
  ]) {
    assert.equal(reports.toQuery(reports.readState(query)), query);
  }
  // Dates without a preset mean custom on the server too, and the repeated value still refuses the query.
  assert.equal(
    reports.toQuery(reports.readState("?startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05")),
    "?preset=custom&startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05"
  );
});

test("choosing a filter replaces only that filter's repeated values", () => {
  const state = reports.readState("?preset=today&platform=1&platform=2");
  assert.equal(reports.toQuery(reports.withChanges(state, { platform: "GetirYemek" })), "?preset=today&platform=GetirYemek");
  assert.equal(reports.toQuery(reports.withChanges(state, { section: "platforms" })), "?preset=today&platform=1&platform=2&section=platforms");
});

test("a newer load drops the older answer, even when the older one arrives last", async () => {
  const first = deferred();
  const second = deferred();
  const queue = [first, second];
  const signals = [];
  const { loader, events } = harness((url, init) => { signals.push(init.signal); return queue.shift().promise; });

  const older = loader.load("/a");
  const newer = loader.load("/b");
  assert.equal(signals[0].aborted, true);

  second.resolve(response(200, "new"));
  assert.equal(await newer, "ok");
  first.resolve(response(200, "old"));
  assert.equal(await older, "stale");
  assert.deepEqual(events, ["start", "start", "result:200:new"]);
});

test("an aborted older request is not reported as a failure", async () => {
  const first = deferred();
  const second = deferred();
  const queue = [first, second];
  const { loader, events } = harness(() => queue.shift().promise);

  const older = loader.load("/a");
  const newer = loader.load("/b");
  first.reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
  assert.equal(await older, "stale");
  second.resolve(response(200, "new"));
  await newer;
  assert.ok(!events.includes("failure"));
});

test("a refused filter renders its answer, and a server error is a failure", async () => {
  let next = response(400, "invalid");
  const { loader, events } = harness(async () => next);

  assert.equal(await loader.load("/a"), "invalid");
  next = response(500);
  assert.equal(await loader.load("/a"), "failed");
  assert.deepEqual(events, ["start", "result:400:invalid", "start", "failure"]);
});

test("a network error is a failure and a timeout is a failure", async () => {
  const net = harness(async () => { throw new TypeError("Failed to fetch"); });
  assert.equal(await net.loader.load("/a"), "failed");

  const events = [];
  const slow = reports.createLoader({
    fetch: (url, init) => new Promise((_, reject) => init.signal.addEventListener("abort", () => reject(Object.assign(new Error("x"), { name: "AbortError" })))),
    timeoutMs: 5,
    onStart: () => {},
    onResult: () => events.push("result"),
    onFailure: () => events.push("failure"),
    onSignedOut: () => {}
  });
  assert.equal(await slow.load("/a"), "failed");
  assert.deepEqual(events, ["failure"]);
});

test("an expired session is sent to sign in rather than shown as a failure", async () => {
  for (const answer of [response(200, "<html>", true), response(401), response(403)]) {
    const { loader, events } = harness(async () => answer);
    assert.equal(await loader.load("/a"), "signed-out");
    assert.deepEqual(events, ["start", "signed-out"]);
  }
});

test("arrow keys follow the screen, so right-to-left swaps them", () => {
  assert.equal(reports.nextColumnIndex("ArrowLeft", 5, 10, false), 4);
  assert.equal(reports.nextColumnIndex("ArrowRight", 5, 10, false), 6);
  assert.equal(reports.nextColumnIndex("ArrowLeft", 5, 10, true), 6);
  assert.equal(reports.nextColumnIndex("ArrowRight", 5, 10, true), 4);
  assert.equal(reports.nextColumnIndex("ArrowRight", 9, 10, false), 9);
  assert.equal(reports.nextColumnIndex("Home", 5, 10, false), 0);
  assert.equal(reports.nextColumnIndex("End", 5, 10, false), 9);
  assert.equal(reports.nextColumnIndex("a", 5, 10, false), null);
});

// Export (WAS-184)

function fileResponse(status, body, headers) {
  return {
    status,
    ok: status >= 200 && status < 300,
    redirected: false,
    text: async () => body || "",
    blob: async () => ({ size: (body || "").length }),
    headers: { get: (name) => (headers || {})[name] || null }
  };
}

function exportHarness(fetchImpl) {
  const events = [];
  const exporter = reports.createExporter({
    fetch: fetchImpl,
    onStart: (format) => events.push(`start:${format}`),
    onFile: (blob, name, format) => events.push(`file:${format}:${name}:${blob.size}`),
    onFailure: (format, message) => events.push(`failure:${format}:${message}`),
    onSignedOut: () => events.push("signed-out"),
    onEnd: (format) => events.push(`end:${format}`)
  });
  return { exporter, events };
}

test("the export carries the filter only, never the section or comparison", () => {
  assert.equal(reports.exportQuery(reports.readState(""), "csv"), "?format=csv");
  assert.equal(
    reports.exportQuery(reports.readState("?preset=custom&startDate=2026-10-01&endDate=2026-10-05&platform=GetirYemek&compare=1&section=platforms"), "xlsx"),
    "?preset=custom&startDate=2026-10-01&endDate=2026-10-05&platform=GetirYemek&format=xlsx"
  );
});

test("the download keeps the server's ASCII file name, or falls back to a plain one", () => {
  assert.equal(
    reports.exportFileName("attachment; filename=wasla-orders-report_2026-10-06_2026-10-08.pdf; filename*=UTF-8''wasla-orders-report_2026-10-06_2026-10-08.pdf", "pdf"),
    "wasla-orders-report_2026-10-06_2026-10-08.pdf"
  );
  assert.equal(reports.exportFileName('attachment; filename="a.csv"', "csv"), "a.csv");
  assert.equal(reports.exportFileName(null, "xlsx"), "wasla-orders-report.xlsx");
  assert.equal(reports.exportFileName("attachment; filename=../../evil.exe;", "csv"), "wasla-orders-report.csv");
});

test("a file is handed over with the server's name", async () => {
  const { exporter, events } = exportHarness(async () => fileResponse(200, "abc", { "Content-Disposition": "attachment; filename=r.csv" }));
  assert.equal(await exporter.run("/reports/export?format=csv", "csv"), "ok");
  assert.deepEqual(events, ["start:csv", "file:csv:r.csv:3", "end:csv"]);
  assert.equal(exporter.isRunning(), false);
});

test("a second export is refused while one runs, without a request", async () => {
  const pending = deferred();
  let requests = 0;
  const { exporter, events } = exportHarness(() => { requests++; return pending.promise; });
  const first = exporter.run("/a", "pdf");
  assert.equal(exporter.isRunning(), true);
  assert.equal(await exporter.run("/b", "csv"), "running");
  assert.equal(requests, 1);
  pending.resolve(fileResponse(200, "x", {}));
  assert.equal(await first, "ok");
  assert.deepEqual(events, ["start:pdf", "file:pdf:wasla-orders-report.pdf:1", "end:pdf"]);
});

test("refusals show the server's own text; other failures show none and are not files", async () => {
  for (const [answer, message] of [[fileResponse(400, " You can select at most 92 days. "), "You can select at most 92 days."], [fileResponse(429, "Already running"), "Already running"], [fileResponse(500, "stack trace"), ""], [fileResponse(502, "<html>"), ""]]) {
    const { exporter, events } = exportHarness(async () => answer);
    assert.equal(await exporter.run("/a", "csv"), "failed");
    assert.deepEqual(events, ["start:csv", `failure:csv:${message}`, "end:csv"]);
  }

  const { exporter, events } = exportHarness(async () => { throw new TypeError("network"); });
  assert.equal(await exporter.run("/a", "xlsx"), "failed");
  assert.deepEqual(events, ["start:xlsx", "failure:xlsx:", "end:xlsx"]);
});

test("an export that takes too long is abandoned as a failure", async () => {
  const events = [];
  const exporter = reports.createExporter({
    fetch: (url, init) => new Promise((_, reject) => init.signal.addEventListener("abort", () => reject(Object.assign(new Error("x"), { name: "AbortError" })))),
    timeoutMs: 5,
    onStart: () => {},
    onFile: () => events.push("file"),
    onFailure: () => events.push("failure"),
    onSignedOut: () => {},
    onEnd: () => events.push("end")
  });
  assert.equal(await exporter.run("/a", "pdf"), "failed");
  assert.deepEqual(events, ["failure", "end"]);
});

test("an expired session reloads the page instead of saving a sign-in page as the file", async () => {
  for (const answer of [Object.assign(fileResponse(200, "<html>"), { redirected: true }), fileResponse(401), fileResponse(403)]) {
    const { exporter, events } = exportHarness(async () => answer);
    assert.equal(await exporter.run("/a", "pdf"), "signed-out");
    assert.deepEqual(events, ["start:pdf", "signed-out", "end:pdf"]);
  }
});

/** A toggle element and a dropdown that, like Bootstrap 5.3.3's hide(), does nothing while the toggle is disabled. */
function fakeDropdown() {
  const classes = new Set(["show"]);
  const attributes = new Map();
  const toggle = {
    classList: { add: (name) => classes.add(name), contains: (name) => classes.has(name) },
    setAttribute: (name, value) => attributes.set(name, value),
    getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null)
  };
  const instance = {
    open: true,
    hide() {
      if (classes.has("disabled") || !this.open) return;
      this.open = false;
      classes.delete("show");
    }
  };
  const bootstrap = { Dropdown: { getInstance: (element) => (element === toggle ? instance : null) } };
  return { toggle, instance, bootstrap };
}

test("starting an export closes the menu before the toggle is marked busy, which Bootstrap would refuse to close", () => {
  const { toggle, instance, bootstrap } = fakeDropdown();
  reports.closeMenuThenMarkBusy(toggle, bootstrap);
  assert.equal(instance.open, false);
  assert.equal(toggle.classList.contains("show"), false);
  assert.equal(toggle.classList.contains("disabled"), true);
  assert.equal(toggle.getAttribute("aria-disabled"), "true");
  assert.equal(toggle.getAttribute("aria-busy"), "true");

  // The order is what matters: marked busy first, the same dropdown cannot be closed any more.
  const late = fakeDropdown();
  late.toggle.classList.add("disabled");
  late.instance.hide();
  assert.equal(late.instance.open, true);
});

test("without Bootstrap, or a menu that was never opened, the toggle is still marked busy", () => {
  for (const bootstrap of [undefined, {}, { Dropdown: { getInstance: () => null } }]) {
    const { toggle } = fakeDropdown();
    reports.closeMenuThenMarkBusy(toggle, bootstrap);
    assert.equal(toggle.classList.contains("disabled"), true);
    assert.equal(toggle.getAttribute("aria-busy"), "true");
  }
});
