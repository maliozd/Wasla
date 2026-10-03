(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && !root.WaslaGuidedSetup) {
    root.WaslaGuidedSetup = api;
    api.bind(root.document, root);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  // Guided-setup forms post once. State rules live on the server; this only prevents double clicks.
  var FORM_SELECTOR = "form[data-guided-setup-form]";

  function isGuidedForm(form) {
    return !!(form && typeof form.matches === "function" && form.matches(FORM_SELECTOR));
  }

  function setBusy(form, busy) {
    if (busy) form.setAttribute("data-guided-setup-submitting", "true");
    else form.removeAttribute("data-guided-setup-submitting");
    form.setAttribute("aria-busy", busy ? "true" : "false");
    var buttons = form.querySelectorAll("button[type='submit'], button:not([type])");
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = busy;
  }

  /** Returns false (and cancels the event) when the form is already submitting. */
  function handleSubmit(event) {
    var form = event.target;
    if (!isGuidedForm(form)) return true;
    if (form.getAttribute("data-guided-setup-submitting") === "true") {
      event.preventDefault();
      return false;
    }
    setBusy(form, true);
    return true;
  }

  // A connected Print Bridge section offers its device guide in two places: in the panel and in the panel's part that
  // is fixed to the viewport. Both links carry data-guided-setup-continue and are the same read-only GET (the server
  // picks the page; nothing moves on). The first activation of either one makes both wait for that one navigation, so
  // another click on either adds no request. A page restored from the back/forward cache, or a navigation that never
  // happened (stopped, or blocked), makes them usable again.
  var CONTINUE_SELECTOR = "a[data-guided-setup-continue]";
  var CONTINUE_RESTORE_MS = 10000;
  var continueTimer = null;

  function setContinueBusy(doc, busy) {
    if (!doc || typeof doc.querySelectorAll !== "function") return;
    var links = doc.querySelectorAll(CONTINUE_SELECTOR);
    for (var i = 0; i < links.length; i++) {
      if (busy) {
        links[i].setAttribute("data-guided-setup-navigating", "true");
        links[i].setAttribute("aria-disabled", "true");
      } else {
        links[i].removeAttribute("data-guided-setup-navigating");
        links[i].removeAttribute("aria-disabled");
      }
    }
  }

  function continueLinkFor(target) {
    if (!target) return null;
    if (typeof target.closest === "function") return target.closest(CONTINUE_SELECTOR);
    return typeof target.matches === "function" && target.matches(CONTINUE_SELECTOR) ? target : null;
  }

  /** Returns false (and cancels the click) while either continuation link is already opening the device guide. */
  function handleContinueClick(event, doc, win) {
    var link = continueLinkFor(event && event.target);
    if (!link || event.defaultPrevented) return true;
    // Opening it in a new tab or window is the user's own choice: it neither waits nor blocks this page.
    if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return true;
    if (link.getAttribute("data-guided-setup-navigating") === "true") {
      event.preventDefault();
      return false;
    }
    setContinueBusy(doc, true);
    if (win && typeof win.setTimeout === "function") {
      if (continueTimer !== null && typeof win.clearTimeout === "function") win.clearTimeout(continueTimer);
      continueTimer = win.setTimeout(function () {
        continueTimer = null;
        setContinueBusy(doc, false);
      }, CONTINUE_RESTORE_MS);
    }
    return true;
  }

  /** A page restored from the back/forward cache must be usable again. */
  function resetAll(doc) {
    var forms = doc.querySelectorAll(FORM_SELECTOR);
    for (var i = 0; i < forms.length; i++) setBusy(forms[i], false);
    setContinueBusy(doc, false);
  }

  /** The End dialog opens on its safe choice; Bootstrap returns focus to the trigger on close. */
  function focusSafeChoice(event) {
    var dialog = event.target;
    if (!dialog || typeof dialog.hasAttribute !== "function" || !dialog.hasAttribute("data-guided-setup-dialog")) return;
    var cancel = dialog.querySelector("[data-guided-setup-cancel]");
    if (cancel && typeof cancel.focus === "function") cancel.focus();
  }

  // A setup panel whose page finishes the setup in place (Print Bridge pairing) re-reads its section's
  // readiness when the page reports success, so the panel shows the connected state and its Continue button
  // without a reload. It is event-driven, not a new poll: the page's own setup flow raises the event, and the
  // panel checks a few times while the device's first heartbeat lands. It only reads (GET); it never moves
  // the journey on, and it never redirects by itself.
  var PANEL_SELECTOR = "[data-guided-setup-panel][data-guided-setup-refresh-on]";
  var RECHECK_INTERVAL_MS = 2000;
  var RECHECK_ATTEMPTS = 5;

  // A manual Print Bridge connection (the Web Panel URL and a device token pasted into the app) has no success
  // message of its own: only the device's first authenticated heartbeat proves it. When the page issues a token
  // it raises the panel's watch event, and the panel re-reads the same read-only readiness every
  // RECHECK_INTERVAL_MS until the server confirms it, the section is no longer the user's current one, the page
  // is hidden, or WATCH_ATTEMPTS run out (about 15 minutes). A panel never has more than one watch.
  var WATCH_SELECTOR = "[data-guided-setup-panel][data-guided-setup-watch-on]";
  var WATCH_ATTEMPTS = 450;
  var STATE_CHANGED_EVENT = "wasla:guided-setup-state-changed";
  var WATCH_ENDED_EVENT = "wasla:guided-setup-watch-ended";

  /** "ready" / "not-ready" for a status response, or null when the section is no longer the user's current one. */
  function panelStateFor(status) {
    if (!status || status.current !== true) return null;
    return status.ready === true ? "ready" : "not-ready";
  }

  /** Shows the elements for <state> and hides the others; returns true when the state changed. */
  function applyPanelState(panel, state) {
    if (!panel || (state !== "ready" && state !== "not-ready")) return false;
    var changed = panel.getAttribute("data-guided-setup-state") !== state;
    panel.setAttribute("data-guided-setup-state", state);
    if (panel.classList) panel.classList.toggle("is-ready", state === "ready");
    var parts = panel.querySelectorAll("[data-guided-setup-when]");
    for (var i = 0; i < parts.length; i++) parts[i].hidden = parts[i].getAttribute("data-guided-setup-when") !== state;
    return changed;
  }

  function notify(doc, win, type, panel, state) {
    if (!doc || typeof doc.dispatchEvent !== "function" || !win || typeof win.CustomEvent !== "function") return;
    doc.dispatchEvent(new win.CustomEvent(type, {
      detail: { section: panel.getAttribute("data-guided-setup-panel"), state: state }
    }));
  }

  /** Applies a state the server reported and tells the page once, only when it actually changed. */
  function showState(panel, state, doc, win) {
    if (applyPanelState(panel, state)) notify(doc, win, STATE_CHANGED_EVENT, panel, state);
  }

  function canRead(panel, win) {
    return !!(panel && panel.getAttribute("data-guided-setup-status-url") && win && typeof win.fetch === "function");
  }

  /** One read-only readiness read (GET): the section's status, or null when it could not be read. */
  function readStatus(panel, win) {
    var url = panel.getAttribute("data-guided-setup-status-url");
    return win.fetch(url, { headers: { Accept: "application/json" }, credentials: "same-origin", cache: "no-store" })
      .then(function (response) { return response.ok ? response.json() : null; })
      .catch(function () { return null; });
  }

  /**
   * Re-reads the panel's readiness up to <attempts> times, <interval> ms apart, and shows the connected
   * state as soon as the server confirms it. Stops early when ready or when the section is no longer the
   * user's current one. Resolves to the last state seen ("ready", "not-ready" or null).
   */
  function recheckPanel(panel, win, attempts, interval, doc) {
    if (!canRead(panel, win)) return Promise.resolve(null);
    var left = attempts;

    function once() {
      left -= 1;
      return readStatus(panel, win).then(function (status) {
        var state = panelStateFor(status);
        if (state) showState(panel, state, doc, win);
        if (state === "ready" || (status && status.current === false) || left <= 0) return state;
        return new Promise(function (resolve) { win.setTimeout(resolve, interval); }).then(once);
      });
    }

    return once();
  }

  var watches = [];

  function watchFor(panel) {
    for (var i = 0; i < watches.length; i++) if (watches[i].panel === panel) return watches[i];
    return null;
  }

  /**
   * Watches the panel's readiness after a manual connection was started (see WATCH_SELECTOR). Starting it again
   * while it runs returns the running watch. Returns null when there is nothing to watch.
   */
  function watchPanel(panel, doc, win, attempts, interval) {
    if (!canRead(panel, win)) return null;
    var running = watchFor(panel);
    if (running) return running;
    if (panel.getAttribute("data-guided-setup-state") === "ready") return null;

    var left = attempts;
    var watch = { panel: panel, timer: null, stopped: false };
    watch.stop = function () {
      if (watch.stopped) return;
      watch.stopped = true;
      if (watch.timer !== null && typeof win.clearTimeout === "function") win.clearTimeout(watch.timer);
      watch.timer = null;
      var at = watches.indexOf(watch);
      if (at >= 0) watches.splice(at, 1);
    };

    function step() {
      watch.timer = null;
      if (watch.stopped) return;
      if (panel.getAttribute("data-guided-setup-state") === "ready") { watch.stop(); return; }
      left -= 1;
      readStatus(panel, win).then(function (status) {
        if (watch.stopped) return;
        var state = panelStateFor(status);
        if (state) showState(panel, state, doc, win);
        if (state === "ready") { watch.stop(); return; }
        if ((status && status.current === false) || left <= 0) {
          watch.stop();
          notify(doc, win, WATCH_ENDED_EVENT, panel, panel.getAttribute("data-guided-setup-state"));
          return;
        }
        watch.timer = win.setTimeout(step, interval);
      });
    }

    watches.push(watch);
    step();
    return watch;
  }

  /** Stops every watch (the page is being hidden) and returns their panels, so a restored page can resume them. */
  function stopWatches() {
    var panels = [];
    while (watches.length) {
      var watch = watches[0];
      panels.push(watch.panel);
      watch.stop();
    }
    return panels;
  }

  function watchPanels(doc, win) {
    var panels = doc.querySelectorAll(PANEL_SELECTOR);
    for (var i = 0; i < panels.length; i++) {
      (function (panel) {
        var refreshOn = panel.getAttribute("data-guided-setup-refresh-on");
        if (!refreshOn) return;
        doc.addEventListener(refreshOn, function () {
          if (panel.getAttribute("data-guided-setup-state") === "ready") return;
          recheckPanel(panel, win, RECHECK_ATTEMPTS, RECHECK_INTERVAL_MS, doc);
        });
      })(panels[i]);
    }

    var watched = doc.querySelectorAll(WATCH_SELECTOR);
    for (var j = 0; j < watched.length; j++) {
      (function (panel) {
        var watchOn = panel.getAttribute("data-guided-setup-watch-on");
        if (!watchOn) return;
        doc.addEventListener(watchOn, function () {
          watchPanel(panel, doc, win, WATCH_ATTEMPTS, RECHECK_INTERVAL_MS);
        });
      })(watched[j]);
    }
  }

  var bound = false;

  function bind(doc, win) {
    if (bound || !doc || typeof doc.addEventListener !== "function") return;
    bound = true;
    doc.addEventListener("submit", handleSubmit);
    doc.addEventListener("click", function (event) { handleContinueClick(event, doc, win); });
    doc.addEventListener("shown.bs.modal", focusSafeChoice);
    if (win && typeof win.addEventListener === "function") {
      var suspended = [];
      win.addEventListener("pagehide", function () {
        suspended = stopWatches();
      });
      win.addEventListener("pageshow", function (event) {
        if (!event || !event.persisted) return;
        resetAll(doc);
        var resume = suspended;
        suspended = [];
        for (var i = 0; i < resume.length; i++) watchPanel(resume[i], doc, win, WATCH_ATTEMPTS, RECHECK_INTERVAL_MS);
      });
    }
    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", function () { watchPanels(doc, win); });
    else watchPanels(doc, win);
  }

  return {
    RECHECK_INTERVAL_MS: RECHECK_INTERVAL_MS,
    RECHECK_ATTEMPTS: RECHECK_ATTEMPTS,
    WATCH_ATTEMPTS: WATCH_ATTEMPTS,
    CONTINUE_RESTORE_MS: CONTINUE_RESTORE_MS,
    bind: bind,
    handleSubmit: handleSubmit,
    handleContinueClick: handleContinueClick,
    resetAll: resetAll,
    focusSafeChoice: focusSafeChoice,
    panelStateFor: panelStateFor,
    applyPanelState: applyPanelState,
    recheckPanel: recheckPanel,
    watchPanel: watchPanel,
    stopWatches: stopWatches,
    watchPanels: watchPanels
  };
});
