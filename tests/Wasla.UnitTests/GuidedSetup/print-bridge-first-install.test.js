const test = require("node:test");
const assert = require("node:assert/strict");

const {
  guided, page, MESSAGES, TOKEN, NEW_DEVICE_URL, SESSION_URL, DOWNLOAD_URL, PROTOCOL_URL
} = require("./print-bridge-setup-page.js");

// Guided first install on the Print Bridge setup page: download the ZIP, confirm it is prepared (or say it is already
// installed), then choose automatic or manual connection. Page load does nothing; only "Open Print Bridge and connect"
// creates the setup session; the manual section opens only when the user chooses it.

test.afterEach(() => { guided.stopWatches(); });

const steps = ["pbStepPrepare", "pbStepConnect", "pbStepPrinter"];
const shown = (p) => steps.filter((id) => !p.els[id].hidden);

test("the guided first install starts with an explicit download and does nothing on load", async () => {
  const p = page({ guidedFirstInstall: true });
  await p.settle();

  assert.deepEqual(p.requests, [], "no session, device, token or readiness request on load");
  assert.deepEqual(p.events, []);
  assert.equal(p.win.location.href, "", "nothing is opened or downloaded");
  assert.deepEqual(p.focused, [], "focus stays where the user is");
  assert.deepEqual(shown(p), [], "only the download step is shown");
  assert.equal(p.els.pbManualSetup.hidden, true, "no connection controls yet");
  assert.deepEqual(Object.keys(p.radios), [], "no setup-type choice");
  assert.equal(p.els.pbReconnectDeviceSelect, undefined, "no reconnect picker");
  // A plain same-origin link without any tenant, user, token or setup-code data.
  assert.equal(p.els.pbDownloadBtn.href, DOWNLOAD_URL);
  assert.doesNotMatch(p.els.pbDownloadBtn.href, /[?#]|tenant|user|token|code|[0-9a-f]{8}-/i);
});

test("Download leaves the file to the browser, says so politely and reveals only the prepare step", async () => {
  const p = page({ guidedFirstInstall: true });

  const prevented = p.els.pbDownloadBtn.click();
  await p.settle();

  assert.equal(prevented, false, "the link's own download goes ahead");
  assert.deepEqual(p.requests, [], "downloading creates no session, device or token");
  assert.equal(p.els.pbDownloadStatus.textContent, MESSAGES.downloadStarted);
  assert.doesNotMatch(p.els.pbDownloadStatus.textContent, /install(ed)? (complete|success)|connected/i, "never claims more than a started download");
  assert.deepEqual(shown(p), ["pbStepPrepare"], "the connection choice waits for the user's confirmation");
  assert.deepEqual(p.focused, ["pbStepPrepareTitle"], "focus follows the click to step 2");
  assert.equal(p.win.location.href, "", "the app is not opened");
  assert.equal(p.els.pbManualSetup.hidden, true);

  // Downloading again is harmless.
  p.els.pbDownloadBtn.click();
  p.els.pbDownloadBtn.click();
  await p.settle();
  assert.deepEqual(p.requests, []);
});

test("\"I downloaded and opened Print Bridge\" reveals the connection choice without creating anything", async () => {
  const p = page({ guidedFirstInstall: true });
  p.els.pbDownloadBtn.click();

  p.els.pbPreparedBtn.click();
  await p.settle();

  assert.deepEqual(shown(p), ["pbStepPrepare", "pbStepConnect", "pbStepPrinter"]);
  assert.equal(p.focused.at(-1), "pbStepConnectTitle");
  assert.deepEqual(p.requests, [], "confirming creates no session");
  assert.equal(p.win.location.href, "");
  assert.equal(p.els.pbManualSetup.hidden, true, "manual stays closed until chosen");
});

test("\"Print Bridge is already installed\" skips download and prepare, and creates nothing", async () => {
  const p = page({ guidedFirstInstall: true });

  p.els.pbAlreadyInstalledBtn.click();
  await p.settle();

  assert.deepEqual(shown(p), ["pbStepConnect", "pbStepPrinter"], "the prepare step stays hidden");
  assert.deepEqual(p.focused, ["pbStepConnectTitle"]);
  assert.deepEqual(p.requests, []);
  assert.equal(p.win.location.href, "");
});

test("only Open Print Bridge and connect creates the setup session and opens the app, for a new device", async () => {
  const p = page({ guidedFirstInstall: true });
  p.els.pbAlreadyInstalledBtn.click();
  await p.settle();
  assert.equal(p.requests.length, 0);

  p.els.pbAutoOpenBtn.click();
  await p.settle();

  assert.deepEqual(p.requests.map((r) => [r.method, r.url]), [["POST", SESSION_URL]]);
  assert.equal(p.requests[0].body, "__RequestVerificationToken=antiforgery&setupMode=new", "a new device; never a reconnect");
  assert.equal(p.win.location.href, PROTOCOL_URL, "the app link is opened only now");
  assert.equal(p.els.pbAutoStatus.classList.contains("d-none"), false);

  // The existing session polling starts with it, and only it.
  await p.tickIntervals();
  assert.deepEqual(p.requests.slice(1).map((r) => [r.method, r.url.includes("/setup/session/")]), [["GET", true]]);
});

test("the manual section opens only when the user chooses it, also after a failed automatic attempt", async () => {
  // An automatic attempt that does not open the app shows the fallback, but does not open manual by itself.
  const attempted = page({ guidedFirstInstall: true });
  attempted.els.pbAlreadyInstalledBtn.click();
  attempted.els.pbAutoOpenBtn.click();
  await attempted.settle();
  await attempted.runPageTimers(6000);
  assert.equal(attempted.els.pbAutoFallback.classList.contains("d-none"), false, "the 'didn't open?' fallback appears");
  assert.equal(attempted.els.pbManualSetup.hidden, true, "manual still waits for the user's choice");
  // Its "Connect manually" is that choice.
  assert.equal(attempted.els.pbAutoUseManualLink.click(), true);
  assert.equal(attempted.els.pbManualSetup.hidden, false);

  const failed = page({ guidedFirstInstall: true, session: () => ({ ok: false, body: { success: false, message: "Could not start." } }) });
  failed.els.pbAlreadyInstalledBtn.click();
  failed.els.pbAutoOpenBtn.click();
  await failed.settle();
  assert.equal(failed.win.location.href, "", "a failed session never opens the app");
  assert.equal(failed.els.pbManualSetup.hidden, true);

  // The step's own "Connect manually": a real button, focus moves to the section it opened, nothing is created.
  const chosen = page({ guidedFirstInstall: true });
  chosen.els.pbAlreadyInstalledBtn.click();
  chosen.els.pbChooseManualBtn.click();
  await chosen.settle();
  assert.equal(chosen.els.pbManualSetup.hidden, false);
  assert.equal(chosen.focused.at(-1), "pbManualSetupHeader");
  assert.deepEqual(chosen.requests, [], "choosing manual creates no device or token");
  assert.equal(chosen.els.pbManualTokenValue.value, "");
});

test("an address pointing at the manual section does not skip ahead in the first install", async () => {
  const guidedPage = page({ guidedFirstInstall: true, hash: "#pbManualSetup" });
  await guidedPage.settle();
  assert.equal(guidedPage.els.pbManualSetup.hidden, true);
  assert.deepEqual(guidedPage.focused, []);

  // The ordinary page still honours it.
  const ordinary = page({ hash: "#pbManualSetup" });
  await ordinary.settle();
  assert.equal(ordinary.els.pbManualSetup.hidden, false);
});

test("the manual token is created only by its own action, for a new device, masked and never a connection", async () => {
  const p = page({ guidedFirstInstall: true, readiness: [{ current: true, ready: false }] });
  p.els.pbAlreadyInstalledBtn.click();
  p.els.pbChooseManualBtn.click();
  await p.settle();
  assert.deepEqual(p.requests, []);
  assert.equal(p.els.pbManualTokenActionText.textContent, MESSAGES.createNewDeviceToken);

  await p.issueToken();

  assert.deepEqual(p.requests.filter((r) => r.method === "POST").map((r) => r.url), [NEW_DEVICE_URL]);
  assert.equal(p.els.pbManualTokenValue.value, TOKEN);
  assert.equal(p.els.pbManualTokenValue.type, "password");
  assert.equal(p.panelState(), "not-ready");
  assert.equal(p.status(), MESSAGES.manualNotConnectedYet);
  assert.equal(JSON.stringify(p.events).includes(TOKEN), false);
});

test("on a phone, tablet or non-Windows computer the page explains where to set up, and nothing is downloaded", async () => {
  const others = [
    { platform: "MacIntel", userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)" },
    { platform: "iPhone", userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Mobile" },
    { platform: "Linux armv8l", userAgent: "Mozilla/5.0 (Linux; Android 14) Mobile" },
    { userAgentData: { platform: "Windows", mobile: true }, userAgent: "Mozilla/5.0" }
  ];
  for (const navigator of others) {
    const p = page({ guidedFirstInstall: true, navigator });
    await p.settle();
    assert.equal(p.els.pbNotWindowsNotice.classList.contains("d-none"), false, JSON.stringify(navigator));
    assert.deepEqual(p.requests, []);
    assert.equal(p.win.location.href, "");
  }

  for (const navigator of [{ platform: "Win32", userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }, { userAgentData: { platform: "Windows", mobile: false } }]) {
    const p = page({ guidedFirstInstall: true, navigator });
    assert.equal(p.els.pbNotWindowsNotice.classList.contains("d-none"), true, JSON.stringify(navigator));
  }
});

test("without a published package there is no download control, and an installed app can still connect", async () => {
  const p = page({ guidedFirstInstall: true, packageAvailable: false });

  assert.equal(p.els.pbDownloadBtn, undefined);
  assert.ok(p.els.pbPackageMissingNotice);
  p.els.pbAlreadyInstalledBtn.click();
  await p.settle();
  assert.deepEqual(shown(p), ["pbStepConnect", "pbStepPrinter"]);
  assert.deepEqual(p.requests, []);
});

test("the ordinary setup page keeps its setup-type choice and the automatic reconnect", async () => {
  const devices = [{ id: "dev-2", name: "Bar PC", machineName: "BAR-PC", isActive: true, status: "Offline", lastSeenAtUtc: null }];
  const p = page({ devices });
  await p.settle();
  assert.deepEqual(p.requests, [], "the ordinary page does nothing on load either");
  assert.equal(p.els.pbManualSetup.hidden, false, "the manual connection is always there outside the first install");

  await p.chooseReconnect("dev-2");
  p.els.pbAutoOpenBtn.click();
  await p.settle();

  assert.deepEqual(p.requests.map((r) => [r.method, r.url]), [["POST", SESSION_URL]]);
  assert.equal(p.requests[0].body, "__RequestVerificationToken=antiforgery&setupMode=reconnect&deviceId=dev-2&confirmReplaceActiveToken=true");
  assert.equal(p.win.location.href, PROTOCOL_URL);
});
