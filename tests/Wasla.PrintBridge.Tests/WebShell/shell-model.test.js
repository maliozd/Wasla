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
    version: 2,
    type: 'operation.result',
    sequence: sequence || 2,
    payload: Object.assign({ requestId: REQUEST_ID, operation: 'printer.testPrint', outcome: 'succeeded', message: 'Test çıktısı gönderildi.' }, payload)
  };
}

function historyMessage(history, sequence) {
  return {
    version: 2,
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
          canReprint: true
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
    message((m) => { m.version = 3; }),
    message((m) => { m.version = '2'; }),
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
  assert.deepEqual(JSON.parse(model.createCommand('ui.ready')), { version: 2, type: 'ui.ready', payload: {} });
  assert.deepEqual(JSON.parse(model.createCommand('classicWindow.open', { tab: 'settings' })), { version: 2, type: 'classicWindow.open', payload: {} });
  assert.deepEqual(
    JSON.parse(model.createCommand('language.change', { culture: 'ar-SA', extra: 'ignored' })),
    { version: 2, type: 'language.change', payload: { culture: 'ar-SA' } });
  assert.deepEqual(
    JSON.parse(model.createCommand('printer.save', { requestId: REQUEST_ID, name: 'POS-58', path: 'C:\\x' })),
    { version: 2, type: 'printer.save', payload: { requestId: REQUEST_ID, name: 'POS-58' } });
  assert.deepEqual(
    JSON.parse(model.createCommand('history.query', { requestId: REQUEST_ID, range: 'last7Days', page: 2, search: '' })),
    { version: 2, type: 'history.query', payload: { requestId: REQUEST_ID, range: 'last7Days', page: 2 } });
  assert.deepEqual(
    JSON.parse(model.createCommand('history.reprint', { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef', jobId: 'x' })),
    { version: 2, type: 'history.reprint', payload: { requestId: REQUEST_ID, itemRef: 'h0123456789abcdef' } });
  assert.deepEqual(model.COMMANDS, [
    'ui.ready', 'snapshot.request', 'language.change', 'classicWindow.open',
    'engine.start', 'engine.stop', 'connection.test', 'connection.openSetup', 'connection.reset',
    'printers.refresh', 'printer.save', 'printer.testPrint', 'history.query', 'history.reprint',
    'logs.openFolder'
  ]);
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
  assert.deepEqual(model.heroActions(snapshot((p) => { p.connection.state = 'notConfigured'; p.actions.reconnect = true; })), ['reconnect']);
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

test('the model script uses no DOM, storage, network or dynamic code', () => {
  const source = fs.readFileSync(require.resolve('../../../src/Wasla.PrintBridge/WebShell/Assets/shell-model.js'), 'utf8');

  for (const forbidden of ['document.', 'localStorage', 'sessionStorage', 'fetch(', 'XMLHttpRequest', 'eval(', 'new Function', 'innerHTML']) {
    assert.equal(source.includes(forbidden), false, forbidden);
  }
});
