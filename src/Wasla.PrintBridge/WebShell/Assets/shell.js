/*
 * Wasla Print Bridge status shell: DOM wiring. Renders host snapshots with textContent only and sends
 * the four allowlisted commands. Nothing is stored in localStorage, sessionStorage or cookies.
 */
(function () {
  'use strict';

  var model = window.WaslaPrintBridgeShell;
  var webview = window.chrome && window.chrome.webview;
  if (!model || !webview) {
    return;
  }

  var lastSequence = 0;
  var current = null;
  var languageRequestPending = false;

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

  function renderStrings(snapshot) {
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i += 1) {
      setText(nodes[i], model.text(snapshot, nodes[i].getAttribute('data-i18n')));
    }
    document.title = model.text(snapshot, 'Common.AppTitle');
  }

  function renderLanguages(snapshot) {
    var select = byId('language');
    var signature = snapshot.languages.map(function (l) { return l.culture + '=' + l.nativeName; }).join('|');
    if (select.getAttribute('data-signature') !== signature) {
      while (select.firstChild) {
        select.removeChild(select.firstChild);
      }
      snapshot.languages.forEach(function (language) {
        var option = document.createElement('option');
        option.value = language.culture;
        option.lang = language.culture;
        option.textContent = language.nativeName;
        select.appendChild(option);
      });
      select.setAttribute('data-signature', signature);
    }
    // Always show the language the host actually applied.
    select.value = snapshot.culture;
    select.removeAttribute('aria-busy');
    languageRequestPending = false;
  }

  function renderConnection(snapshot) {
    byId('connection').setAttribute('data-state', snapshot.connection.state);
    setText(byId('connection-label'), snapshot.connection.label);
    setText(byId('connection-detail'), snapshot.connection.detail);
  }

  function renderCards(snapshot) {
    setText(byId('device-name'), model.orDash(snapshot, snapshot.device.name));
    byId('printer').setAttribute('data-state', snapshot.printer.state);
    setText(byId('printer-name'), model.orDash(snapshot, snapshot.printer.name));
    setText(byId('printer-state'), snapshot.printer.label);
    setText(byId('last-contact'), model.orDash(snapshot, snapshot.activity.lastContact));
    setText(byId('jobs-today'), String(snapshot.activity.jobsToday));
    setText(byId('failed-today'), String(snapshot.activity.failedToday));
    byId('failed-stat').setAttribute('data-alert', snapshot.activity.failedToday > 0 ? 'true' : 'false');
    setText(byId('last-print'), model.orDash(snapshot, snapshot.activity.lastPrint));
    byId('dry-run').hidden = !snapshot.dryRun;
  }

  function renderLastJob(snapshot) {
    var job = snapshot.lastJob;
    byId('last-job-empty').hidden = job !== null;
    byId('last-job-row').hidden = job === null;
    byId('last-job').setAttribute('data-status', job ? job.status : '');
    if (job) {
      setText(byId('last-job-order'), job.order);
      setText(byId('last-job-type'), job.typeLabel);
      setText(byId('last-job-time'), job.time);
      setText(byId('last-job-status'), job.statusLabel);
    }
  }

  function render(snapshot, previous) {
    document.documentElement.lang = snapshot.culture;
    document.documentElement.dir = snapshot.direction;
    renderStrings(snapshot);
    renderLanguages(snapshot);
    renderConnection(snapshot);
    renderCards(snapshot);
    renderLastJob(snapshot);
    setText(byId('version'), snapshot.versionLabel);
    byId('app').setAttribute('aria-busy', 'false');
    if (model.shouldAnnounce(previous, snapshot)) {
      setText(byId('announcer'), model.announcement(snapshot));
    }
  }

  webview.addEventListener('message', function (event) {
    var message = model.readHostMessage(event.data, lastSequence);
    if (!message) {
      return;
    }
    lastSequence = message.sequence;
    var previous = current;
    current = message.snapshot;
    render(current, previous);
  });

  byId('language').addEventListener('change', function (event) {
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

  byId('open-classic').addEventListener('click', function () {
    send('classicWindow.open');
  });

  // Block drag-and-drop of files or links onto the page; the host also disables external drops.
  ['dragover', 'drop'].forEach(function (name) {
    window.addEventListener(name, function (event) {
      event.preventDefault();
    });
  });

  send('ui.ready');
})();
