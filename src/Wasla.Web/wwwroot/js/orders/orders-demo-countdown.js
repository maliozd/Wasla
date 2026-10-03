// Live Screen practice-order countdown (guided order training only). The server sends, on the user's own practice
// order, the automatic step waiting on it and its deadline (demoAutomation: PickUp, Deliver or Leave; dueAtUtc;
// durationSeconds), from the same rule Wasla.Worker uses to move it. This script only shows how long is left, inside
// that order's card: it measures the deadline against the snapshot's serverTimeUtc (so the browser's clock offset does
// not matter), never restarts or lengthens a stage on a repeated or stale snapshot, and at zero says it is waiting for
// the platform. It changes no status, raises no event and never touches real orders. When a stage's deadline passes
// it asks the Live Screen store for its ordinary read-only snapshot (requestRefresh, the same GET /orders/live-data the
// store polls, coalesced with it), a few times at most, so the Worker's change shows within about a second instead of
// waiting for the next 10-second poll.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && root.WaslaOrders && !root.WaslaDemoCountdown) {
    root.WaslaDemoCountdown = api;
    api.boot(root.document, root, root.WaslaOrders);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  var TICK_MS = 1000;
  /** First read-only refresh after a deadline: the Worker moves the practice order at the deadline itself. */
  var DEADLINE_REFRESH_DELAY_MS = 400;
  /** Then once a second while the stage has not moved yet ... */
  var REFRESH_RETRY_MS = 1000;
  /** ... at most this many times per stage; afterwards the ordinary poll goes on and the card keeps waiting. */
  var MAX_DEADLINE_REFRESHES = 8;
  var CARD_SELECTOR = "article[data-wasla-demo-card]";
  var NODE_SELECTOR = "[data-demo-countdown]";
  /** Automatic stages in the order they happen; a snapshot showing an earlier one is stale. */
  var STAGE_RANK = { ReadyForPickup: 1, OnTheWay: 2, Delivered: 3 };
  var STAGE_CLASS = { ReadyForPickup: "ready", OnTheWay: "on-the-way", Delivered: "delivered" };
  var ACTION_MESSAGE = { PickUp: "demoCountdownPickUp", Deliver: "demoCountdownDeliver", Leave: "demoCountdownLeave" };
  var GUID = /^[0-9a-fA-F-]{36}$/;

  /** Milliseconds since the epoch for a server UTC timestamp; a value without a zone is UTC too. */
  function parseUtc(value) {
    if (typeof value !== "string" || !value) return NaN;
    var text = /[zZ]$|[+-]\d\d:?\d\d$/.test(value) ? value : value + "Z";
    return Date.parse(text);
  }

  /**
   * The countdown a snapshot asks for on one order, with its deadline moved onto this browser's clock:
   * dueLocalMs = (the moment the snapshot arrived) + (dueAtUtc − serverTimeUtc). Null for anything that is not the
   * user's own practice order in an automatic stage.
   */
  function plan(order, serverTimeUtc, receivedLocalMs) {
    if (!order || order.isDemo !== true || !order.demoAutomation) return null;
    var automation = order.demoAutomation;
    var id = String(order.id || "");
    if (!GUID.test(id) || !STAGE_RANK[order.status] || !ACTION_MESSAGE[automation.action]) return null;
    var due = parseUtc(automation.dueAtUtc);
    var server = parseUtc(serverTimeUtc);
    var durationMs = Number(automation.durationSeconds) * 1000;
    if (!isFinite(due) || !isFinite(server) || !isFinite(receivedLocalMs) || !(durationMs > 0)) return null;
    var dueLocalMs = receivedLocalMs + (due - server);
    return {
      id: id,
      status: order.status,
      rank: STAGE_RANK[order.status],
      action: automation.action,
      durationMs: durationMs,
      dueLocalMs: dueLocalMs,
      refreshes: 0,
      nextRefreshMs: dueLocalMs + DEADLINE_REFRESH_DELAY_MS
    };
  }

  /**
   * A later stage replaces the countdown; an earlier one (a stale snapshot) is ignored; the same stage keeps the
   * earliest deadline seen, so a repeated poll or a slower response never restarts or lengthens it.
   */
  function merge(current, next) {
    if (!current || next.rank > current.rank) return next;
    if (next.rank < current.rank || next.dueLocalMs >= current.dueLocalMs) return current;
    // An earlier deadline for the same stage (a faster response): keep the refreshes already spent on it.
    if (current.refreshes > 0) {
      next.refreshes = current.refreshes;
      next.nextRefreshMs = current.nextRefreshMs;
    }
    return next;
  }

  function remainingMs(entry, nowMs) {
    return Math.max(0, Math.min(entry.durationMs, entry.dueLocalMs - nowMs));
  }

  function format(template, value) {
    return String(template || "").split("{0}").join(String(value));
  }

  /**
   * One countdown controller per page. Options: document, now() (ms), setInterval, clearInterval, message(key), and
   * for deadline refreshes requestRefresh() (the store's read-only refresh; returns a promise), setTimeout, clearTimeout.
   */
  function createController(options) {
    var doc = options.document;
    var now = options.now;
    var message = options.message;
    var entries = {};
    var timer = null;
    var suspended = false;
    var hidden = false;
    var disposed = false;
    var sequence = 0;
    var refreshTimer = null;
    var refreshing = false;

    function each(fn) {
      for (var id in entries) if (Object.prototype.hasOwnProperty.call(entries, id)) fn(entries[id], id);
    }

    function count() {
      var n = 0;
      each(function () { n += 1; });
      return n;
    }

    /** A new accepted snapshot: update the plans, then the cards. */
    function update(detail) {
      if (disposed || !detail) return;
      var receivedAt = now();
      var orders = Array.isArray(detail.orders) ? detail.orders : [];
      var seen = {};
      for (var i = 0; i < orders.length; i++) {
        var next = plan(orders[i], detail.serverTimeUtc, receivedAt);
        if (!next) continue;
        seen[next.id] = true;
        entries[next.id] = merge(entries[next.id], next);
      }
      // A practice order that is no longer in an automatic stage, or no longer on the screen, loses its countdown.
      each(function (entry, id) { if (!seen[id]) delete entries[id]; });
      render();
      armRefresh();
    }

    function canRefresh() {
      return !disposed && !suspended && !hidden && typeof options.requestRefresh === "function"
        && typeof options.setTimeout === "function";
    }

    function clearRefreshTimer() {
      if (refreshTimer !== null) options.clearTimeout(refreshTimer);
      refreshTimer = null;
    }

    /**
     * One timer for the earliest stage whose deadline has passed (or soon will) and still has refreshes left. None
     * while a refresh is running: the next one waits for it, so two never overlap.
     */
    function armRefresh() {
      clearRefreshTimer();
      if (refreshing || !canRefresh()) return;
      var at = Infinity;
      each(function (entry) {
        if (entry.refreshes < MAX_DEADLINE_REFRESHES && entry.nextRefreshMs < at) at = entry.nextRefreshMs;
      });
      if (at === Infinity) return;
      refreshTimer = options.setTimeout(fireRefresh, Math.max(0, at - now()));
    }

    function fireRefresh() {
      refreshTimer = null;
      if (refreshing || !canRefresh()) return;
      var t = now();
      var due = false;
      each(function (entry) {
        if (entry.refreshes < MAX_DEADLINE_REFRESHES && entry.nextRefreshMs <= t) {
          entry.refreshes += 1;
          entry.nextRefreshMs = t + REFRESH_RETRY_MS;
          due = true;
        }
      });
      if (!due) {
        armRefresh();
        return;
      }
      refreshing = true;
      var pending;
      try {
        pending = options.requestRefresh();
      } catch (error) {
        pending = null;
      }
      Promise.resolve(pending).then(settled, settled);
    }

    function settled() {
      refreshing = false;
      armRefresh();
    }

    function build(entry) {
      var key = ++sequence;
      var box = doc.createElement("div");
      box.setAttribute("data-demo-countdown", entry.id);
      box.title = message("demoCountdownHint");

      var text = doc.createElement("p");
      text.className = "wasla-demo-countdown__text";
      text.id = "waslaDemoCountdownText" + key;
      text.setAttribute("data-demo-countdown-text", "");

      var hint = doc.createElement("span");
      hint.className = "visually-hidden";
      hint.id = "waslaDemoCountdownHint" + key;
      hint.textContent = message("demoCountdownHint");

      var row = doc.createElement("div");
      row.className = "wasla-demo-countdown__row";
      var bar = doc.createElement("div");
      bar.className = "wasla-demo-countdown__bar";
      bar.setAttribute("role", "progressbar");
      bar.setAttribute("aria-valuemin", "0");
      bar.setAttribute("aria-label", message("demoCountdownLabel"));
      bar.setAttribute("aria-describedby", text.id + " " + hint.id);
      bar.setAttribute("data-demo-countdown-bar", "");
      var fill = doc.createElement("span");
      fill.className = "wasla-demo-countdown__fill";
      fill.setAttribute("data-demo-countdown-fill", "");
      bar.appendChild(fill);
      var seconds = doc.createElement("span");
      seconds.className = "wasla-demo-countdown__seconds";
      seconds.setAttribute("aria-hidden", "true");
      seconds.setAttribute("data-demo-countdown-seconds", "");
      row.appendChild(bar);
      row.appendChild(seconds);

      box.appendChild(text);
      box.appendChild(row);
      box.appendChild(hint);
      return box;
    }

    /** Right below the card's status row, so it reads with the status it explains. */
    function place(card, box) {
      var badge = card.querySelector("[data-status-badge]");
      var row = badge ? badge.parentNode : null;
      if (row && row.parentNode) row.parentNode.insertBefore(box, row.nextSibling);
      else card.appendChild(box);
    }

    function set(el, attribute, value) {
      if (el && el.getAttribute(attribute) !== value) el.setAttribute(attribute, value);
    }

    function setText(el, value) {
      if (el && el.textContent !== value) el.textContent = value;
    }

    function paint(box, entry, leftMs) {
      var totalSeconds = Math.round(entry.durationMs / 1000);
      var seconds = Math.ceil(leftMs / 1000);
      var waiting = leftMs <= 0;
      var percent = Math.max(0, Math.min(100, (leftMs / entry.durationMs) * 100));
      set(box, "class", "wasla-demo-countdown wasla-demo-countdown--" + STAGE_CLASS[entry.status] + (waiting ? " is-waiting" : ""));
      set(box, "data-demo-countdown-status", entry.status);
      set(box, "data-demo-countdown-action", entry.action);

      var text = box.querySelector("[data-demo-countdown-text]");
      var bar = box.querySelector("[data-demo-countdown-bar]");
      var fill = box.querySelector("[data-demo-countdown-fill]");
      var label = box.querySelector("[data-demo-countdown-seconds]");
      var secondsText = format(message("demoCountdownSeconds"), seconds);
      setText(text, waiting ? message("demoCountdownWaiting") : format(message(ACTION_MESSAGE[entry.action]), seconds));
      setText(label, waiting ? "" : secondsText);
      if (label) label.hidden = waiting;
      set(bar, "aria-valuemax", String(totalSeconds));
      set(bar, "aria-valuenow", String(waiting ? 0 : seconds));
      set(bar, "aria-valuetext", waiting ? message("demoCountdownWaiting") : secondsText);
      if (fill && fill.style) fill.style.inlineSize = percent.toFixed(2) + "%";
    }

    /** Draws every countdown from the current time; keeps ticking only while one still counts down. */
    function render() {
      if (disposed) return;
      var t = now();
      var nodes = doc.querySelectorAll(NODE_SELECTOR);
      for (var n = 0; n < nodes.length; n++) {
        var owner = entries[nodes[n].getAttribute("data-demo-countdown")];
        var card = nodes[n].parentNode ? closestCard(nodes[n]) : null;
        if (!owner || !card || card.getAttribute("data-order-status") !== owner.status) remove(nodes[n]);
      }
      var running = false;
      each(function (entry) {
        var left = remainingMs(entry, t);
        if (left > 0) running = true;
        var cards = doc.querySelectorAll(CARD_SELECTOR + "[data-order-id=\"" + entry.id + "\"]");
        for (var c = 0; c < cards.length; c++) {
          if (cards[c].getAttribute("data-order-status") !== entry.status) continue;
          var box = cards[c].querySelector(NODE_SELECTOR);
          if (!box) {
            box = build(entry);
            place(cards[c], box);
          }
          paint(box, entry, left);
        }
      });
      schedule(running);
    }

    function closestCard(node) {
      if (typeof node.closest === "function") return node.closest(CARD_SELECTOR);
      for (var el = node.parentNode; el; el = el.parentNode) {
        if (typeof el.matches === "function" && el.matches(CARD_SELECTOR)) return el;
      }
      return null;
    }

    function remove(node) {
      if (node && node.parentNode) node.parentNode.removeChild(node);
    }

    function schedule(running) {
      if (running && !suspended && !hidden && !disposed) {
        if (timer === null) timer = options.setInterval(render, TICK_MS);
      } else if (timer !== null) {
        options.clearInterval(timer);
        timer = null;
      }
    }

    return {
      update: update,
      /** Recalculates from the deadline, e.g. when a hidden tab becomes visible again. */
      refresh: function () { render(); armRefresh(); },
      /** The tab is hidden: no ticking and no deadline refresh until it is visible again. */
      setHidden: function (value) {
        hidden = !!value;
        if (hidden) {
          schedule(false);
          clearRefreshTimer();
        } else {
          render();
          armRefresh();
        }
      },
      /** The page is being hidden or put in the back/forward cache: stop ticking and refreshing. */
      suspend: function () { suspended = true; schedule(false); clearRefreshTimer(); },
      resume: function () { suspended = false; render(); armRefresh(); },
      /** Removes every countdown and timer; the controller does nothing afterwards. */
      dispose: function () {
        schedule(false);
        clearRefreshTimer();
        disposed = true;
        entries = {};
        var nodes = doc.querySelectorAll(NODE_SELECTOR);
        for (var i = 0; i < nodes.length; i++) remove(nodes[i]);
      },
      isTicking: function () { return timer !== null; },
      isRefreshPending: function () { return refreshTimer !== null || refreshing; },
      size: count
    };
  }

  function boot(doc, win, O) {
    var controller = createController({
      document: doc,
      now: function () { return Date.now(); },
      setInterval: function (fn, ms) { return win.setInterval(fn, ms); },
      clearInterval: function (id) { win.clearInterval(id); },
      message: function (key) { return O.getMessage(key); },
      setTimeout: function (fn, ms) { return win.setTimeout(fn, ms); },
      clearTimeout: function (id) { win.clearTimeout(id); },
      requestRefresh: function () {
        var live = O.liveStore;
        return live && typeof live.requestRefresh === "function" ? live.requestRefresh() : null;
      }
    });
    doc.addEventListener("wasla:live-rendered", function (event) {
      controller.update(event && event.detail ? event.detail : null);
    });
    doc.addEventListener("visibilitychange", function () {
      controller.setHidden(!!doc.hidden);
    });
    if (doc.hidden) controller.setHidden(true);
    win.addEventListener("pagehide", function () { controller.suspend(); });
    win.addEventListener("pageshow", function () { controller.resume(); });
    return controller;
  }

  return {
    TICK_MS: TICK_MS,
    DEADLINE_REFRESH_DELAY_MS: DEADLINE_REFRESH_DELAY_MS,
    REFRESH_RETRY_MS: REFRESH_RETRY_MS,
    MAX_DEADLINE_REFRESHES: MAX_DEADLINE_REFRESHES,
    parseUtc: parseUtc,
    plan: plan,
    merge: merge,
    remainingMs: remainingMs,
    createController: createController,
    boot: boot
  };
});
