/*
 * Wasla Print Bridge desktop app: DOM wiring. Renders host messages with textContent only and sends
 * allowlisted commands. Nothing is stored in localStorage, sessionStorage or cookies. A command button is
 * held only until the host answers its own request; busy, success and failure always come from the host.
 */
(function () {
  'use strict';

  var model = window.WaslaPrintBridgeShell;
  var webview = window.chrome && window.chrome.webview;
  if (!model || !webview) {
    return;
  }

  var TOAST_MS = 6000;
  var SEARCH_DEBOUNCE_MS = 300;
  var BUSY_FOR_COMMAND = {
    'engine.start': 'engine',
    'engine.stop': 'engine',
    'connection.test': 'connectionTest',
    'connection.openSetup': 'connectionSetup',
    'connection.reset': 'connectionReset',
    'printers.refresh': 'printersRefresh',
    'printer.save': 'printerSave',
    'printer.testPrint': 'testPrint',
    'history.reprint': 'reprint',
    'settings.save': 'settingsSave'
  };
  var OPS_INPUTS = { idle: 'ops-idle', busy: 'ops-busy', error: 'ops-error' };

  var lastSequence = 0;
  var current = null;
  var activeTab = 'overview';
  var languageRequestPending = false;
  var pending = {};           // busy group -> request id awaiting the host's answer
  var printerDraft = null;     // the user's unsaved printer choice
  var opsDraft = null;         // the user's unsaved operational settings; null shows the saved values
  var history = { range: 'today', page: 0, search: '', requestId: null, result: null, confirming: null };
  var toastTimer = null;
  var searchTimer = null;

  function byId(id) {
    return document.getElementById(id);
  }

  function setText(element, value) {
    if (element.textContent !== value) {
      element.textContent = value;
    }
  }

  function send(type, payload) {
    webview.postMessage(model.createCommand(type, payload));
  }

  function sendTracked(type, extra) {
    var requestId = model.newRequestId();
    var group = BUSY_FOR_COMMAND[type];
    if (group) {
      pending[group] = requestId;
    }
    var payload = { requestId: requestId };
    Object.keys(extra || {}).forEach(function (key) { payload[key] = extra[key]; });
    send(type, payload);
    if (group) {
      // Safety net only: the host answers within its 30 s operation timeout.
      setTimeout(function () {
        if (pending[group] === requestId) {
          delete pending[group];
          renderButtons();
        }
      }, 45000);
    }
    renderButtons();
    return requestId;
  }

  function isBusy(group) {
    return Boolean(pending[group]) || model.flag(current, 'busy', group);
  }

  // ---- rendering ---------------------------------------------------------------------------------------

  function renderStrings(snapshot) {
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i += 1) {
      setText(nodes[i], model.text(snapshot, nodes[i].getAttribute('data-i18n')));
    }
    var labelled = document.querySelectorAll('[data-i18n-label]');
    for (var j = 0; j < labelled.length; j += 1) {
      labelled[j].setAttribute('aria-label', model.text(snapshot, labelled[j].getAttribute('data-i18n-label')));
    }
    var placeholders = document.querySelectorAll('[data-i18n-placeholder]');
    for (var k = 0; k < placeholders.length; k += 1) {
      placeholders[k].setAttribute('placeholder', model.text(snapshot, placeholders[k].getAttribute('data-i18n-placeholder')));
    }
    document.title = model.text(snapshot, 'Common.AppTitle');
  }

  function fillOptions(select, entries, signature) {
    if (select.getAttribute('data-signature') === signature) {
      return;
    }
    while (select.firstChild) {
      select.removeChild(select.firstChild);
    }
    entries.forEach(function (entry) {
      var option = document.createElement('option');
      option.value = entry.value;
      option.textContent = entry.label;
      if (entry.lang) {
        option.lang = entry.lang;
      }
      select.appendChild(option);
    });
    select.setAttribute('data-signature', signature);
  }

  function renderLanguages(snapshot) {
    var entries = snapshot.languages.map(function (l) { return { value: l.culture, label: l.nativeName, lang: l.culture }; });
    var signature = entries.map(function (e) { return e.value + '=' + e.label; }).join('|');
    ['language', 'settings-language'].forEach(function (id) {
      var select = byId(id);
      fillOptions(select, entries, signature);
      select.value = snapshot.culture;
      select.removeAttribute('aria-busy');
    });
    languageRequestPending = false;
  }

  function renderConnection(snapshot) {
    var c = snapshot.connection;
    byId('connection').setAttribute('data-state', c.state);
    byId('status-pill').setAttribute('data-state', c.state);
    setText(byId('status-pill-label'), c.label);
    setText(byId('connection-label'), c.label);
    setText(byId('connection-detail'), c.detail);
  }

  var HERO_ACTIONS = {
    connect: { labelKey: 'Shell.Action.Connect', primary: true, command: 'connection.openSetup', busy: 'connectionSetup' },
    reconnect: { labelKey: 'Shell.Action.Reconnect', primary: true, command: 'connection.openSetup', busy: 'connectionSetup' },
    configurePrinter: { labelKey: 'Shell.Action.ConfigurePrinter', primary: true, run: function () { activateTab('printer', true); byId('printer-select').focus(); } },
    checkConnection: { labelKey: 'Button.TestConnection', primary: true, command: 'connection.test', busy: 'connectionTest' },
    start: { labelKey: 'Button.StartListening', primary: true, command: 'engine.start', busy: 'engine' },
    showDiagnostics: { labelKey: 'Shell.Action.ShowDiagnostics', primary: false, run: function () { activateTab('settings', true); byId('diagnostics-title').scrollIntoView({ block: 'start' }); } }
  };

  function renderHeroActions(snapshot) {
    var container = byId('hero-actions');
    var ids = model.heroActions(snapshot);
    var signature = ids.join('|');
    if (container.getAttribute('data-signature') !== signature) {
      var hadFocus = container.contains(document.activeElement) ? document.activeElement.getAttribute('data-hero') : null;
      while (container.firstChild) {
        container.removeChild(container.firstChild);
      }
      ids.forEach(function (id) {
        var spec = HERO_ACTIONS[id];
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'pb-button' + (spec.primary ? ' pb-button--primary' : '');
        button.setAttribute('data-hero', id);
        if (spec.command) {
          button.setAttribute('data-command', spec.command);
          button.setAttribute('data-busy', spec.busy);
        }
        button.addEventListener('click', function () {
          if (button.getAttribute('aria-disabled') === 'true') {
            return;
          }
          if (spec.command) {
            sendTracked(spec.command);
          } else {
            spec.run();
          }
        });
        container.appendChild(button);
      });
      container.setAttribute('data-signature', signature);
      if (hadFocus) {
        var again = container.querySelector('[data-hero="' + hadFocus + '"]');
        if (again) {
          again.focus();
        }
      }
    }
    var buttons = container.querySelectorAll('button');
    for (var i = 0; i < buttons.length; i += 1) {
      setText(buttons[i], model.text(snapshot, HERO_ACTIONS[buttons[i].getAttribute('data-hero')].labelKey));
    }
  }

  function renderOverview(snapshot) {
    byId('dry-run').hidden = !snapshot.dryRun;
    byId('tile-printer').setAttribute('data-state', snapshot.printer.state);
    setText(byId('printer-name'), model.orDash(snapshot, snapshot.printer.name));
    setText(byId('printer-state'), snapshot.printer.label);
    byId('printer-state').setAttribute('data-tone', model.printerTone(snapshot.printer.state));
    byId('tile-engine').setAttribute('data-state', snapshot.engine.state);
    setText(byId('engine-label'), snapshot.engine.label);
    setText(byId('device-name'), snapshot.device.name);
    setText(byId('last-contact'), model.orDash(snapshot, snapshot.activity.lastContact));
    setText(byId('jobs-today'), String(snapshot.activity.jobsToday));
    setText(byId('failed-today'), String(snapshot.activity.failedToday));
    byId('failed-stat').setAttribute('data-alert', snapshot.activity.failedToday > 0 ? 'true' : 'false');
    setText(byId('last-print'), model.orDash(snapshot, snapshot.activity.lastPrint));

    var job = snapshot.lastJob;
    byId('last-job-empty').hidden = job !== null;
    byId('last-job-row').hidden = job === null;
    byId('last-job').setAttribute('data-status', job ? job.status : '');
    setText(byId('last-job-order'), job ? job.order : '');
    setText(byId('last-job-status'), job ? job.statusLabel : '');
    byId('last-job-status').setAttribute('data-tone', job ? model.jobTone(job.status) : 'neutral');
    setText(byId('last-job-time'), job ? job.typeLabel + ' · ' + job.time : '');
  }

  function renderPrinter(snapshot) {
    var select = byId('printer-select');
    var saved = snapshot.printer.name;
    var installed = snapshot.printer.installed;
    var entries = [{ value: '', label: model.text(snapshot, 'Shell.Printer.Placeholder') }];
    installed.forEach(function (name) { entries.push({ value: name, label: name }); });
    var savedMissing = saved !== null && installed.length > 0 && installed.indexOf(saved) < 0;
    if (savedMissing) {
      entries.push({ value: saved, label: saved });
    }
    fillOptions(select, entries, entries.map(function (e) { return e.value; }).join('|') + '#' + entries[0].label);
    if (printerDraft !== null && printerDraft === saved) {
      printerDraft = null;
    }
    select.value = printerDraft !== null ? printerDraft : (saved || '');
    byId('printer-none').hidden = installed.length > 0 || model.flag(snapshot, 'busy', 'printersRefresh');
    byId('printer-missing').hidden = !savedMissing;
    byId('printer-panel-state').setAttribute('data-tone', model.printerTone(snapshot.printer.state));
    setText(byId('printer-panel-state'), snapshot.printer.label);
    byId('printer-dry-run').hidden = !snapshot.dryRun;
  }

  function renderSettings(snapshot) {
    var d = snapshot.diagnostics;
    setText(byId('settings-connection-state'), snapshot.connection.label);
    byId('settings-open-setup').setAttribute('data-i18n', model.setupLabelKey(snapshot));
    setText(byId('settings-open-setup'), model.text(snapshot, model.setupLabelKey(snapshot)));
    renderOperational(snapshot);
    setText(byId('diag-app-version'), d.appVersion);
    setText(byId('diag-webview2'), d.webView2Version);
    setText(byId('diag-engine'), d.engineLabel);
    setText(byId('diag-last-contact'), model.orDash(snapshot, d.lastContact));
    setText(byId('diag-last-error'), d.lastIssue || model.text(snapshot, 'Shell.Diagnostics.None'));
    setText(byId('diag-printer'), d.printerName + ' · ' + d.printerLabel);
    setText(byId('diag-test-mode'), d.testModeLabel);
  }

  // ---- operational settings ------------------------------------------------------------------------------

  function readOpsForm() {
    return {
      testMode: byId('ops-test-mode').checked,
      idle: byId(OPS_INPUTS.idle).value,
      busy: byId(OPS_INPUTS.busy).value,
      error: byId(OPS_INPUTS.error).value
    };
  }

  /**
   * Shows the saved values (what the engine uses) unless the user is editing. A draft is dropped as soon as the
   * host reports exactly those values as saved, so the form never claims a value the host did not confirm.
   */
  function renderOperational(snapshot) {
    var o = snapshot.operational;
    var saved = model.savedOperational(snapshot);
    if (opsDraft !== null && model.sameOperational(opsDraft, saved)) {
      opsDraft = null;
    }
    var shown = opsDraft !== null ? opsDraft : saved;
    var limits = { idle: o.idlePoll, busy: o.busyPoll, error: o.errorPoll };
    Object.keys(OPS_INPUTS).forEach(function (name) {
      var input = byId(OPS_INPUTS[name]);
      input.setAttribute('min', String(limits[name].min));
      input.setAttribute('max', String(limits[name].max));
      setText(byId(OPS_INPUTS[name] + '-range'), limits[name].rangeLabel);
      if (input.value !== shown[name]) {
        input.value = shown[name];
      }
    });
    byId('ops-test-mode').checked = shown.testMode;
    renderOpsState();
  }

  function renderOpsState() {
    if (!current) {
      return;
    }
    var draft = opsDraft !== null ? opsDraft : model.savedOperational(current);
    var invalid = model.invalidOperationalFields(draft, current.operational);
    Object.keys(OPS_INPUTS).forEach(function (name) {
      var input = byId(OPS_INPUTS[name]);
      var bad = invalid.indexOf(name) >= 0;
      input.setAttribute('aria-invalid', bad ? 'true' : 'false');
      // The error text is part of the description only while it applies.
      input.setAttribute('aria-describedby', OPS_INPUTS[name] + '-range' + (bad ? ' ops-invalid' : ''));
    });
    var dirty = opsDraft !== null;
    byId('ops-invalid').hidden = invalid.length === 0;
    byId('ops-unsaved').hidden = !dirty;
    byId('ops-save').setAttribute('data-enabled', dirty && invalid.length === 0 ? 'true' : 'false');
    byId('ops-discard').setAttribute('data-enabled', dirty ? 'true' : 'false');
  }

  function onOpsEdited() {
    if (!current) {
      return;
    }
    var draft = readOpsForm();
    opsDraft = model.sameOperational(draft, model.savedOperational(current)) ? null : draft;
    renderOpsState();
    renderButtons();
  }

  /** Applies enabled/busy state to every command button from host truth plus this page's own pending requests. */
  function renderButtons() {
    if (!current) {
      return;
    }
    var engine = model.engineToggle(current);
    var engineButton = byId('action-engine');
    engineButton.setAttribute('data-command', engine.command);
    engineButton.setAttribute('data-busy', 'engine');
    setText(engineButton, model.text(current, engine.labelKey));
    engineButton.setAttribute('data-enabled', engine.enabled ? 'true' : 'false');

    var saveEnabled = byId('printer-select').value !== '' && byId('printer-select').value !== (current.printer.name || '');
    byId('printer-save').setAttribute('data-enabled', saveEnabled ? 'true' : 'false');

    var buttons = document.querySelectorAll('button[data-command], #printer-save, #ops-save, #ops-discard');
    for (var i = 0; i < buttons.length; i += 1) {
      var button = buttons[i];
      var action = button.getAttribute('data-action');
      var enabledAttr = button.getAttribute('data-enabled');
      var enabled = enabledAttr !== null ? enabledAttr === 'true' : (action ? model.flag(current, 'actions', action) : true);
      var group = button.getAttribute('data-busy');
      var busy = group ? isBusy(group) : false;
      button.setAttribute('aria-disabled', !enabled || busy ? 'true' : 'false');
      if (busy) {
        // The spinner is decorative; the description tells assistive technology the host is still working.
        button.setAttribute('aria-busy', 'true');
        button.setAttribute('aria-describedby', 'busy-hint');
      } else {
        button.removeAttribute('aria-busy');
        button.removeAttribute('aria-describedby');
      }
    }
  }

  function renderSnapshot(snapshot, previous) {
    document.documentElement.lang = snapshot.culture;
    document.documentElement.dir = snapshot.direction;
    renderStrings(snapshot);
    renderLanguages(snapshot);
    renderConnection(snapshot);
    renderHeroActions(snapshot);
    renderOverview(snapshot);
    renderPrinter(snapshot);
    renderSettings(snapshot);
    renderButtons();
    if (history.result) {
      renderHistory(history.result);
    }
    byId('app').setAttribute('aria-busy', 'false');
    if (model.shouldAnnounce(previous, snapshot)) {
      setText(byId('announcer'), model.announcement(snapshot));
    }
    if (activeTab === 'history' && model.historyChanged(previous, snapshot)) {
      queryHistory();
    }
  }

  // ---- history -----------------------------------------------------------------------------------------

  function queryHistory() {
    if (!current) {
      return;
    }
    history.requestId = model.newRequestId();
    byId('history-wrap').setAttribute('aria-busy', 'true');
    byId('history-loading').hidden = history.result !== null;
    send('history.query', { requestId: history.requestId, range: history.range, page: history.page, search: history.search });
  }

  function cell(row, value, className) {
    var td = document.createElement('td');
    if (className) {
      td.className = className;
    }
    td.textContent = value;
    row.appendChild(td);
    return td;
  }

  function reprintControls(td, item) {
    var wrap = document.createElement('div');
    wrap.className = 'pb-row-actions';
    var confirming = history.confirming === item.ref;
    var busy = isBusy('reprint');
    if (!confirming) {
      var reprint = document.createElement('button');
      reprint.type = 'button';
      reprint.className = 'pb-button pb-button--small';
      reprint.textContent = model.text(current, 'Reprint.Button');
      reprint.setAttribute('data-ref', item.ref);
      reprint.setAttribute('data-role', 'reprint');
      reprint.setAttribute('aria-disabled', busy ? 'true' : 'false');
      reprint.addEventListener('click', function () {
        if (reprint.getAttribute('aria-disabled') === 'true') {
          return;
        }
        history.confirming = item.ref;
        renderHistory(history.result);
        var confirm = document.querySelector('[data-ref="' + item.ref + '"][data-role="confirm"]');
        if (confirm) {
          confirm.focus();
        }
      });
      wrap.appendChild(reprint);
    } else {
      var question = document.createElement('span');
      question.className = 'pb-row-question';
      question.textContent = model.text(current, 'Shell.History.ConfirmReprint');
      wrap.appendChild(question);
      var yes = document.createElement('button');
      yes.type = 'button';
      yes.className = 'pb-button pb-button--small pb-button--primary';
      yes.textContent = model.text(current, 'Shell.Action.Confirm');
      yes.setAttribute('data-ref', item.ref);
      yes.setAttribute('data-role', 'confirm');
      yes.setAttribute('aria-disabled', busy ? 'true' : 'false');
      yes.addEventListener('click', function () {
        if (yes.getAttribute('aria-disabled') === 'true') {
          return;
        }
        history.confirming = null;
        sendTracked('history.reprint', { itemRef: item.ref });
        renderHistory(history.result);
        var back = document.querySelector('[data-ref="' + item.ref + '"][data-role="reprint"]');
        if (back) {
          back.focus();
        }
      });
      var no = document.createElement('button');
      no.type = 'button';
      no.className = 'pb-button pb-button--small';
      no.textContent = model.text(current, 'Shell.Action.Cancel');
      no.setAttribute('data-ref', item.ref);
      no.setAttribute('data-role', 'cancel');
      no.addEventListener('click', function () {
        history.confirming = null;
        renderHistory(history.result);
        var back = document.querySelector('[data-ref="' + item.ref + '"][data-role="reprint"]');
        if (back) {
          back.focus();
        }
      });
      wrap.appendChild(yes);
      wrap.appendChild(no);
    }
    td.appendChild(wrap);
  }

  function renderHistory(result) {
    if (!current) {
      return;
    }
    var page = result.history;
    var focused = document.activeElement;
    var focusRef = focused && focused.getAttribute ? focused.getAttribute('data-ref') : null;
    var focusRole = focused && focused.getAttribute ? focused.getAttribute('data-role') : null;

    var body = byId('history-rows');
    while (body.firstChild) {
      body.removeChild(body.firstChild);
    }
    page.items.forEach(function (item) {
      var row = document.createElement('tr');
      row.setAttribute('data-status', item.status);
      cell(row, item.time, 'pb-number pb-nowrap');
      var order = cell(row, item.order, 'pb-strong');
      if (item.detail) {
        var detail = document.createElement('span');
        detail.className = 'pb-row-detail';
        detail.textContent = item.detail;
        order.appendChild(detail);
      }
      cell(row, item.platform, 'pb-col-optional');
      cell(row, item.printer, 'pb-col-optional');
      var status = cell(row, '', '');
      var badge = document.createElement('span');
      badge.className = 'pb-badge';
      badge.textContent = item.statusLabel;
      badge.setAttribute('data-tone', model.jobTone(item.status));
      status.appendChild(badge);
      if (item.canReprint) {
        reprintControls(status, item);
      }
      body.appendChild(row);
    });

    var empty = page.items.length === 0;
    byId('history-table').hidden = empty;
    byId('history-empty').hidden = !empty;
    setText(byId('history-empty'), model.text(current, page.filtered ? 'PrintHistory.EmptyFiltered' : 'PrintHistory.Empty'));
    byId('history-loading').hidden = true;
    byId('history-wrap').setAttribute('aria-busy', 'false');
    setText(byId('history-page'), page.total > 0 ? page.pageLabel : '');
    byId('history-previous').setAttribute('aria-disabled', page.hasPrevious ? 'false' : 'true');
    byId('history-next').setAttribute('aria-disabled', page.hasNext ? 'false' : 'true');

    if (focusRef) {
      var again = body.querySelector('[data-ref="' + focusRef + '"][data-role="' + focusRole + '"]')
        || body.querySelector('[data-ref="' + focusRef + '"]');
      if (again) {
        again.focus();
      }
    }
  }

  // ---- operation results ---------------------------------------------------------------------------------

  function showToast(message, tone) {
    if (!message) {
      return;
    }
    var toast = byId('toast');
    toast.setAttribute('data-tone', tone);
    setText(byId('toast-text'), message);
    toast.hidden = false;
    setText(byId(tone === 'error' ? 'alert' : 'announcer'), message);
    if (toastTimer) {
      clearTimeout(toastTimer);
    }
    toastTimer = setTimeout(function () { toast.hidden = true; }, TOAST_MS);
  }

  function handleOperation(result) {
    Object.keys(pending).forEach(function (group) {
      if (pending[group] === result.requestId) {
        delete pending[group];
      }
    });
    if (result.operation === 'printer.save' && result.outcome === 'succeeded') {
      printerDraft = null;
    }
    if (result.operation === 'history.reprint' && activeTab === 'history') {
      queryHistory();
    }
    renderButtons();
    if (history.result) {
      renderHistory(history.result);
    }
    showToast(result.message, model.outcomeTone(result.outcome));
  }

  // ---- navigation ----------------------------------------------------------------------------------------

  function tabButton(name) {
    return byId('tab-' + name);
  }

  function activateTab(name, moveFocus) {
    activeTab = name;
    model.TABS.forEach(function (tab) {
      var selected = tab === name;
      var button = tabButton(tab);
      button.setAttribute('aria-selected', selected ? 'true' : 'false');
      button.tabIndex = selected ? 0 : -1;
      byId('panel-' + tab).hidden = !selected;
    });
    if (moveFocus) {
      tabButton(name).focus();
    }
    if (name === 'history') {
      queryHistory();
    }
  }

  function wireTabs() {
    model.TABS.forEach(function (tab, index) {
      var button = tabButton(tab);
      button.addEventListener('click', function () { activateTab(tab, false); });
      button.addEventListener('keydown', function (event) {
        var next = model.nextTabIndex(index, event.key, model.TABS.length, document.documentElement.dir === 'rtl');
        if (next < 0) {
          return;
        }
        event.preventDefault();
        activateTab(model.TABS[next], true);
      });
    });
  }

  function wireCommands() {
    var buttons = document.querySelectorAll('button[data-command]');
    for (var i = 0; i < buttons.length; i += 1) {
      (function (button) {
        button.addEventListener('click', function () {
          if (button.getAttribute('aria-disabled') === 'true') {
            return;
          }
          sendTracked(button.getAttribute('data-command'));
        });
      })(buttons[i]);
    }

    byId('printer-save').addEventListener('click', function (event) {
      if (event.currentTarget.getAttribute('aria-disabled') === 'true') {
        return;
      }
      sendTracked('printer.save', { name: byId('printer-select').value });
    });
    byId('printer-select').addEventListener('change', function (event) {
      printerDraft = event.target.value;
      renderButtons();
    });
    byId('open-classic').addEventListener('click', function () { send('classicWindow.open'); });

    ['ops-test-mode', OPS_INPUTS.idle, OPS_INPUTS.busy, OPS_INPUTS.error].forEach(function (id) {
      byId(id).addEventListener('input', onOpsEdited);
      byId(id).addEventListener('change', onOpsEdited);
    });
    byId('ops-save').addEventListener('click', function (event) {
      if (event.currentTarget.getAttribute('aria-disabled') === 'true' || opsDraft === null) {
        return;
      }
      // Turning test mode on is confirmed by the host in a native dialog; the page cannot confirm it.
      sendTracked('settings.save', model.operationalPayload(opsDraft));
    });
    byId('ops-discard').addEventListener('click', function (event) {
      if (event.currentTarget.getAttribute('aria-disabled') === 'true') {
        return;
      }
      opsDraft = null;
      renderOperational(current);
      renderButtons();
      byId('ops-test-mode').focus();
    });
    byId('toast-close').addEventListener('click', function () { byId('toast').hidden = true; });

    ['language', 'settings-language'].forEach(function (id) {
      byId(id).addEventListener('change', function (event) {
        var culture = event.target.value;
        if (!current || languageRequestPending || culture === current.culture) {
          return;
        }
        // The host answers with a snapshot in the new (or, if saving failed, the old) language.
        // The picker stays enabled so keyboard focus is not lost while the host applies it.
        languageRequestPending = true;
        event.target.setAttribute('aria-busy', 'true');
        send('language.change', { culture: culture });
      });
    });

    byId('history-range').addEventListener('change', function (event) {
      history.range = event.target.value;
      history.page = 0;
      queryHistory();
    });
    byId('history-search').addEventListener('input', function (event) {
      history.search = event.target.value.slice(0, 64);
      history.page = 0;
      if (searchTimer) {
        clearTimeout(searchTimer);
      }
      searchTimer = setTimeout(queryHistory, SEARCH_DEBOUNCE_MS);
    });
    byId('history-refresh').addEventListener('click', queryHistory);
    byId('history-previous').addEventListener('click', function (event) {
      if (event.currentTarget.getAttribute('aria-disabled') === 'true') {
        return;
      }
      history.page = Math.max(0, history.page - 1);
      queryHistory();
    });
    byId('history-next').addEventListener('click', function (event) {
      if (event.currentTarget.getAttribute('aria-disabled') === 'true') {
        return;
      }
      history.page += 1;
      queryHistory();
    });
  }

  webview.addEventListener('message', function (event) {
    var message = model.readHostMessage(event.data, lastSequence);
    if (!message) {
      return;
    }
    lastSequence = message.sequence;
    if (message.kind === 'snapshot') {
      var previous = current;
      current = message.payload;
      renderSnapshot(current, previous);
    } else if (message.kind === 'operation') {
      handleOperation(message.payload);
    } else if (message.kind === 'history' && message.payload.requestId === history.requestId) {
      history.result = message.payload;
      history.page = message.payload.history.page;
      renderHistory(history.result);
    } else if (message.kind === 'navigate') {
      // The tray's Print history and Settings, or the Printer tab after connecting without a printer.
      activateTab(message.payload.tab, true);
    }
  });

  // Block drag-and-drop of files or links onto the page; the host also disables external drops.
  ['dragover', 'drop'].forEach(function (name) {
    window.addEventListener(name, function (event) {
      event.preventDefault();
    });
  });

  wireTabs();
  wireCommands();
  send('ui.ready');
})();
