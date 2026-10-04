/*
 * Wasla Print Bridge status shell: message contract (version 1) and pure view helpers.
 * No DOM access here, so the same file runs in WebView2 and under `node --test`.
 * The page only renders host snapshots; it never derives or simulates connection or print state.
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

  var VERSION = 1;
  var SNAPSHOT_UPDATED = 'snapshot.updated';
  var COMMANDS = ['ui.ready', 'snapshot.request', 'language.change', 'classicWindow.open'];
  var CONNECTION_STATES = ['online', 'connecting', 'offline', 'error', 'notConfigured', 'stopped'];
  var PRINTER_STATES = ['ready', 'notConfigured', 'notFound', 'dryRun'];
  var JOB_STATUSES = ['pending', 'printing', 'printed', 'failed', 'skipped'];

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

  function isStringMap(value) {
    if (!isObject(value)) {
      return false;
    }
    return Object.keys(value).every(function (key) {
      return isString(value[key]);
    });
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

  function isSnapshot(value) {
    return isObject(value)
      && isString(value.culture)
      && (value.direction === 'ltr' || value.direction === 'rtl')
      && isObject(value.connection)
      && CONNECTION_STATES.indexOf(value.connection.state) >= 0
      && isString(value.connection.label)
      && isString(value.connection.detail)
      && isObject(value.device)
      && isString(value.device.name)
      && isObject(value.printer)
      && isOptionalString(value.printer.name)
      && PRINTER_STATES.indexOf(value.printer.state) >= 0
      && isString(value.printer.label)
      && isObject(value.activity)
      && isOptionalString(value.activity.lastContact)
      && isOptionalString(value.activity.lastPrint)
      && isCount(value.activity.jobsToday)
      && isCount(value.activity.failedToday)
      && (value.lastJob === null || isJob(value.lastJob))
      && typeof value.dryRun === 'boolean'
      && isString(value.versionLabel)
      && isLanguageList(value.languages)
      && isStringMap(value.strings);
  }

  /**
   * Validates a host message. Returns { sequence, snapshot } for a newer, well-formed snapshot,
   * otherwise null (unknown type, wrong version, stale or duplicate sequence, malformed payload).
   */
  function readHostMessage(data, lastSequence) {
    if (!isObject(data) || data.version !== VERSION || data.type !== SNAPSHOT_UPDATED) {
      return null;
    }
    if (!Number.isSafeInteger(data.sequence) || data.sequence <= lastSequence) {
      return null;
    }
    if (!isSnapshot(data.payload)) {
      return null;
    }
    return { sequence: data.sequence, snapshot: data.payload };
  }

  /** Serializes one allowlisted command. Throws for anything outside the contract. */
  function createCommand(type, payload) {
    if (COMMANDS.indexOf(type) < 0) {
      throw new Error('Unsupported Print Bridge shell command.');
    }
    var body = {};
    if (type === 'language.change') {
      if (!isObject(payload) || !isString(payload.culture)) {
        throw new Error('language.change requires a culture.');
      }
      body = { culture: payload.culture };
    }
    return JSON.stringify({ version: VERSION, type: type, payload: body });
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
    COMMANDS: COMMANDS.slice(),
    CONNECTION_STATES: CONNECTION_STATES.slice(),
    readHostMessage: readHostMessage,
    createCommand: createCommand,
    shouldAnnounce: shouldAnnounce,
    announcement: announcement,
    text: text,
    orDash: orDash
  };
});
