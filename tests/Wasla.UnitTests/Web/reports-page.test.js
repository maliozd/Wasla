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
