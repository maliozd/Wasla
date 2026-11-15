'use strict';

// Node tests for the WebView2 Print Bridge app model (node --test). The fixture is the same one the C#
// tests compare against the host serializer, so both ends agree on the snapshot shape.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const model = require('../../../src/Wasla.PrintBridge/WebShell/Assets/shell-model.js');
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'shell-snapshot.fixture.json'), 'utf8'));

const REQUEST_ID = 'a1b2c3d4-0000-4000-8000-00000000000a';

function message(overrides) {
  const copy = JSON.parse(JSON.stringify(fixture));
  if (overrides) {
    overrides(copy);
  }
  return copy;
}

function snapshot(overrides) {
  return message((m) => { if (overrides) { overrides(m.payload); } }).payload;
}

function operationMessage(payload, sequence) {
  return {
    version: 4,
    type: 'operation.result',
    sequence: sequence || 2,
    payload: Object.assign({ requestId: REQUEST_ID, operation: 'printer.testPrint', outcome: 'succeeded', message: 'Test çıktısı gönderildi.' }, payload)
  };
}

function historyMessage(history, sequence) {
  return {
    version: 4,
    type: 'history.result',
    sequence: sequence || 3,
    payload: {
      requestId: REQUEST_ID,
      history: Object.assign({
        range: 'today',
        page: 0,
        pageSize: 20,
        total: 1,
        hasPrevious: false,
        hasNext: false,
        filtered: false,
        pageLabel: '1–1 / 1',
        items: [{
          ref: 'h0123456789abcdef',
          time: '04.10.2026 20:23',
          order: 'GTR-1001',
          platform: 'Getir',
          printer: 'POS-58',
          status: 'printed',
          statusLabel: '✓ Yazdırıldı',
          detail: null,
          canReprint: true,
          canResolve: false
        }]
      }, history)
    }
  };
}

test('accepts a well-formed host snapshot', () => {
  const result = model.readHostMessage(message(), 0);

  assert.equal(result.kind, 'snapshot');
  assert.equal(result.sequence, 1);
  assert.equal(result.payload.connection.state, 'online');
  assert.equal(result.payload.engine.state, 'running');
  assert.equal(result.payload.device.name, 'Kasa 1');
  assert.deepEqual(result.payload.printer.installed, ['Microsoft Print to PDF', 'POS-58']);
});

test('accepts operation and history results', () => {
  assert.equal(model.readHostMessage(operationMessage(), 1).kind, 'operation');
  assert.equal(model.readHostMessage(historyMessage(), 1).kind, 'history');
  for (const outcome of ['succeeded', 'failed', 'busy', 'duplicate', 'rejected', 'cancelled']) {
    assert.notEqual(model.readHostMessage(operationMessage({ outcome }), 1), null, outcome);
  }
  assert.notEqual(model.readHostMessage(historyMessage({ items: [], total: 0, pageLabel: '0 / 0' }), 1), null);
});

test('drops stale and repeated sequences for every host message type', () => {
  assert.equal(model.readHostMessage(message(), 1), null);
  assert.equal(model.readHostMessage(message(), 5), null);
  assert.equal(model.readHostMessage(operationMessage({}, 2), 2), null);
  assert.equal(model.readHostMessage(historyMessage({}, 3), 5), null);
  assert.notEqual(model.readHostMessage(message((m) => { m.sequence = 6; }), 5), null);
});

test('rejects other versions, types and envelopes', () => {
  const cases = [
    null,
    'snapshot.updated',
    [],
    message((m) => { m.version = 1; }),
    message((m) => { m.version = 2; }),
    // Version 3 had no update section and no update commands (WAS-63 raised it to 4).
    message((m) => { m.version = 3; }),
    message((m) => { m.version = 5; }),
    message((m) => { m.version = '4'; }),
    message((m) => { m.type = 'snapshot.request'; }),
    message((m) => { m.type = 'eval'; }),
    message((m) => { m.sequence = '2'; }),
    message((m) => { m.sequence = 1.5; }),
    message((m) => { delete m.payload; })
  ];

  for (const candidate of cases) {
    assert.equal(model.readHostMessage(candidate, 0), null, JSON.stringify(candidate));
  }
});

test('rejects snapshots the page cannot render truthfully', () => {
  const cases = [
    (p) => { p.connection.state = 'printing'; },
    (p) => { p.connection.state = 'ONLINE'; },
    (p) => { delete p.connection.label; },
    (p) => { p.engine.state = 'paused'; },
    (p) => { delete p.engine; },
    (p) => { p.direction = 'auto'; },
    (p) => { p.printer.state = 'busy'; },
    (p) => { p.printer.name = 42; },
    (p) => { p.printer.installed = 'POS-58'; },
    (p) => { p.printer.installed = [1]; },
    (p) => { p.activity.jobsToday = -1; },
    (p) => { p.activity.failedToday = '0'; },
    (p) => { p.lastJob.status = 'queued'; },
    (p) => { p.actions.start = 'true'; },
    (p) => { delete p.actions.testPrint; },
    (p) => { delete p.busy.testPrint; },
    (p) => { p.diagnostics.lastIssue = 42; },
    (p) => { delete p.diagnostics.webView2Version; },
    (p) => { p.dryRun = 'false'; },
    (p) => { delete p.operational; },
    (p) => { p.operational.testMode = 'false'; },
    (p) => { p.operational.idlePoll.value = '5'; },
    (p) => { p.operational.busyPoll.min = 70; },
    (p) => { delete p.operational.errorPoll.rangeLabel; },
    (p) => { delete p.busy.connectionSetup; },
    (p) => { p.busy.settingsSave = 'no'; },
    (p) => { p.languages = []; },
    (p) => { p.strings['Common.AppTitle'] = { html: '<b>x</b>' }; },
    (p) => { p.strings = null; }
  ];

  for (const mutate of cases) {
    assert.equal(model.readHostMessage(message((m) => mutate(m.payload)), 0), null, mutate.toString());
  }
});

test('rejects malformed operation and history results', () => {
  const cases = [
    operationMessage({ operation: 'host.exec' }),
    operationMessage({ outcome: 'maybe' }),
    operationMessage({ message: 7 }),
    operationMessage({ requestId: 12 }),
    historyMessage({ range: 'forever' }),
    historyMessage({ page: -1 }),
    historyMessage({ pageSize: 0 }),
    historyMessage({ hasNext: 'true' }),
    historyMessage({ items: [{ ref: '../etc', time: '', order: '', platform: '', printer: '', status: 'printed', statusLabel: '', detail: null, canReprint: true }] }),
    historyMessage({ items: [{ ref: 'h0123456789abcdef', time: '', order: '', platform: '', printer: '', status: 'printed', statusLabel: '', detail: null, canReprint: 'yes' }] })
  ];

  for (const candidate of cases) {
    assert.equal(model.readHostMessage(candidate, 0), null, JSON.stringify(candidate.payload));
  }
});

test('accepts every connection state the host can send and an empty last job', () => {
  for (const state of ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped']) {
    assert.notEqual(model.readHostMessage(message((m) => { m.payload.connection.state = state; }), 0), null, state);
  }
  assert.notEqual(model.readHostMessage(message((m) => { m.payload.lastJob = null; m.payload.printer.name = null; m.payload.printer.installed = []; }), 0), null);
  assert.deepEqual(model.CONNECTION_STATES, ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped']);
});

test('creates only allowlisted, versioned commands with their allowed fields', () => {
  assert.deepEqual(JSON.parse(model.createCommand('ui.ready')), { version: 4, type: 'ui.ready', payload: {} });
  assert.deepEqual(JSON.parse(model.createCommand('snapshot.request', { tab: 'settings' })), { version: 4, type: 'snapshot.request', payload: {} });
  assert.deepEqual(
    JSON.parse(model.createCommand('language.change', { culture: 'ar-SA', extra: 'ignored' })),
    { version: 4, type: 'language.change', payload: { culture: 'ar-SA' } });
  assert.deepEqual(
    JSON.parse(model.createCommand('printer.save', { requestId: REQUEST_ID, name: 'POS-58', path: 'C:\\x' })),
    { version: 4, type: 'printer.save', payload: { requestId: REQUEST_ID, name: 'POS-58' } });
  assert.deepEqual(
    JSON.parse(model.createCommand('history.query', { requestId: REQUEST_ID, range: 'last7Days', page: 2, search: '' })),
    { version: 4, type: 'history.query', payload: { requestId: REQUEST_ID, range: 'last7Days', page: 2 } });
  assert.deepEqual(
    JSON.parse(model.createCommand('history.reprint', { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef', jobId: 'x' })),
    { version: 4, type: 'history.reprint', payload: { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef' } });
  assert.deepEqual(model.COMMANDS, [
    'ui.ready', 'snapshot.request', 'language.change',
    'engine.start', 'engine.stop', 'connection.test', 'connection.openSetup', 'connection.reset',
    'printers.refresh', 'printer.save', 'printer.testPrint', 'history.query', 'history.reprint', 'history.resolve',
    'logs.openFolder', 'settings.save', 'update.check', 'update.start', 'update.cancel'
  ]);
  // WAS-61: the user's check of a receipt whose paper outcome is unknown; printed must be a boolean, false included.
  assert.deepEqual(
    JSON.parse(model.createCommand('history.resolve', { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef', printed: false, jobId: 'x' })),
    { version: 4, type: 'history.resolve', payload: { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef', printed: false } });
  assert.throws(() => model.createCommand('history.resolve', { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef', printed: 'yes' }), /invalid printed/);
  assert.throws(() => model.createCommand('history.resolve', { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef' }), /requires printed/);
  assert.deepEqual(
    JSON.parse(model.createCommand('connection.openSetup', { requestId: REQUEST_ID, serverUrl: 'https://x.test' })),
    { version: 4, type: 'connection.openSetup', payload: { requestId: REQUEST_ID } });
  assert.deepEqual(
    JSON.parse(model.createCommand('settings.save', {
      requestId: REQUEST_ID, testMode: false, idlePollSeconds: 5, busyPollSeconds: 1, errorPollSeconds: 15, maxJobsPerPoll: 10
    })),
    { version: 4, type: 'settings.save', payload: { requestId: REQUEST_ID, testMode: false, idlePollSeconds: 5, busyPollSeconds: 1, errorPollSeconds: 15 } });
});

test('settings.save carries only typed whole numbers and a boolean', () => {
  const valid = { requestId: REQUEST_ID, testMode: true, idlePollSeconds: 5, busyPollSeconds: 1, errorPollSeconds: 15 };
  const cases = [
    [{ testMode: 'true' }, /invalid testMode/],
    [{ testMode: 1 }, /invalid testMode/],
    [{ idlePollSeconds: '5' }, /invalid idlePollSeconds/],
    [{ idlePollSeconds: 1.5 }, /invalid idlePollSeconds/],
    [{ busyPollSeconds: -1 }, /invalid busyPollSeconds/],
    [{ errorPollSeconds: 86401 }, /invalid errorPollSeconds/],
    [{ errorPollSeconds: undefined }, /requires errorPollSeconds/]
  ];
  for (const [change, expected] of cases) {
    assert.throws(() => model.createCommand('settings.save', Object.assign({}, valid, change)), expected, JSON.stringify(change));
  }
  assert.throws(() => model.createCommand('connection.openSetup'), /requires requestId/);
});

test('accepts tab requests from the host only for known tabs', () => {
  const navigate = (tab, sequence) => ({ version: 4, type: 'ui.navigate', sequence: sequence || 4, payload: { tab } });

  assert.deepEqual(model.readHostMessage(navigate('history'), 1), { kind: 'navigate', sequence: 4, payload: { tab: 'history' } });
  for (const tab of ['overview', 'printer', 'settings']) {
    assert.notEqual(model.readHostMessage(navigate(tab), 1), null, tab);
  }
  for (const tab of ['logs', 'Settings', '', '../history', null, 3]) {
    assert.equal(model.readHostMessage(navigate(tab), 1), null, String(tab));
  }
  assert.equal(model.readHostMessage(navigate('history', 2), 2), null);
  assert.notEqual(model.readHostMessage(operationMessage({ requestId: null, operation: 'connection.openSetup' }), 1), null);
});

test('the connection button says what the native dialog will do', () => {
  assert.equal(model.setupLabelKey(snapshot()), 'Shell.Action.ChangeConnection');
  assert.equal(model.setupLabelKey(snapshot((p) => { p.connection.state = 'notConfigured'; p.actions.reconnect = true; })), 'Shell.Action.Connect');
  assert.equal(model.setupLabelKey(snapshot((p) => { p.connection.state = 'error'; p.actions.reconnect = true; })), 'Shell.Action.Reconnect');
});

test('operational drafts are checked against the ranges the host sent', () => {
  const s = snapshot();
  const saved = model.savedOperational(s);

  assert.deepEqual(saved, { testMode: false, idle: '5', busy: '1', error: '15' });
  assert.equal(model.sameOperational(saved, { testMode: false, idle: '5', busy: '1', error: '15' }), true);
  assert.equal(model.sameOperational(saved, Object.assign({}, saved, { testMode: true })), false);
  assert.deepEqual(model.invalidOperationalFields(saved, s.operational), []);
  assert.deepEqual(model.invalidOperationalFields({ testMode: false, idle: '0', busy: '61', error: '300' }, s.operational), ['idle', 'busy']);
  assert.deepEqual(model.invalidOperationalFields({ testMode: false, idle: '', busy: '1.5', error: '1e2' }, s.operational), ['idle', 'busy', 'error']);
  assert.deepEqual(model.invalidOperationalFields({ testMode: false, idle: ' 30 ', busy: '60', error: '1' }, s.operational), []);
  assert.deepEqual(
    model.operationalPayload({ testMode: true, idle: ' 30 ', busy: '2', error: '45' }),
    { testMode: true, idlePollSeconds: 30, busyPollSeconds: 2, errorPollSeconds: 45 });
});

test('refuses commands or fields outside the contract', () => {
  for (const type of ['host.exec', 'Start', 'snapshot.updated', '', undefined, 'UI.READY']) {
    assert.throws(() => model.createCommand(type), /Unsupported/, String(type));
  }
  assert.throws(() => model.createCommand('language.change'), /culture/);
  assert.throws(() => model.createCommand('language.change', { culture: 7 }), /culture/);
  assert.throws(() => model.createCommand('engine.start'), /requestId/);
  assert.throws(() => model.createCommand('engine.start', { requestId: 'x' }), /invalid requestId/);
  assert.throws(() => model.createCommand('logs.openFolder', { requestId: REQUEST_ID + '/..' }), /invalid requestId/);
  assert.throws(() => model.createCommand('printer.save', { requestId: REQUEST_ID }), /requires name/);
  assert.throws(() => model.createCommand('printer.save', { requestId: REQUEST_ID, name: ' POS-58' }), /invalid name/);
  assert.throws(() => model.createCommand('printer.save', { requestId: REQUEST_ID, name: 'a\nb' }), /invalid name/);
  assert.throws(() => model.createCommand('printer.save', { requestId: REQUEST_ID, name: 'P'.repeat(257) }), /invalid name/);
  assert.throws(() => model.createCommand('history.query', { requestId: REQUEST_ID, range: 'all', page: 0 }), /invalid range/);
  assert.throws(() => model.createCommand('history.query', { requestId: REQUEST_ID, range: 'today', page: -1 }), /invalid page/);
  assert.throws(() => model.createCommand('history.query', { requestId: REQUEST_ID, range: 'today', page: 1001 }), /invalid page/);
  assert.throws(() => model.createCommand('history.query', { requestId: REQUEST_ID, range: 'today', page: 0, search: 'x'.repeat(65) }), /invalid search/);
  assert.throws(() => model.createCommand('history.reprint', { requestId: REQUEST_ID, itemRef: 'job-1' }), /invalid itemRef/);
});

test('commands stay well under the host size limit', () => {
  assert.ok(model.createCommand('printer.save', { requestId: 'r'.repeat(64), name: 'P'.repeat(256) }).length < 1024);
  assert.ok(model.createCommand('history.query', { requestId: 'r'.repeat(64), range: 'last30Days', page: 1000, search: 'x'.repeat(64) }).length < 1024);
});

test('request ids come from the secure random source', () => {
  assert.equal(model.newRequestId(() => REQUEST_ID), REQUEST_ID);
  assert.match(model.newRequestId(), /^[0-9a-f-]{36}$/);
});

test('hero actions always offer a next step for errors', () => {
  assert.deepEqual(model.heroActions(snapshot()), []);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.engine.state = 'stopped'; p.connection.state = 'stopped'; p.actions.start = true; })), ['start']);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.connection.state = 'error'; p.actions.reconnect = true; })), ['reconnect', 'showDiagnostics']);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.connection.state = 'notConfigured'; p.actions.reconnect = true; })), ['connect']);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.actions.configurePrinter = true; })), ['configurePrinter']);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.connection.state = 'offline'; })), ['checkConnection', 'showDiagnostics']);
  assert.deepEqual(model.heroActions(snapshot((p) => { p.connection.state = 'error'; p.actions.checkConnection = false; })), ['showDiagnostics']);
});

test('the engine toggle follows host state and permissions', () => {
  assert.deepEqual(model.engineToggle(snapshot()), { command: 'engine.stop', labelKey: 'Button.StopListening', enabled: true, busy: false });
  assert.deepEqual(
    model.engineToggle(snapshot((p) => { p.engine.state = 'stopped'; p.actions.stop = false; p.actions.start = false; p.busy.engine = true; })),
    { command: 'engine.start', labelKey: 'Button.StartListening', enabled: false, busy: true });
});

test('tab keyboard navigation follows the reading direction', () => {
  assert.deepEqual(model.TABS, ['overview', 'printer', 'history', 'settings']);
  assert.equal(model.nextTabIndex(0, 'ArrowRight', 4, false), 1);
  assert.equal(model.nextTabIndex(3, 'ArrowRight', 4, false), 0);
  assert.equal(model.nextTabIndex(0, 'ArrowLeft', 4, false), 3);
  assert.equal(model.nextTabIndex(0, 'ArrowLeft', 4, true), 1);
  assert.equal(model.nextTabIndex(1, 'ArrowRight', 4, true), 0);
  assert.equal(model.nextTabIndex(2, 'Home', 4, false), 0);
  assert.equal(model.nextTabIndex(1, 'End', 4, true), 3);
  assert.equal(model.nextTabIndex(1, 'Tab', 4, false), -1);
  assert.equal(model.nextTabIndex(1, 'Enter', 4, false), -1);
});

test('announces only real connection changes', () => {
  const first = snapshot();
  const refreshed = snapshot((p) => { p.activity.jobsToday = 9; p.busy.testPrint = true; });
  const offline = snapshot((p) => { p.connection.state = 'offline'; p.connection.label = 'Çevrimdışı'; });
  const arabic = snapshot((p) => { p.culture = 'ar-SA'; });

  assert.equal(model.shouldAnnounce(null, first), true);
  assert.equal(model.shouldAnnounce(first, refreshed), false);
  assert.equal(model.shouldAnnounce(first, offline), true);
  assert.equal(model.shouldAnnounce(first, arabic), true);
  assert.equal(model.shouldAnnounce(first, null), false);
  assert.equal(model.announcement(first), 'Çevrimiçi. Wasla\'ya bağlı. Yeni fiş işleri bekleniyor.');
});

test('history is re-queried only when print activity changed', () => {
  const first = snapshot();

  assert.equal(model.historyChanged(null, first), true);
  assert.equal(model.historyChanged(first, snapshot((p) => { p.activity.lastContact = 'later'; p.busy.engine = true; })), false);
  assert.equal(model.historyChanged(first, snapshot((p) => { p.activity.jobsToday = 1; })), true);
  assert.equal(model.historyChanged(first, snapshot((p) => { p.lastJob.status = 'failed'; })), true);
  assert.equal(model.historyChanged(first, snapshot((p) => { p.lastJob = null; })), true);
  assert.equal(model.historyChanged(first, snapshot((p) => { p.culture = 'en-US'; })), true);
});

test('status tones fall back to neutral and never stand alone', () => {
  assert.equal(model.printerTone('ready'), 'success');
  assert.equal(model.printerTone('notFound'), 'danger');
  assert.equal(model.printerTone('notConfigured'), 'warning');
  assert.equal(model.printerTone('dryRun'), 'info');
  assert.equal(model.printerTone('unknown'), 'neutral');
  assert.equal(model.jobTone('failed'), 'danger');
  assert.equal(model.jobTone('pending'), 'warning');
  assert.equal(model.jobTone('skipped'), 'neutral');
  assert.equal(model.jobTone('uncertain'), 'warning');
  assert.equal(model.outcomeTone('succeeded'), 'success');
  assert.equal(model.outcomeTone('rejected'), 'error');
  assert.equal(model.outcomeTone('failed'), 'error');
  assert.equal(model.outcomeTone('busy'), 'info');
  assert.equal(model.outcomeTone('cancelled'), 'info');
});

test('falls back to the localized dash, never to a raw key', () => {
  const s = snapshot();

  assert.equal(model.orDash(s, null), '-');
  assert.equal(model.orDash(s, ''), '-');
  assert.equal(model.orDash(s, 'POS-58'), 'POS-58');
  assert.equal(model.text(s, 'Missing.Key'), '');
  assert.equal(model.text(s, 'Settings.Language'), 'Dil');
});

// ---- updates (WAS-63) -------------------------------------------------------------------------------------

function withUpdate(update) {
  return snapshot((p) => { Object.assign(p.update, update); });
}

test('accepts every update stage the host can send, and a failure group only with the failed stage', () => {
  assert.deepEqual(model.UPDATE_STAGES, [
    'unavailable', 'unsupported', 'idle', 'checking', 'upToDate', 'available', 'downloading', 'verifying',
    'waitingForPrinting', 'ready', 'opening', 'setupOpen', 'installing', 'succeeded', 'notApplied', 'cancelled', 'failed'
  ]);
  for (const stage of model.UPDATE_STAGES.filter((s) => s !== 'failed')) {
    assert.notEqual(model.readHostMessage(message((m) => { m.payload.update.stage = stage; m.payload.update.label = 'x'; }), 0), null, stage);
  }
  for (const failure of model.UPDATE_FAILURES) {
    assert.notEqual(model.readHostMessage(message((m) => { Object.assign(m.payload.update, { stage: 'failed', failure, label: 'x' }); }), 0), null, failure);
  }
  assert.notEqual(model.readHostMessage(message((m) => {
    Object.assign(m.payload.update, { stage: 'downloading', downloadPercent: 100, progress: '100%', targetVersion: '1.0.1', availableVersion: '1.0.1' });
  }), 0), null);
});

test('rejects update sections the page cannot render truthfully', () => {
  const cases = [
    (u) => { delete u.installedVersion; },
    (u) => { u.supported = 'true'; },
    (u) => { u.stage = 'installed'; },
    (u) => { u.stage = 'Downloading'; },
    (u) => { u.label = null; },
    (u) => { u.availableVersion = 101; },
    (u) => { u.downloadPercent = 101; },
    (u) => { u.downloadPercent = -1; },
    (u) => { u.downloadPercent = 4.5; },
    (u) => { u.downloadPercent = '40'; },
    (u) => { u.progress = 40; },
    // A failure code, or a failure group without the failed stage, or the failed stage without its group.
    (u) => { u.stage = 'failed'; u.failure = 'hashMismatch'; },
    (u) => { u.stage = 'failed'; u.failure = 'HashMismatch'; },
    (u) => { u.stage = 'failed'; u.failure = null; },
    (u) => { u.failure = 'download'; },
    (u) => { delete u.actions.cancel; },
    (u) => { u.actions.start = 1; },
    (u) => { delete u.busy.check; },
    (u) => { u.busy = null; }
  ];
  for (const mutate of cases) {
    assert.equal(model.readHostMessage(message((m) => mutate(m.payload.update)), 0), null, mutate.toString());
  }
  assert.equal(model.readHostMessage(message((m) => { delete m.payload.update; }), 0), null);
});

test('update commands carry only a request id: the page never names a version, a path or an attempt', () => {
  for (const type of ['update.check', 'update.start', 'update.cancel']) {
    assert.deepEqual(
      JSON.parse(model.createCommand(type, { requestId: REQUEST_ID, version: '9.9.9', path: 'C:\\setup.exe', attemptId: 'x', arguments: '/quiet' })),
      { version: 4, type, payload: { requestId: REQUEST_ID } });
    assert.throws(() => model.createCommand(type), /requires requestId/);
    assert.throws(() => model.createCommand(type, { requestId: 'x' }), /invalid requestId/);
  }
  assert.throws(() => model.createCommand('update.prepare', { requestId: REQUEST_ID }), /Unsupported/);
  for (const operation of ['update.check', 'update.start', 'update.cancel']) {
    for (const outcome of ['succeeded', 'failed', 'busy', 'rejected', 'cancelled']) {
      assert.notEqual(model.readHostMessage(operationMessage({ operation, outcome, message: '' }), 1), null, operation + ' ' + outcome);
    }
  }
});

test('update buttons are shown when the host offers them, and stay while their request runs', () => {
  const idle = snapshot();
  assert.deepEqual(model.updateControl(idle, 'check', false), { hidden: false, disabled: false, busy: false });
  assert.deepEqual(model.updateControl(idle, 'start', false), { hidden: true, disabled: true, busy: false });
  assert.deepEqual(model.updateControl(idle, 'cancel', false), { hidden: true, disabled: true, busy: false });

  // Clicked: the page waits for the host's answer; the host then reports its own busy flag.
  assert.deepEqual(model.updateControl(idle, 'check', true), { hidden: false, disabled: true, busy: true });
  const checking = withUpdate({ stage: 'checking', label: 'Checking', actions: { check: false, start: false, cancel: false }, busy: { check: true, start: false, cancel: false } });
  assert.deepEqual(model.updateControl(checking, 'check', false), { hidden: false, disabled: true, busy: true });
  assert.equal(model.hostBusy(checking, 'updateCheck'), true);
  assert.equal(model.hostBusy(checking, 'updateStart'), false);
  assert.equal(model.hostBusy(checking, 'engine'), false);

  const downloading = withUpdate({ stage: 'downloading', downloadPercent: 40, progress: '40%', actions: { check: false, start: false, cancel: true } });
  assert.deepEqual(model.updateControl(downloading, 'cancel', false), { hidden: false, disabled: false, busy: false });
  assert.deepEqual(model.updateControl(downloading, 'start', false), { hidden: true, disabled: true, busy: false });

  // An unsigned (development) copy offers nothing at all.
  const unsupported = withUpdate({ stage: 'unsupported', supported: false, actions: { check: false, start: false, cancel: false } });
  for (const name of ['check', 'start', 'cancel']) {
    assert.equal(model.updateControl(unsupported, name, false).hidden, true, name);
  }
});

test('after a failed or unapplied attempt the start button says try again', () => {
  for (const stage of ['failed', 'notApplied', 'cancelled']) {
    assert.equal(model.updateStartLabelKey(withUpdate({ stage, failure: stage === 'failed' ? 'download' : null })), 'Shell.Update.Retry', stage);
  }
  for (const stage of ['available', 'idle', 'upToDate']) {
    assert.equal(model.updateStartLabelKey(withUpdate({ stage })), 'Shell.Update.Start', stage);
  }
});

test('update stages have a tone, and the text always says the same', () => {
  assert.equal(model.updateTone('failed'), 'danger');
  assert.equal(model.updateTone('succeeded'), 'success');
  assert.equal(model.updateTone('upToDate'), 'success');
  assert.equal(model.updateTone('notApplied'), 'warning');
  assert.equal(model.updateTone('installing'), 'warning');
  assert.equal(model.updateTone('downloading'), 'info');
  assert.equal(model.updateTone('idle'), 'neutral');
  assert.equal(model.updateTone('unsupported'), 'neutral');
});

test('update changes are announced politely, failures as an alert, never each percent', () => {
  const idle = snapshot();
  const downloading = withUpdate({ stage: 'downloading', label: 'Downloading version 1.0.1.', downloadPercent: 10, progress: '10%' });
  const further = withUpdate({ stage: 'downloading', label: 'Downloading version 1.0.1.', downloadPercent: 55, progress: '55%' });
  const failed = withUpdate({ stage: 'failed', failure: 'verification', label: 'The download could not be verified and was deleted.' });
  const arabic = snapshot((p) => { p.culture = 'ar-SA'; Object.assign(p.update, { stage: 'failed', failure: 'download', label: 'x' }); });

  assert.equal(model.updateAnnouncement(null, idle), null);
  assert.deepEqual(model.updateAnnouncement(idle, downloading), { text: 'Downloading version 1.0.1.', assertive: false });
  assert.equal(model.updateAnnouncement(downloading, further), null);
  assert.deepEqual(model.updateAnnouncement(further, failed), { text: 'The download could not be verified and was deleted.', assertive: true });
  assert.equal(model.updateAnnouncement(failed, idle), null);
  assert.equal(model.updateAnnouncement(idle, arabic), null);
});

test('the model script uses no DOM, storage, network or dynamic code', () => {
  const source = fs.readFileSync(require.resolve('../../../src/Wasla.PrintBridge/WebShell/Assets/shell-model.js'), 'utf8');

  for (const forbidden of ['document.', 'localStorage', 'sessionStorage', 'fetch(', 'XMLHttpRequest', 'eval(', 'new Function', 'innerHTML']) {
    assert.equal(source.includes(forbidden), false, forbidden);
  }
});
