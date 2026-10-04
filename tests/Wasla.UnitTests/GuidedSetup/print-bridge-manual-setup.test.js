const test = require("node:test");
const assert = require("node:assert/strict");

// The page harness (real setup script + real guided panel logic) is shared with print-bridge-first-install.test.js.
const {
  guided, page, FakeEvent, MESSAGES,
  WEB_PANEL_URL, TOKEN, NEW_DEVICE_URL, REGENERATE_URL, READINESS_URL, SESSION_URL
} = require("./print-bridge-setup-page.js");

test.afterEach(() => { guided.stopWatches(); });

test("the manual connection shows the Web Panel URL and a newly issued token once, and claims no connection", async () => {
  const p = page({ readiness: [{ current: true, ready: false }] });
  assert.equal(p.els.pbManualTokenActionText.textContent, MESSAGES.createNewDeviceToken);

  await p.issueToken();

  // One antiforgery-protected POST to the new-device endpoint, with nothing but the antiforgery token in it.
  const posts = p.requests.filter((r) => r.method === "POST");
  assert.deepEqual(posts.map((r) => r.url), [NEW_DEVICE_URL]);
  assert.equal(posts[0].headers.RequestVerificationToken, "antiforgery");
  assert.equal(posts[0].body, "__RequestVerificationToken=antiforgery");
  // Both values the app asks for are on the page; the token only in its read-only field.
  assert.equal(p.els.pbSetupServerUrl.value, WEB_PANEL_URL);
  assert.equal(p.els.pbManualTokenValue.value, TOKEN);
  assert.equal(p.els.pbManualTokenTitle.textContent, "Device token created for Print Bridge.");
  assert.equal(p.els.pbManualTokenResult.classList.contains("d-none"), false);
  assert.equal(p.els.pbManualTokenWarning.classList.contains("d-none"), true, "a new device replaces no token");
  assert.equal(p.els.pbManualManageDeviceLink.href, "/print-bridge/devices/dev-1");
  assert.deepEqual(p.focused, ["pbManualTokenTitle"], "focus moves to the result for keyboard and screen-reader users");
  // One token per page view: the button is gone and a second click issues nothing.
  assert.equal(p.els.pbManualTokenActionBtn.classList.contains("d-none"), true);
  await p.issueToken();
  assert.equal(p.requests.filter((r) => r.method === "POST").length, 1);

  // Issuing (or copying) a token is not a connection.
  assert.equal(p.status(), MESSAGES.manualNotConnectedYet);
  assert.equal(p.panelState(), "not-ready");
  assert.deepEqual(p.visible(), ["set-up-later"]);

  // The raw token is nowhere else: no URL, request body, attribute, message, other element or event.
  assert.equal(p.requests.some((r) => r.url.includes(TOKEN) || r.body.includes(TOKEN)), false);
  for (const el of Object.values(p.els)) {
    if (el.id === "pbManualTokenValue") continue;
    assert.equal([el.value, el.textContent, el.innerHTML, el.href].some((v) => String(v || "").includes(TOKEN)), false, el.id);
  }
  for (const el of Object.values(p.els)) assert.equal(Object.values(el.attributes || {}).some((v) => v.includes(TOKEN)), false, el.id);
  assert.equal(JSON.stringify(p.events).includes(TOKEN), false);
});

test("only the server's readiness connects the guided panel, without a reload and without posting Continue", async () => {
  const p = page({ readiness: [{ current: true, ready: false }, { current: true, ready: false }, { current: true, ready: true }] });

  await p.issueToken();
  assert.equal(p.readinessReads(), 1, "the watch reads readiness once right away");
  assert.equal(p.panelState(), "not-ready");
  await p.tick();
  assert.equal(p.panelState(), "not-ready");
  await p.tick();

  assert.equal(p.panelState(), "ready");
  assert.deepEqual(p.visible(), ["get-to-know-your-device"], "Set up later is hidden; the device guide link is shown");
  assert.equal(p.status(), MESSAGES.manualVerified);
  assert.equal(p.pendingTimers(), 0, "the watch stops once the connection is verified");
  await p.tick();
  assert.equal(p.readinessReads(), 3);
  // Reads only; the journey never moves by itself.
  assert.deepEqual(p.requests.filter((r) => r.url === READINESS_URL).map((r) => r.method), ["GET", "GET", "GET"]);
  assert.deepEqual(p.requests.filter((r) => r.method === "POST").map((r) => r.url), [NEW_DEVICE_URL]);
  assert.equal(p.requests.some((r) => r.url.startsWith("/guided-setup/") && r.method === "POST"), false);
  assert.equal(p.win.location.href, "");
});

test("a failed token request shows the error, issues nothing and watches nothing", async () => {
  const p = page({ issue: () => ({ ok: false, body: { success: false, message: "Your plan includes 3 active devices." } }) });

  await p.issueToken();

  assert.match(p.message(), /alert-danger/);
  assert.match(p.message(), /Your plan includes 3 active devices\./);
  assert.equal(p.els.pbManualTokenResult.classList.contains("d-none"), true);
  assert.equal(p.els.pbManualTokenValue.value, "");
  assert.equal(p.els.pbManualTokenActionBtn.disabled, false, "the user can try again");
  assert.equal(p.els.pbManualTokenActionBtn.classList.contains("d-none"), false);
  assert.equal(p.readinessReads(), 0);
  assert.equal(p.panelState(), "not-ready");
});

test("an unverified connection leaves the panel unconnected, and the bounded watch ends with a hint", async () => {
  const p = page({ readiness: [new Error("offline")] });

  await p.issueToken();
  for (let i = 0; i < guided.WATCH_ATTEMPTS + 5 && p.pendingTimers() > 0; i++) await p.tick();

  assert.equal(p.readinessReads(), guided.WATCH_ATTEMPTS, "network errors and not-ready answers keep watching, up to the limit");
  assert.equal(p.pendingTimers(), 0);
  assert.equal(p.panelState(), "not-ready");
  assert.deepEqual(p.visible(), ["set-up-later"]);
  assert.equal(p.status(), MESSAGES.manualStillWaiting);
  assert.equal(p.events.filter((e) => e.type === "wasla:guided-setup-watch-ended").length, 1);
});

test("a section that is no longer current stops the watch at once", async () => {
  const p = page({ readiness: [{ current: false, ready: false }] });

  await p.issueToken();

  assert.equal(p.readinessReads(), 1);
  assert.equal(p.pendingTimers(), 0);
  assert.equal(p.panelState(), "not-ready");
  assert.equal(p.status(), MESSAGES.manualStillWaiting);
});

test("repeated start events, repeated readiness answers and repeated completions are harmless", async () => {
  const p = page({ readiness: [{ current: true, ready: false }, { current: true, ready: true }, { current: true, ready: true }] });

  await p.issueToken();
  await p.raise("wasla:print-bridge-manual-setup-started");
  await p.raise("wasla:print-bridge-manual-setup-started");
  assert.equal(p.pendingTimers(), 1, "one watch per panel");
  assert.equal(p.readinessReads(), 1);

  await p.tick();
  assert.equal(p.panelState(), "ready");
  const changes = p.events.filter((e) => e.type === "wasla:guided-setup-state-changed");
  assert.deepEqual(changes.map((e) => e.detail), [{ section: "print-bridge", state: "ready" }], "the page hears about the change once");

  // Once connected, more events neither read again nor notify again.
  const reads = p.readinessReads();
  await p.raise("wasla:print-bridge-manual-setup-started");
  await p.raise("wasla:print-bridge-setup-completed");
  await p.tick();
  assert.equal(p.readinessReads(), reads);
  assert.equal(p.events.filter((e) => e.type === "wasla:guided-setup-state-changed").length, 1);
  assert.deepEqual(p.visible(), ["get-to-know-your-device"]);
});

test("hiding the page stops the watch", async () => {
  const p = page();
  await p.issueToken();
  assert.equal(p.pendingTimers(), 1);

  assert.deepEqual(guided.stopWatches(), [p.panel]);
  await p.tick();

  assert.equal(p.readinessReads(), 1);
});

test("reconnecting manually replaces the selected device's token only after the user confirms it for that device", async () => {
  const devices = [{ id: "dev-2", name: "Bar PC", machineName: "BAR-PC", isActive: true, status: "Offline", lastSeenAtUtc: null }];
  let answer = false;
  const p = page({
    devices,
    confirm: () => answer,
    issue: (url) => ({ ok: true, body: { success: true, deviceId: "dev-2", deviceName: "Bar PC", token: TOKEN, message: "regenerated" } })
  });

  // No device selected: nothing is asked or sent.
  await p.chooseReconnect("");
  assert.equal(p.els.pbManualTokenActionText.textContent, MESSAGES.createReconnectToken);
  await p.issueToken();
  assert.deepEqual(p.confirms, []);
  assert.equal(p.requests.length, 0);

  // Declined: nothing is sent.
  await p.chooseReconnect("dev-2");
  await p.issueToken();
  assert.deepEqual(p.confirms, ["Regenerate token for Bar PC? The old token will stop working."]);
  assert.equal(p.requests.length, 0);

  // Confirmed: the existing regenerate action for exactly that device.
  answer = true;
  await p.issueToken();
  assert.deepEqual(p.requests.filter((r) => r.method === "POST").map((r) => r.url), [REGENERATE_URL]);
  assert.equal(p.els.pbManualTokenValue.value, TOKEN);
  assert.equal(p.els.pbManualTokenTitle.textContent, "Device token created for Bar PC.");
  assert.equal(p.els.pbManualTokenWarning.textContent, MESSAGES.oldTokenInvalidAfterRegenerate);
  assert.equal(p.els.pbManualTokenWarning.classList.contains("d-none"), false);
  assert.equal(p.els.pbManualManageDeviceLink.href, "/print-bridge/devices/dev-2");
  assert.equal(p.panelState(), "not-ready");
});

/** Every place outside the token field's value where the raw token must never appear. */
function assertTokenOnlyInTheField(p) {
  for (const el of Object.values(p.els)) {
    if (el.id !== "pbManualTokenValue")
      assert.equal([el.value, el.textContent, el.innerHTML, el.href, el.className].some((v) => String(v || "").includes(TOKEN)), false, el.id);
    assert.equal(Object.values(el.attributes || {}).some((v) => v.includes(TOKEN)), false, el.id + " attributes");
  }
  assert.equal(JSON.stringify(p.events).includes(TOKEN), false, "events");
  assert.equal(p.requests.some((r) => r.url.includes(TOKEN) || r.body.includes(TOKEN)), false, "requests");
}

test("the issued token is masked by default; Show and Hide change only how it is displayed", async () => {
  const p = page();
  await p.issueToken();

  assert.equal(p.els.pbManualTokenValue.type, "password", "masked by default");
  assert.equal(p.els.pbManualTokenValue.value, TOKEN);
  assert.equal(p.els.pbManualTokenToggleText.textContent, MESSAGES.showToken);

  p.els.pbManualTokenToggleBtn.click();
  assert.equal(p.els.pbManualTokenValue.type, "text");
  assert.equal(p.els.pbManualTokenToggleText.textContent, MESSAGES.hideToken, "the toggle names its next action");
  assert.equal(p.els.pbManualTokenToggleIcon.className, "bi bi-eye-slash");
  assert.equal(p.els.pbManualTokenValue.value, TOKEN, "revealing does not touch the value");
  assertTokenOnlyInTheField(p);

  p.els.pbManualTokenToggleBtn.click();
  assert.equal(p.els.pbManualTokenValue.type, "password");
  assert.equal(p.els.pbManualTokenToggleText.textContent, MESSAGES.showToken);
  assert.equal(p.els.pbManualTokenToggleIcon.className, "bi bi-eye");

  // Showing or hiding is not a connection, makes no request and raises no event.
  assert.deepEqual(p.requests.filter((r) => r.method === "POST").map((r) => r.url), [NEW_DEVICE_URL]);
  assert.equal(p.events.some((e) => /token/i.test(e.type) && e.type !== "wasla:print-bridge-manual-setup-started"), false);
  assert.equal(p.panelState(), "not-ready");
  assertTokenOnlyInTheField(p);
});

test("a new token result always starts masked, even if the field was revealed before", async () => {
  const p = page();
  p.els.pbManualTokenValue.type = "text";
  p.els.pbManualTokenToggleText.textContent = MESSAGES.hideToken;
  p.els.pbManualTokenToggleIcon.className = "bi bi-eye-slash";

  await p.issueToken();

  assert.equal(p.els.pbManualTokenValue.type, "password");
  assert.equal(p.els.pbManualTokenToggleText.textContent, MESSAGES.showToken);
  assert.equal(p.els.pbManualTokenToggleIcon.className, "bi bi-eye");
});

test("Copy works while the token stays masked", async () => {
  const p = page();
  await p.issueToken();

  p.els.pbManualCopyTokenBtn.click();
  await p.settle();

  assert.deepEqual(p.clipboard, [TOKEN]);
  assert.equal(p.els.pbManualTokenValue.type, "password", "copying does not reveal the token");
  assert.equal(p.els.pbManualTokenToggleText.textContent, MESSAGES.showToken);
  assert.equal(p.els.pbManualCopyStatus.textContent, MESSAGES.deviceTokenCopied);
  assertTokenOnlyInTheField(p);
});

test("without the device page permission there is no management link, and the manual connection still works", async () => {
  const p = page({ canManageDevices: false, readiness: [{ current: true, ready: true }] });

  await p.issueToken();

  assert.equal(p.els.pbManualManageDeviceLink, undefined, "the server did not render the link");
  assert.equal(p.els.pbManualTokenValue.value, TOKEN);
  assert.equal(p.els.pbManualTokenResult.classList.contains("d-none"), false);
  p.els.pbManualCopyTokenBtn.click();
  await p.settle();
  assert.deepEqual(p.clipboard, [TOKEN]);
  assert.equal(p.panelState(), "ready", "the guided panel still connects from the server's readiness");
  assert.equal(p.message(), "", "no error");
});

test("both copy buttons copy the exact value, show it on the button and announce it once", async () => {
  const p = page();

  p.els.pbSetupCopyServerUrlBtn.click();
  await p.settle();
  assert.deepEqual(p.clipboard, [WEB_PANEL_URL]);
  assert.equal(p.els.pbManualCopyStatus.textContent, MESSAGES.webPanelUrlCopied);
  assert.match(p.els.pbSetupCopyServerUrlBtn.innerHTML, /Copied/);

  await p.issueToken();
  p.els.pbManualCopyTokenBtn.click();
  await p.settle();
  assert.deepEqual(p.clipboard, [WEB_PANEL_URL, TOKEN]);
  assert.equal(p.els.pbManualCopyStatus.textContent, MESSAGES.deviceTokenCopied);
  assert.match(p.els.pbManualCopyTokenBtn.innerHTML, /Copied/);
  // Copying is still not a connection.
  assert.equal(p.panelState(), "not-ready");
});

test("the automatic flow still opens the app through a setup session and connects the panel only after a verified completion", async () => {
  const verified = page({
    statuses: [{ success: true, status: "Pending" }, { success: true, status: "Completed", connectionVerified: true }],
    readiness: [{ current: true, ready: true }]
  });

  verified.els.pbAutoOpenBtn.click();
  await verified.settle();
  assert.deepEqual(verified.requests.filter((r) => r.method === "POST").map((r) => r.url), [SESSION_URL]);
  assert.equal(verified.win.location.href, "wasla-printbridge://setup?server=x&code=hidden");
  await verified.tickIntervals();
  assert.equal(verified.panelState(), "not-ready", "a pending session changes nothing");
  await verified.tickIntervals();
  assert.equal(verified.panelState(), "ready");
  assert.deepEqual(verified.visible(), ["get-to-know-your-device"]);
  // The page never showed the session's code.
  assert.equal(JSON.stringify(Object.values(verified.els).map((e) => [e.textContent, e.innerHTML, e.value])).includes("hidden"), false);

  const unverified = page({ statuses: [{ success: true, status: "Completed", connectionVerified: false }], readiness: [{ current: true, ready: true }] });
  unverified.els.pbAutoOpenBtn.click();
  await unverified.settle();
  await unverified.tickIntervals();
  assert.equal(unverified.readinessReads(), 0, "an unverified completion does not ask the panel to re-check");
  assert.equal(unverified.panelState(), "not-ready");
});
