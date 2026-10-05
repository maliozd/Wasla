/*
 * Wasla Print Bridge desktop app: message contract (version 3) and pure view helpers.
 * No DOM access here, so the same file runs in WebView2 and under `node --test`.
 * The page only renders host messages; it never derives or simulates connection, engine or print state,
 * and it never treats a command as successful until the host answers.
 */
(function (root, factory) {
  'use strict';
  var api = factory();
  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  } else {
    root.WaslaPrintBridgeShell = api;
  }
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var VERSION = 3;
  var SNAPSHOT_UPDATED = 'snapshot.updated';
  var OPERATION_RESULT = 'operation.result';
  var HISTORY_RESULT = 'history.result';
  var UI_NAVIGATE = 'ui.navigate';

  // Mirrors ShellMessageContract.Commands on the host: command -> allowed payload fields.
  var COMMANDS = {
    'ui.ready': [],
    'snapshot.request': [],
    'language.change': ['culture'],
    'classicWindow.open': [],
    'engine.start': ['requestId'],
    'engine.stop': ['requestId'],
    'connection.test': ['requestId'],
    'connection.openSetup': ['requestId'],
    'connection.reset': ['requestId'],
    'printers.refresh': ['requestId'],
    'printer.save': ['requestId', 'name'],
    'printer.testPrint': ['requestId'],
    'history.query': ['requestId', 'range', 'page', 'search'],
    'history.reprint': ['requestId', 'itemRef'],
    'logs.openFolder': ['requestId'],
    'settings.save': ['requestId', 'testMode', 'idlePollSeconds', 'busyPollSeconds', 'errorPollSeconds']
  };
  var OPTIONAL_FIELDS = { search: true };
  // The host's envelope for seconds fields (ShellMessageContract.MinSecondsField/MaxSecondsField); the real ranges
  // arrive in every snapshot and are enforced again by the host.
  var SECONDS_ENVELOPE = { min: 0, max: 86400 };

  var CONNECTION_STATES = ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped'];
  var ENGINE_STATES = ['running', 'stopped'];
  var PRINTER_STATES = ['ready', 'notConfigured', 'notFound', 'dryRun'];
  var JOB_STATUSES = ['pending', 'printing', 'printed', 'failed', 'skipped'];
  var OUTCOMES = ['succeeded', 'failed', 'busy', 'duplicate', 'rejected', 'cancelled'];
  var HISTORY_RANGES = ['today', 'last7Days', 'last30Days'];
  var ACTION_FLAGS = ['start', 'stop', 'testPrint', 'checkConnection', 'reconnect', 'configurePrinter', 'resetConnection'];
  var BUSY_FLAGS = ['engine', 'connectionTest', 'connectionReset', 'printersRefresh', 'printerSave', 'testPrint', 'reprint',
    'connectionSetup', 'settingsSave'];
  var TABS = ['overview', 'printer', 'history', 'settings'];
  var OPERATIONAL_FIELDS = ['idle', 'busy', 'error'];
  var WHOLE_NUMBER = /^\d{1,6}$/;

  var REQUEST_ID = /^[A-Za-z0-9][A-Za-z0-9-]{7,63}$/;
  var ITEM_REF = /^h[0-9a-f]{16}$/;
  var CONTROL = /[\u0000-\u001f\u007f-\u009f]/;

  function isObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
  }

  function isString(value) {
    return typeof value === 'string';
  }

  function isOptionalString(value) {
    return value === null || typeof value === 'string';
  }

  function isCount(value) {
    return Number.isSafeInteger(value) && value >= 0;
  }

  function isFlagMap(value, names) {
    return isObject(value) && names.every(function (name) {
      return typeof value[name] === 'boolean';
    });
  }

  function isStringMap(value) {
    return isObject(value) && Object.keys(value).every(function (key) {
      return isString(value[key]);
    });
  }

  function isStringList(value) {
    return Array.isArray(value) && value.every(isString);
  }

  function isLanguageList(value) {
    return Array.isArray(value) && value.length > 0 && value.every(function (item) {
      return isObject(item) && isString(item.culture) && isString(item.nativeName);
    });
  }

  function isJob(value) {
    return isObject(value)
      && JOB_STATUSES.indexOf(value.status) >= 0
      && isString(value.statusLabel)
      && isString(value.order)
      && isString(value.typeLabel)
      && isString(value.time);
  }

  function isDiagnostics(value) {
    return isObject(value)
      && isString(value.appVersion)
      && isString(value.webView2Version)
      && isString(value.engineLabel)
      && isOptionalString(value.lastContact)
      && isOptionalString(value.lastIssue)
      && isString(value.printerName)
      && isString(value.printerLabel)
      && isString(value.testModeLabel);
  }

  function isSecondsSetting(value) {
    return isObject(value)
      && isCount(value.value)
      && isCount(value.min)
      && isCount(value.max)
      && value.min <= value.max
      && isString(value.rangeLabel);
  }

  function isOperational(value) {
    return isObject(value)
      && typeof value.testMode === 'boolean'
      && isSecondsSetting(value.idlePoll)
      && isSecondsSetting(value.busyPoll)
      && isSecondsSetting(value.errorPoll);
  }

  function isSnapshot(value) {
    return isObject(value)
      && isString(value.culture)
      && (value.direction === 'ltr' || value.direction === 'rtl')
      && isObject(value.connection)
      && CONNECTION_STATES.indexOf(value.connection.state) >= 0
      && isString(value.connection.label)
      && isString(value.connection.detail)
      && isObject(value.engine)
      && ENGINE_STATES.indexOf(value.engine.state) >= 0
      && isString(value.engine.label)
      && isObject(value.device)
      && isString(value.device.name)
      && isObject(value.printer)
      && isOptionalString(value.printer.name)
      && PRINTER_STATES.indexOf(value.printer.state) >= 0
      && isString(value.printer.label)
      && isStringList(value.printer.installed)
      && isObject(value.activity)
      && isOptionalString(value.activity.lastContact)
      && isOptionalString(value.activity.lastPrint)
      && isCount(value.activity.jobsToday)
      && isCount(value.activity.failedToday)
      && (value.lastJob === null || isJob(value.lastJob))
      && isFlagMap(value.actions, ACTION_FLAGS)
      && isFlagMap(value.busy, BUSY_FLAGS)
      && isDiagnostics(value.diagnostics)
      && typeof value.dryRun === 'boolean'
      && isOperational(value.operational)
      && isLanguageList(value.languages)
      && isStringMap(value.strings);
  }

  function isOperationResult(value) {
    return isObject(value)
      && (value.requestId === null || isString(value.requestId))
      && Object.prototype.hasOwnProperty.call(COMMANDS, value.operation)
      && OUTCOMES.indexOf(value.outcome) >= 0
      && isString(value.message);
  }

  function isHistoryItem(value) {
    return isObject(value)
      && isString(value.ref) && ITEM_REF.test(value.ref)
      && isString(value.time)
      && isString(value.order)
      && isString(value.platform)
      && isString(value.printer)
      && JOB_STATUSES.indexOf(value.status) >= 0
      && isString(value.statusLabel)
      && isOptionalString(value.detail)
      && typeof value.canReprint === 'boolean';
  }

  function isHistoryResult(value) {
    if (!isObject(value) || !isString(value.requestId) || !isObject(value.history)) {
      return false;
    }
    var h = value.history;
    return HISTORY_RANGES.indexOf(h.range) >= 0
      && isCount(h.page)
      && isCount(h.pageSize)
      && isCount(h.total)
      && typeof h.hasPrevious === 'boolean'
      && typeof h.hasNext === 'boolean'
      && typeof h.filtered === 'boolean'
      && isString(h.pageLabel)
      && Array.isArray(h.items)
      && h.items.length <= h.pageSize
      && h.items.every(isHistoryItem);
  }

  function isNavigate(value) {
    return isObject(value) && TABS.indexOf(value.tab) >= 0;
  }

  var VALIDATORS = {};
  VALIDATORS[SNAPSHOT_UPDATED] = { kind: 'snapshot', check: isSnapshot };
  VALIDATORS[OPERATION_RESULT] = { kind: 'operation', check: isOperationResult };
  VALIDATORS[HISTORY_RESULT] = { kind: 'history', check: isHistoryResult };
  VALIDATORS[UI_NAVIGATE] = { kind: 'navigate', check: isNavigate };

  /**
   * Validates a host message. Returns { kind, sequence, payload } for a newer, well-formed message,
   * otherwise null (unknown type, wrong version, stale or duplicate sequence, malformed payload).
   */
  function readHostMessage(data, lastSequence) {
    if (!isObject(data) || data.version !== VERSION || !Object.prototype.hasOwnProperty.call(VALIDATORS, data.type)) {
      return null;
    }
    if (!Number.isSafeInteger(data.sequence) || data.sequence <= lastSequence) {
      return null;
    }
    var validator = VALIDATORS[data.type];
    if (!validator.check(data.payload)) {
      return null;
    }
    return { kind: validator.kind, sequence: data.sequence, payload: data.payload };
  }

  function validField(name, value) {
    switch (name) {
      case 'requestId':
        return isString(value) && REQUEST_ID.test(value);
      case 'culture':
        return isString(value) && value.length > 0;
      case 'name':
        return isString(value) && value.length > 0 && value.length <= 256 && value.trim() === value && !CONTROL.test(value);
      case 'range':
        return HISTORY_RANGES.indexOf(value) >= 0;
      case 'page':
        return Number.isSafeInteger(value) && value >= 0 && value <= 1000;
      case 'search':
        return isString(value) && value.length <= 64 && !CONTROL.test(value);
      case 'itemRef':
        return isString(value) && ITEM_REF.test(value);
      case 'testMode':
        return typeof value === 'boolean';
      case 'idlePollSeconds':
      case 'busyPollSeconds':
      case 'errorPollSeconds':
        return Number.isSafeInteger(value) && value >= SECONDS_ENVELOPE.min && value <= SECONDS_ENVELOPE.max;
      default:
        return false;
    }
  }

  /** Serializes one allowlisted command with only its allowed fields. Throws for anything outside the contract. */
  function createCommand(type, payload) {
    if (!Object.prototype.hasOwnProperty.call(COMMANDS, type)) {
      throw new Error('Unsupported Print Bridge shell command.');
    }
    var source = isObject(payload) ? payload : {};
    var body = {};
    COMMANDS[type].forEach(function (field) {
      var value = source[field];
      if (value === undefined || value === null || value === '') {
        if (OPTIONAL_FIELDS[field]) {
          return;
        }
        throw new Error(type + ' requires ' + field + '.');
      }
      if (!validField(field, value)) {
        throw new Error(type + ' has an invalid ' + field + '.');
      }
      body[field] = value;
    });
    return JSON.stringify({ version: VERSION, type: type, payload: body });
  }

  /** Request ids make repeated clicks idempotent on the host. */
  function newRequestId(random) {
    var generate = random || (typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID.bind(crypto) : null);
    if (!generate) {
      throw new Error('No secure random source.');
    }
    return generate();
  }

  function flag(snapshot, group, name) {
    return Boolean(snapshot && snapshot[group] && snapshot[group][name] === true);
  }

  /**
   * The context actions next to the connection state, most relevant first. Every error state offers a next step.
   * Returns ids from: connect, reconnect, configurePrinter, checkConnection, start, showDiagnostics.
   */
  function heroActions(snapshot) {
    var state = snapshot.connection.state;
    var failing = state === 'error' || state === 'offline';
    if (flag(snapshot, 'actions', 'reconnect')) {
      if (state === 'notConfigured') {
        return ['connect'];
      }
      return failing ? ['reconnect', 'showDiagnostics'] : ['reconnect'];
    }
    if (flag(snapshot, 'actions', 'configurePrinter')) {
      return ['configurePrinter'];
    }
    if (failing) {
      return flag(snapshot, 'actions', 'checkConnection') ? ['checkConnection', 'showDiagnostics'] : ['showDiagnostics'];
    }
    if (snapshot.engine.state === 'stopped' && flag(snapshot, 'actions', 'start')) {
      return ['start'];
    }
    return [];
  }

  /** The engine toggle in the action bar: Stop while running, Start otherwise (enabled only when allowed). */
  function engineToggle(snapshot) {
    var running = snapshot.engine.state === 'running';
    return {
      command: running ? 'engine.stop' : 'engine.start',
      labelKey: running ? 'Button.StopListening' : 'Button.StartListening',
      enabled: running ? flag(snapshot, 'actions', 'stop') : flag(snapshot, 'actions', 'start'),
      busy: flag(snapshot, 'busy', 'engine')
    };
  }

  /** The label of the button that opens the native connection dialog, matching what the dialog will do. */
  function setupLabelKey(snapshot) {
    if (snapshot.connection.state === 'notConfigured') {
      return 'Shell.Action.Connect';
    }
    return flag(snapshot, 'actions', 'reconnect') ? 'Shell.Action.Reconnect' : 'Shell.Action.ChangeConnection';
  }

  /** The saved operational values in the form's shape (inputs hold text). */
  function savedOperational(snapshot) {
    var o = snapshot.operational;
    return {
      testMode: o.testMode,
      idle: String(o.idlePoll.value),
      busy: String(o.busyPoll.value),
      error: String(o.errorPoll.value)
    };
  }

  function sameOperational(a, b) {
    return a.testMode === b.testMode && a.idle === b.idle && a.busy === b.busy && a.error === b.error;
  }

  /**
   * The draft fields that are not whole numbers inside the ranges the host sent. The host checks again with the
   * same validator, so this only gives immediate feedback.
   */
  function invalidOperationalFields(draft, operational) {
    var limits = { idle: operational.idlePoll, busy: operational.busyPoll, error: operational.errorPoll };
    return OPERATIONAL_FIELDS.filter(function (name) {
      var text = String(draft[name]).trim();
      if (!WHOLE_NUMBER.test(text)) {
        return true;
      }
      var value = Number(text);
      return value < limits[name].min || value > limits[name].max;
    });
  }

  /** The settings.save payload for a valid draft. */
  function operationalPayload(draft) {
    return {
      testMode: draft.testMode === true,
      idlePollSeconds: Number(String(draft.idle).trim()),
      busyPollSeconds: Number(String(draft.busy).trim()),
      errorPollSeconds: Number(String(draft.error).trim())
    };
  }

  /** Announce only real connection changes, not every periodic refresh. */
  function shouldAnnounce(previous, next) {
    if (!next) {
      return false;
    }
    if (!previous) {
      return true;
    }
    return previous.connection.state !== next.connection.state
      || previous.connection.label !== next.connection.label
      || previous.culture !== next.culture;
  }

  function announcement(snapshot) {
    var detail = snapshot.connection.detail;
    return detail ? snapshot.connection.label + '. ' + detail : snapshot.connection.label;
  }

  /** History is re-queried only when print activity changed, not on every snapshot. */
  function historyChanged(previous, next) {
    if (!previous || !next) {
      return Boolean(next);
    }
    var a = previous.activity;
    var b = next.activity;
    var jobA = previous.lastJob;
    var jobB = next.lastJob;
    return a.lastPrint !== b.lastPrint
      || a.jobsToday !== b.jobsToday
      || a.failedToday !== b.failedToday
      || (jobA === null) !== (jobB === null)
      || (jobA !== null && jobB !== null && (jobA.order !== jobB.order || jobA.status !== jobB.status || jobA.time !== jobB.time))
      || previous.culture !== next.culture;
  }

  /** Roving focus for the tab list. Arrow keys follow the reading direction. Returns -1 for other keys. */
  function nextTabIndex(index, key, count, rtl) {
    var forward = rtl ? 'ArrowLeft' : 'ArrowRight';
    var backward = rtl ? 'ArrowRight' : 'ArrowLeft';
    if (key === forward) {
      return (index + 1) % count;
    }
    if (key === backward) {
      return (index - 1 + count) % count;
    }
    if (key === 'Home') {
      return 0;
    }
    if (key === 'End') {
      return count - 1;
    }
    return -1;
  }

  /** Status colour family; every badge also carries its text label, so colour is never the only signal. */
  function printerTone(state) {
    return { ready: 'success', dryRun: 'info', notConfigured: 'warning', notFound: 'danger' }[state] || 'neutral';
  }

  function jobTone(status) {
    return { printed: 'success', printing: 'info', pending: 'warning', failed: 'danger' }[status] || 'neutral';
  }

  function outcomeTone(outcome) {
    if (outcome === 'succeeded') {
      return 'success';
    }
    if (outcome === 'failed' || outcome === 'rejected') {
      return 'error';
    }
    return 'info';
  }

  function text(snapshot, key) {
    var value = snapshot.strings[key];
    return isString(value) ? value : '';
  }

  /** Values the page shows when the host has nothing to report. */
  function orDash(snapshot, value) {
    return isString(value) && value.length > 0 ? value : text(snapshot, 'Common.Dash');
  }

  return {
    VERSION: VERSION,
    COMMANDS: Object.keys(COMMANDS),
    CONNECTION_STATES: CONNECTION_STATES.slice(),
    TABS: TABS.slice(),
    readHostMessage: readHostMessage,
    createCommand: createCommand,
    newRequestId: newRequestId,
    heroActions: heroActions,
    engineToggle: engineToggle,
    setupLabelKey: setupLabelKey,
    savedOperational: savedOperational,
    sameOperational: sameOperational,
    invalidOperationalFields: invalidOperationalFields,
    operationalPayload: operationalPayload,
    shouldAnnounce: shouldAnnounce,
    announcement: announcement,
    historyChanged: historyChanged,
    nextTabIndex: nextTabIndex,
    outcomeTone: outcomeTone,
    printerTone: printerTone,
    jobTone: jobTone,
    flag: flag,
    text: text,
    orDash: orDash
  };
});
