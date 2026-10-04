'use strict';

// Node tests for the WebView2 status shell model (node --test). The fixture is the same one the C#
// tests compare against the host serializer, so both ends agree on the snapshot shape.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const model = require('../../../src/Wasla.PrintBridge/WebShell/Assets/shell-model.js');
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'shell-snapshot.fixture.json'), 'utf8'));

function message(overrides) {
  const copy = JSON.parse(JSON.stringify(fixture));
  if (overrides) {
    overrides(copy);
  }
  return copy;
}

test('accepts a well-formed host snapshot', () => {
  const result = model.readHostMessage(message(), 0);

  assert.equal(result.sequence, 1);
  assert.equal(result.snapshot.connection.state, 'online');
  assert.equal(result.snapshot.device.name, 'Kasa 1');
});

test('drops stale and repeated sequences', () => {
  assert.equal(model.readHostMessage(message(), 1), null);
  assert.equal(model.readHostMessage(message(), 5), null);
  assert.notEqual(model.readHostMessage(message((m) => { m.sequence = 6; }), 5), null);
});

test('rejects other versions, types and envelopes', () => {
  const cases = [
    null,
    'snapshot.updated',
    [],
    message((m) => { m.version = 2; }),
    message((m) => { m.version = '1'; }),
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

test('rejects payloads the page cannot render truthfully', () => {
  const cases = [
    (m) => { m.payload.connection.state = 'printing'; },
    (m) => { m.payload.connection.state = 'ONLINE'; },
    (m) => { delete m.payload.connection.label; },
    (m) => { m.payload.direction = 'auto'; },
    (m) => { m.payload.printer.state = 'busy'; },
    (m) => { m.payload.printer.name = 42; },
    (m) => { m.payload.activity.jobsToday = -1; },
    (m) => { m.payload.activity.failedToday = '0'; },
    (m) => { m.payload.lastJob.status = 'queued'; },
    (m) => { m.payload.dryRun = 'false'; },
    (m) => { m.payload.languages = []; },
    (m) => { m.payload.strings['Common.AppTitle'] = { html: '<b>x</b>' }; },
    (m) => { m.payload.strings = null; }
  ];

  for (const mutate of cases) {
    assert.equal(model.readHostMessage(message(mutate), 0), null, mutate.toString());
  }
});

test('accepts every connection state the host can send and an empty last job', () => {
  for (const state of ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped']) {
    assert.notEqual(model.readHostMessage(message((m) => { m.payload.connection.state = state; }), 0), null, state);
  }
  assert.notEqual(model.readHostMessage(message((m) => { m.payload.lastJob = null; m.payload.printer.name = null; }), 0), null);
  assert.deepEqual(model.CONNECTION_STATES, ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped']);
});

test('creates only allowlisted, versioned commands', () => {
  assert.deepEqual(JSON.parse(model.createCommand('ui.ready')), { version: 1, type: 'ui.ready', payload: {} });
  assert.deepEqual(JSON.parse(model.createCommand('snapshot.request')), { version: 1, type: 'snapshot.request', payload: {} });
  assert.deepEqual(JSON.parse(model.createCommand('classicWindow.open', { tab: 'settings' })), { version: 1, type: 'classicWindow.open', payload: {} });
  assert.deepEqual(
    JSON.parse(model.createCommand('language.change', { culture: 'ar-SA', extra: 'ignored' })),
    { version: 1, type: 'language.change', payload: { culture: 'ar-SA' } });
  assert.deepEqual(model.COMMANDS, ['ui.ready', 'snapshot.request', 'language.change', 'classicWindow.open']);
});

test('refuses commands outside the contract', () => {
  for (const type of ['host.exec', 'Start', 'snapshot.updated', '', undefined, 'UI.READY']) {
    assert.throws(() => model.createCommand(type), /Unsupported/, String(type));
  }
  assert.throws(() => model.createCommand('language.change'), /culture/);
  assert.throws(() => model.createCommand('language.change', { culture: 7 }), /culture/);
});

test('commands stay well under the host size limit', () => {
  assert.ok(model.createCommand('language.change', { culture: 'ru-RU' }).length < 1024);
});

test('announces only real connection changes', () => {
  const first = model.readHostMessage(message(), 0).snapshot;
  const refreshed = model.readHostMessage(message((m) => { m.sequence = 2; m.payload.activity.jobsToday = 9; }), 1).snapshot;
  const offline = model.readHostMessage(message((m) => {
    m.sequence = 3;
    m.payload.connection.state = 'offline';
    m.payload.connection.label = 'Çevrimdışı';
  }), 2).snapshot;
  const arabic = model.readHostMessage(message((m) => { m.sequence = 4; m.payload.culture = 'ar-SA'; }), 3).snapshot;

  assert.equal(model.shouldAnnounce(null, first), true);
  assert.equal(model.shouldAnnounce(first, refreshed), false);
  assert.equal(model.shouldAnnounce(first, offline), true);
  assert.equal(model.shouldAnnounce(first, arabic), true);
  assert.equal(model.shouldAnnounce(first, null), false);
  assert.equal(model.announcement(first), 'Çevrimiçi. Wasla\'ya bağlı. Yeni fiş işleri bekleniyor.');
});

test('falls back to the localized dash, never to a raw key', () => {
  const snapshot = model.readHostMessage(message(), 0).snapshot;

  assert.equal(model.orDash(snapshot, null), '-');
  assert.equal(model.orDash(snapshot, ''), '-');
  assert.equal(model.orDash(snapshot, 'POS-58'), 'POS-58');
  assert.equal(model.text(snapshot, 'Missing.Key'), '');
  assert.equal(model.text(snapshot, 'Settings.Language'), 'Dil');
});

test('the model script uses no DOM, storage, network or dynamic code', () => {
  const source = fs.readFileSync(require.resolve('../../../src/Wasla.PrintBridge/WebShell/Assets/shell-model.js'), 'utf8');

  for (const forbidden of ['document.', 'localStorage', 'sessionStorage', 'fetch(', 'XMLHttpRequest', 'eval(', 'new Function', 'innerHTML']) {
    assert.equal(source.includes(forbidden), false, forbidden);
  }
});
