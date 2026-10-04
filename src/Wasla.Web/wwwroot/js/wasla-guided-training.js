// Order training on the Live Screen (guided setup, section "live-screen-demo").
// The server decides the starting step from the saved position and the practice order; this script only follows
// the practice order through Live Screen snapshots and points at it. Real orders never pause or move training:
// while the tenant is still in setup the server leaves them out of this user's snapshot and sends only how many
// arrived, which one quiet status line reports. It never changes journey state: Start practice and Complete are
// ordinary form posts, and the courier steps come from Wasla.Worker. Rendering a snapshot never sends a request.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && !root.WaslaGuidedTraining) {
    root.WaslaGuidedTraining = api;
    api.boot(root.document, root);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  var INTRO = "intro";
  var DELIVERED = "practice-delivered";

  /** Practice-order status → training step. Statuses training does not use map to nothing. */
  var STEP_FOR_STATUS = {
    New: "practice-new",
    Accepted: "practice-accepted",
    Preparing: "practice-preparing",
    ReadyForPickup: "practice-ready",
    OnTheWay: "practice-on-the-way",
    Delivered: DELIVERED
  };

  /** Steps where only the courier (simulated by Wasla.Worker) can move the order. */
  var COURIER_STEPS = { "practice-ready": true, "practice-on-the-way": true };

  /**
   * How long a courier step may last before the page explains that updates come from the background
   * service. Well above the Worker's cycle plus its courier delay, so it only shows when it is paused.
   */
  var WAITING_HINT_AFTER_MS = 60000;

  var TARGET_CLASS = "wasla-guided-training-target";
  var ACTION_CLASS = "wasla-guided-training-target--action";

  function indexOf(steps, step) {
    for (var i = 0; i < steps.length; i++) if (steps[i] === step) return i;
    return -1;
  }

  function stepForStatus(status) {
    return Object.prototype.hasOwnProperty.call(STEP_FOR_STATUS, status) ? STEP_FOR_STATUS[status] : null;
  }

  function findDemo(orders) {
    if (!orders) return null;
    for (var i = 0; i < orders.length; i++) if (orders[i] && orders[i].isDemo) return orders[i];
    return null;
  }

  /**
   * The step after observing <demo>. Training only moves forward, so a late or stale snapshot can
   * never reset it; a missing demo changes nothing. Before the practice starts, a delivered demo
   * (e.g. an earlier one still inside the delivered window) is ignored, as on the server.
   */
  function nextStep(steps, current, demo) {
    if (!demo) return current;
    if (current === INTRO && demo.status === "Delivered") return current;
    var observed = stepForStatus(demo.status);
    if (!observed) return current;
    return indexOf(steps, observed) > indexOf(steps, current) ? observed : current;
  }

  /** Whether an element marked data-training-when="step step …" is shown on <step>. */
  function shownOn(when, step) {
    if (!when) return true;
    return (" " + when + " ").indexOf(" " + step + " ") !== -1;
  }

  /**
   * How many real orders arrived during training, from the snapshot's training section. Only an isolated
   * trainee's snapshot carries one; anything else (including after the tenant went live) is 0.
   */
  function realOrderCount(training) {
    if (!training || training.isolated !== true) return 0;
    var count = training.realOrdersReceived;
    return typeof count === "number" && isFinite(count) && count > 0 ? Math.floor(count) : 0;
  }

  /**
   * The real-orders status copy for <count>: singular for one, the plural title with the count filled in for more,
   * nothing for none. Only the count is ever shown; no order or customer detail reaches this text.
   */
  function realOrdersText(copy, count) {
    if (!copy || !(count >= 1)) return null;
    if (count === 1) return { title: copy.titleOne || "", body: copy.bodyOne || "" };
    return { title: String(copy.titleMany || "").replace("{0}", String(count)), body: copy.bodyMany || "" };
  }

  /** Escape closes the panel only when nothing else (a dialog, a menu, a text field) owns the key. */
  function escapeClosesPanel(event, doc) {
    if (!event || event.key !== "Escape" || event.defaultPrevented) return false;
    if (doc.querySelector(".modal.show, .dropdown-menu.show")) return false;
    var target = event.target;
    // Bootstrap 5.3 handles Escape on the dialog itself and removes "show" before this document listener
    // runs (without preventDefault), so a key that came from inside any dialog is the dialog's, not ours.
    if (target && typeof target.closest === "function" && target.closest(".modal")) return false;
    if (doc.body && doc.body.classList && doc.body.classList.contains("modal-open")) return false;
    var tag = target && target.tagName ? String(target.tagName).toLowerCase() : "";
    if (tag === "input" || tag === "textarea" || tag === "select") return false;
    if (target && target.isContentEditable) return false;
    return true;
  }

  function prefersReducedMotion(win) {
    try {
      return !!(win.matchMedia && win.matchMedia("(prefers-reduced-motion: reduce)").matches);
    } catch (e) {
      return false;
    }
  }

  function createController(doc, win, config) {
    var section = doc.getElementById("waslaGuidedTraining");
    if (!section || !config || !config.copy) return null;

    var steps = config.steps || [];
    var panel = section.querySelector("[data-training-panel]");
    var reopen = section.querySelector("[data-training-reopen]");
    var waiting = section.querySelector("[data-training-waiting]");
    var announcer = section.querySelector("[data-training-announce]");
    var title = section.querySelector("[data-training-title]");
    var realOrders = section.querySelector("[data-training-real-orders-box]");

    var state = {
      step: indexOf(steps, config.step) >= 0 ? config.step : INTRO,
      demoId: null,
      // Whether the latest snapshot shows the practice order ("Show the practice order" needs one).
      demoPresent: false,
      // Real orders that arrived during training, as the latest snapshot reported (0: no status line).
      realOrders: 0,
      hidden: false,
      stepSince: Date.now(),
      waitingTimer: null
    };

    function host() {
      return doc.getElementById("ordersLiveScreenHost");
    }

    function live() {
      return win.WaslaOrders && win.WaslaOrders.liveStore ? win.WaslaOrders.liveStore : null;
    }

    function setText(node, value) {
      if (node && node.textContent !== (value || "")) node.textContent = value || "";
    }

    function announce(text) {
      if (!announcer || !text) return;
      // Clearing first makes a screen reader read a repeated phrase; the text itself changes only on a new state.
      announcer.textContent = "";
      win.setTimeout(function () { announcer.textContent = text; }, 50);
    }

    function render() {
      var copy = config.copy[state.step] || {};
      section.setAttribute("data-step", state.step);
      setText(section.querySelector("[data-training-progress]"), copy.progress);
      setText(title, copy.title);
      setText(section.querySelector("[data-training-body]"), copy.body);

      var scoped = section.querySelectorAll("[data-training-when], [data-training-needs-practice]");
      for (var i = 0; i < scoped.length; i++) {
        var node = scoped[i];
        node.hidden = (node.hasAttribute("data-training-needs-practice") && !state.demoPresent)
          || !shownOn(node.getAttribute("data-training-when"), state.step);
      }

      if (panel) panel.hidden = state.hidden;
      if (reopen) reopen.hidden = !state.hidden;
      updateWaiting();
    }

    /**
     * The one real-orders status line. It lives inside a polite live region (role="status"), so a new count is read
     * out once without moving focus; at zero it is hidden and empty, so there is no banner at all.
     */
    function renderRealOrders() {
      if (!realOrders) return;
      var text = realOrdersText(config.realOrders, state.realOrders);
      realOrders.hidden = !text;
      setText(realOrders.querySelector("[data-training-real-orders-title]"), text ? text.title : "");
      setText(realOrders.querySelector("[data-training-real-orders-body]"), text ? text.body : "");
    }

    function updateWaiting() {
      if (!waiting) return;
      win.clearTimeout(state.waitingTimer);
      if (!COURIER_STEPS[state.step]) {
        waiting.hidden = true;
        return;
      }
      var remaining = WAITING_HINT_AFTER_MS - (Date.now() - state.stepSince);
      waiting.hidden = remaining > 0;
      if (remaining > 0) state.waitingTimer = win.setTimeout(updateWaiting, remaining + 50);
    }

    function clearHighlight() {
      var marked = doc.querySelectorAll("." + TARGET_CLASS + ", ." + ACTION_CLASS);
      for (var i = 0; i < marked.length; i++) marked[i].classList.remove(TARGET_CLASS, ACTION_CLASS);
    }

    /** Re-applied after every render and DOM change, so Board, List and Focus all show the same pointer. */
    function highlight() {
      clearHighlight();
      var box = host();
      if (!box || state.hidden || state.step === INTRO) return;
      var demos = box.querySelectorAll("[data-wasla-demo-card], [data-focus-select][data-wasla-demo]");
      for (var i = 0; i < demos.length; i++) demos[i].classList.add(TARGET_CLASS);
      var action = (config.copy[state.step] || {}).action;
      if (!action) return;
      var buttons = box.querySelectorAll('[data-order-action="' + action + '"][data-wasla-demo]');
      for (var j = 0; j < buttons.length; j++) buttons[j].classList.add(ACTION_CLASS);
    }

    function focusTitle() {
      if (!title) return;
      title.setAttribute("tabindex", "-1");
      title.focus({ preventScroll: true });
    }

    /** Scrolls to the practice order (its pending action when there is one) and moves focus there. */
    function revealPractice() {
      if (state.demoId == null) return;
      var id = String(state.demoId);
      var store = live();
      var view = win.WaslaOrders && win.WaslaOrders.liveView && win.WaslaOrders.liveView.getView
        ? win.WaslaOrders.liveView.getView()
        : "board";
      if (view === "focus" && store && typeof store.selectOrder === "function") store.selectOrder(id, true);
      var box = host();
      if (!box) return;
      win.requestAnimationFrame(function () {
        var escaped = id.replace(/["\\]/g, "\\$&");
        var target = box.querySelector("." + ACTION_CLASS);
        if (!target) {
          var card = box.querySelector('.wasla-live-focus__detail [data-order-id="' + escaped + '"]')
            || box.querySelector('[data-order-id="' + escaped + '"]');
          target = card && (card.querySelector("[data-order-action]") || card.querySelector("[data-order-detail]") || card);
        }
        if (!target) return;
        if (typeof target.scrollIntoView === "function") {
          target.scrollIntoView({ block: "center", inline: "nearest", behavior: prefersReducedMotion(win) ? "auto" : "smooth" });
        }
        if (!target.matches("button, a[href], [tabindex]")) target.setAttribute("tabindex", "-1");
        target.focus({ preventScroll: true });
      });
    }

    /** Whether <node> is hidden by itself or by an ancestor inside the training section. */
    function isHiddenInSection(node) {
      for (var n = node; n && n !== section.parentNode; n = n.parentNode) {
        if (n.hidden) return true;
      }
      return false;
    }

    /**
     * A step change from a snapshot can hide the control that has keyboard focus (e.g. "Start practice order" once
     * the practice order was started in another tab). Move focus to the guide's title instead of letting it fall to
     * the page body. Focus outside the guide is never taken.
     */
    function keepFocusInSection(hadFocus) {
      if (!hadFocus || state.hidden) return;
      var active = doc.activeElement;
      if (active && section.contains(active) && !isHiddenInSection(active)) return;
      focusTitle();
    }

    function hide() {
      var hadFocus = section.contains(doc.activeElement);
      state.hidden = true;
      render();
      clearHighlight();
      if (hadFocus && reopen) reopen.focus();
    }

    function show() {
      state.hidden = false;
      render();
      highlight();
      focusTitle();
    }

    function onRendered(event) {
      var detail = event && event.detail ? event.detail : null;
      if (detail && Object.prototype.hasOwnProperty.call(detail, "training")) {
        // Only the status line changes: no step, focus, pointer or announcement of its own.
        var count = realOrderCount(detail.training);
        if (count !== state.realOrders) {
          state.realOrders = count;
          renderRealOrders();
        }
      }

      var orders = detail ? detail.orders : null;
      if (!orders) {
        highlight();
        return;
      }

      // The practice order alone drives the step; real orders in the snapshot (a tenant that is already live)
      // are ignored here.
      var hadFocus = section.contains(doc.activeElement);
      var demo = findDemo(orders);
      if (demo) state.demoId = String(demo.id);
      var demoChanged = !!demo !== state.demoPresent;
      state.demoPresent = !!demo;

      var next = nextStep(steps, state.step, demo);
      var stepChanged = next !== state.step;
      if (stepChanged) {
        state.step = next;
        state.stepSince = Date.now();
      }

      if (stepChanged || demoChanged) {
        render();
        keepFocusInSection(hadFocus);
      }
      if (stepChanged) announce((config.copy[next] || {}).title);
      highlight();
    }

    function onClick(event) {
      var target = event.target && event.target.closest ? event.target : null;
      if (!target) return;
      if (target.closest("[data-training-hide]")) hide();
      else if (target.closest("[data-training-reopen]")) show();
      else if (target.closest("[data-training-show-practice]")) revealPractice();
    }

    function onKeydown(event) {
      if (state.hidden || !escapeClosesPanel(event, doc)) return;
      // Closes the guide on this page view only; the journey and its saved step are untouched.
      hide();
    }

    // View switches and Focus selection replace cards without a new snapshot. Highlighting only changes
    // classes, and the observer only watches added/removed nodes, so this cannot feed itself.
    var scheduled = false;
    function scheduleHighlight() {
      if (scheduled) return;
      scheduled = true;
      win.requestAnimationFrame(function () {
        scheduled = false;
        highlight();
      });
    }

    doc.addEventListener("wasla:live-rendered", onRendered);
    section.addEventListener("click", onClick);
    doc.addEventListener("keydown", onKeydown);
    var box = host();
    if (box && win.MutationObserver) {
      new win.MutationObserver(scheduleHighlight).observe(box, { childList: true, subtree: true });
    }
    render();
    renderRealOrders();

    return {
      state: state,
      onRendered: onRendered,
      onKeydown: onKeydown,
      hide: hide,
      show: show,
      highlight: highlight
    };
  }

  var controller = null;

  function boot(doc, win) {
    function start() {
      if (controller) return;
      var node = doc.getElementById("waslaGuidedTrainingConfig");
      if (!node) return;
      var config;
      try {
        config = JSON.parse(node.textContent || "{}");
      } catch (e) {
        return;
      }
      controller = createController(doc, win, config);
    }
    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", start);
    else start();
  }

  return {
    STEP_FOR_STATUS: STEP_FOR_STATUS,
    WAITING_HINT_AFTER_MS: WAITING_HINT_AFTER_MS,
    stepForStatus: stepForStatus,
    findDemo: findDemo,
    nextStep: nextStep,
    shownOn: shownOn,
    realOrderCount: realOrderCount,
    realOrdersText: realOrdersText,
    escapeClosesPanel: escapeClosesPanel,
    createController: createController,
    boot: boot
  };
});
