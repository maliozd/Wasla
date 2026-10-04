(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root) root.WaslaTour = api;
})(typeof window !== "undefined" ? window : globalThis, function () {
  var gap = 12;
  var margin = 12;
  var mounted = null;
  var active = null;
  var layoutPending = false;
  // Set once the user skips or finishes on this page, so a lingering demo card cannot reopen the tour.
  var dismissed = false;

  function filterSteps(steps, isAvailable) {
    var kept = [];
    for (var i = 0; i < steps.length; i++) {
      if (isAvailable(steps[i])) kept.push(steps[i]);
    }
    return kept;
  }

  function moveIndex(length, index, delta) {
    if (length <= 0) return 0;
    var next = index + delta;
    if (next < 0) return 0;
    if (next >= length) return length - 1;
    return next;
  }

  function uniquePlacements(preferred) {
    var opposite = {
      top: "bottom",
      bottom: "top",
      "inline-start": "inline-end",
      "inline-end": "inline-start"
    };
    var order = [preferred, opposite[preferred] || "bottom", "bottom", "top", "inline-start", "inline-end"];
    var seen = {};
    var result = [];
    for (var i = 0; i < order.length; i++) {
      if (!order[i] || seen[order[i]]) continue;
      seen[order[i]] = true;
      result.push(order[i]);
    }
    return result;
  }

  function coordinates(placement, target, popover, rtl) {
    var top = target.top;
    var left = target.left;
    if (placement === "top") {
      top = target.top - gap - popover.height;
      left = target.left + (target.width - popover.width) / 2;
    } else if (placement === "bottom") {
      top = target.top + target.height + gap;
      left = target.left + (target.width - popover.width) / 2;
    } else {
      var onStartSide = placement === "inline-start";
      var onLeft = rtl ? !onStartSide : onStartSide;
      top = target.top + (target.height - popover.height) / 2;
      left = onLeft
        ? target.left - gap - popover.width
        : target.left + target.width + gap;
    }
    return { top: top, left: left };
  }

  function inside(point, popover, viewport) {
    return point.top >= margin
      && point.left >= margin
      && point.top + popover.height <= viewport.height - margin
      && point.left + popover.width <= viewport.width - margin;
  }

  function clamp(value, min, max) {
    if (max < min) return min;
    return Math.min(Math.max(value, min), max);
  }

  function overlaps(point, popover, rect) {
    return Math.min(point.left + popover.width, rect.left + rect.width) > Math.max(point.left, rect.left)
      && Math.min(point.top + popover.height, rect.top + rect.height) > Math.max(point.top, rect.top);
  }

  function coversAny(point, popover, rects) {
    for (var i = 0; i < rects.length; i++) {
      if (overlaps(point, popover, rects[i])) return true;
    }
    return false;
  }

  function choosePlacement(preferred, space) {
    var target = space.target;
    var popover = space.popover;
    var viewport = space.viewport;
    var rtl = !!space.rtl;
    var avoid = space.avoid || [];
    var order = uniquePlacements(preferred || "bottom");
    var points = order.map(function (placement) {
      var point = coordinates(placement, target, popover, rtl);
      // Slide along the target edge before abandoning a nearby placement.
      if (placement === "top" || placement === "bottom")
        point.left = clamp(point.left, margin, viewport.width - popover.width - margin);
      else
        point.top = clamp(point.top, margin, viewport.height - popover.height - margin);
      return point;
    });
    // Prefer a fitting placement that leaves the avoided areas readable. Side placements may then also
    // align with the target's bottom or top edge. Otherwise keep the first fitting centered placement.
    if (avoid.length) {
      for (var a = 0; a < order.length; a++) {
        var variants = [points[a]];
        if (order[a] === "inline-start" || order[a] === "inline-end") {
          variants.push({ top: target.top + target.height - popover.height, left: points[a].left });
          variants.push({ top: target.top, left: points[a].left });
        }
        for (var v = 0; v < variants.length; v++) {
          if (inside(variants[v], popover, viewport) && !coversAny(variants[v], popover, avoid))
            return { placement: order[a], top: variants[v].top, left: variants[v].left, fits: true };
        }
      }
    }
    for (var i = 0; i < order.length; i++) {
      if (inside(points[i], popover, viewport))
        return { placement: order[i], top: points[i].top, left: points[i].left, fits: true };
    }

    var best = null;
    order.forEach(function (placement) {
      var point = coordinates(placement, target, popover, rtl);
      point.top = clamp(point.top, margin, viewport.height - popover.height - margin);
      point.left = clamp(point.left, margin, viewport.width - popover.width - margin);
      var overlap = Math.max(0, Math.min(point.left + popover.width, target.left + target.width) - Math.max(point.left, target.left))
        * Math.max(0, Math.min(point.top + popover.height, target.top + target.height) - Math.max(point.top, target.top));
      if (!best || overlap < best.overlap)
        best = { placement: placement, top: point.top, left: point.left, fits: false, overlap: overlap };
    });
    return best;
  }

  function matchesTourEvent(step, eventName, detail) {
    if (!step || step.advance !== "event") return false;
    if (step.eventName && step.eventName !== eventName) return false;
    var match = step.eventMatch || null;
    detail = detail || {};
    if (match && match.action && detail.action !== match.action) return false;
    if (match && match.demo && !detail.demo) return false;
    return true;
  }

  // Skip is the explicit, permanent opt-out: it saves completion and ends the demo.
  // Escape only closes the coachmark for this page view; training resumes from the tour launcher.
  function exitPlan(reason) {
    return reason === "escape"
      ? { persist: false, stayClosedOnPage: true }
      : { persist: true, stayClosedOnPage: true };
  }

  // A tour that names a Skip confirmation dialog asks before the permanent opt-out. If the dialog
  // cannot be shown, Skip still works rather than trapping the user in the training.
  function skipNeedsConfirmation(config, dialogAvailable) {
    return !!(config && config.skipConfirmModal) && !!dialogAvailable;
  }

  // "event" steps wait for the user's action; "state" steps wait passively for the demo to change,
  // e.g. the platform courier delivering it. Neither shows a Next button.
  function waitsOnDemo(step) {
    return !!step && (step.advance === "event" || step.advance === "state");
  }

  // A passive step is done once its completeSelector matches, e.g. the demo shows Delivered.
  function stepCompleted(step) {
    return !!(step && step.completeSelector && typeof document !== "undefined"
      && document.querySelector(step.completeSelector));
  }

  // A demo action step waits for this user's demo. Once two live renders in a row show no demo
  // (rejected, expired or finished elsewhere) it cannot resume. One stale render right after
  // "Send a demo order" must not end the tour, hence two.
  function shouldStopWaiting(step, demoPresent, rendersWithoutDemo) {
    if (!step || !waitsOnDemo(step) || step.primaryAction) return false;
    return !demoPresent && rendersWithoutDemo >= 2;
  }

  // "Show real order" moves focus to the order's first usable action, else to the order itself.
  function focusTargetFor(card) {
    if (!card) return null;
    if (card.matches && card.matches("button, a[href]")) return card;
    var action = card.querySelector && card.querySelector("[data-order-action]:not([disabled]), [data-order-detail]");
    return action || card;
  }

  function isShown(element) {
    if (!element) return false;
    if (element.closest && element.closest("[hidden]")) return false;
    return typeof element.getClientRects !== "function" || element.getClientRects().length > 0;
  }

  function readyTarget(step) {
    if (!step || !step.readySelector) return null;
    var found = document.querySelectorAll(step.readySelector);
    for (var i = 0; i < found.length; i++) {
      if (isShown(found[i])) return found[i];
    }
    return null;
  }

  // A step may resume on a state it does not anchor to: the final step resumes from a delivered demo.
  function resumeTarget(step) {
    if (step && step.resumeSelector) {
      var found = document.querySelectorAll(step.resumeSelector);
      for (var i = 0; i < found.length; i++) {
        if (isShown(found[i])) return found[i];
      }
    }
    return readyTarget(step);
  }

  function lastReadyIndex(steps) {
    for (var i = steps.length - 1; i >= 0; i--) {
      if (resumeTarget(steps[i])) return i;
    }
    return -1;
  }

  function resumeIndex(steps) {
    return Math.max(0, lastReadyIndex(steps));
  }

  // Where a tour opens:
  // - an open demo resumes at its current step, never at "send a demo" (if its controls are off
  //   screen, e.g. Focus shows a real order, at its first action and place() catches up);
  // - a deliberate replay with no open demo starts a new training, even while the previous
  //   delivered demo is still on the Live Screen;
  // - an automatic start resumes at the last step the screen shows (e.g. success after delivery).
  function startIndex(steps, lastReady, resuming, manual) {
    if (resuming)
      return lastReady < 0 || steps[lastReady].primaryAction ? firstActionIndex(steps) : lastReady;
    if (manual) return 0;
    return Math.max(0, lastReady);
  }

  function firstActionIndex(steps) {
    for (var i = 0; i < steps.length; i++) {
      if (steps[i].advance === "event" && !steps[i].primaryAction) return i;
    }
    return 0;
  }

  function formatProgress(template, current, total) {
    return String(template || "{0} / {1}").replace("{0}", String(current)).replace("{1}", String(total));
  }

  function targetOf(step) {
    if (!step || !step.target || typeof document === "undefined") return null;
    return document.querySelector('[data-tour="' + step.target + '"]');
  }

  function resolveAnchor(step, element) {
    if (step && step.readySelector) return readyTarget(step);
    if (!element) return null;
    var style = window.getComputedStyle(element);
    var rect = element.getBoundingClientRect();
    if (style.display === "contents" || rect.width < 2 || rect.height < 2) {
      var child = element.querySelector("[data-order-action], button, a");
      if (child) return child;
    }
    return element;
  }

  function rectOf(element) {
    var rect = element.getBoundingClientRect();
    return { top: rect.top, left: rect.left, width: rect.width, height: rect.height };
  }

  function demoBoxOf(anchor) {
    return (anchor.closest && anchor.closest("[data-wasla-demo-card]")) || anchor;
  }

  function horizontalScroller(element) {
    var node = element.parentElement;
    while (node && node !== document.body && node !== document.documentElement) {
      var overflow = window.getComputedStyle(node).overflowX;
      if ((overflow === "auto" || overflow === "scroll") && node.scrollWidth > node.clientWidth + 1) return node;
      node = node.parentElement;
    }
    return null;
  }

  // Scrolls the board sideways so the whole demo card, not only its button, is visible.
  function revealInline(session, anchor) {
    var box = demoBoxOf(anchor);
    var scroller = horizontalScroller(box);
    if (!scroller) return false;
    var frame = scroller.getBoundingClientRect();
    var from = Math.max(frame.left, 0) + 8;
    var to = Math.min(frame.right, window.innerWidth) - 8;
    var rect = box.getBoundingClientRect();
    if (rect.width > to - from) rect = anchor.getBoundingClientRect();
    var delta = rect.left < from ? rect.left - from : rect.right > to ? rect.right - to : 0;
    if (Math.abs(delta) < 1) return false;
    if (!session.scrollRestore) session.scrollRestore = { element: scroller, left: scroller.scrollLeft };
    scroller.scrollLeft += delta;
    return true;
  }

  // Live cards use content-visibility with an estimated height, so on a long stacked board (phones)
  // a jump to the demo card lands short. Render them once at their real size; the browser then remembers it.
  function measureLiveCards(anchor) {
    var host = anchor.closest && anchor.closest("#ordersLiveScreenHost");
    if (!host || host.classList.contains("wasla-tour-measuring")) return;
    host.classList.add("wasla-tour-measuring");
    host.getBoundingClientRect();
    window.setTimeout(function () { host.classList.remove("wasla-tour-measuring"); }, 200);
  }

  function restoreScroll(session) {
    var saved = session && session.scrollRestore;
    if (!saved) return;
    session.scrollRestore = null;
    try { saved.element.scrollLeft = saved.left; } catch (e) { }
  }

  // Demo coachmarks should leave the header, the DEMO badge and real orders readable when space allows.
  function demoAvoidRects(anchor) {
    var rects = [];
    var add = function (element) {
      if (!element) return;
      var rect = element.getBoundingClientRect();
      if (rect.width < 1 || rect.height < 1 || rect.bottom < 0 || rect.right < 0
        || rect.top > window.innerHeight || rect.left > window.innerWidth) return;
      rects.push({ top: rect.top, left: rect.left, width: rect.width, height: rect.height });
    };
    add(document.querySelector(".wasla-live-display-header"));
    var card = anchor.closest ? anchor.closest("[data-wasla-demo-card]") : null;
    if (card) add(card.querySelector(".wasla-demo-badge"));
    var real = document.querySelectorAll("#ordersLiveScreenHost [data-order-status]:not([data-wasla-demo]), #ordersLiveScreenHost .wasla-live-detail");
    for (var i = 0; i < real.length && rects.length < 80; i++) add(real[i]);
    return rects;
  }

  function ensureUi() {
    var backdrop = document.querySelector(".wasla-tour-backdrop");
    if (!backdrop) {
      backdrop = document.createElement("div");
      backdrop.className = "wasla-tour-backdrop";
      backdrop.hidden = true;
      document.body.appendChild(backdrop);
    }
    var dialog = document.querySelector(".wasla-tour");
    if (!dialog) {
      dialog = document.createElement("div");
      dialog.className = "wasla-tour";
      dialog.setAttribute("role", "dialog");
      dialog.setAttribute("aria-modal", "false");
      dialog.hidden = true;
      dialog.innerHTML =
        '<p class="wasla-tour__progress" data-tour-progress dir="ltr"></p>' +
        '<h2 class="wasla-tour__title" id="wasla-tour-title" data-tour-title></h2>' +
        '<p class="wasla-tour__body" id="wasla-tour-body" data-tour-body></p>' +
        '<div class="wasla-tour__actions">' +
        '<button type="button" class="btn btn-link btn-sm wasla-tour__skip" data-tour-skip></button>' +
        '<button type="button" class="btn btn-outline-secondary btn-sm" data-tour-back></button>' +
        '<button type="button" class="btn btn-primary btn-sm" data-tour-next></button>' +
        "</div>";
      dialog.setAttribute("aria-labelledby", "wasla-tour-title");
      dialog.setAttribute("aria-describedby", "wasla-tour-body");
      document.body.appendChild(dialog);
    }
    return { backdrop: backdrop, dialog: dialog };
  }

  function clearTarget() {
    var marked = document.querySelectorAll(".wasla-tour-target");
    for (var i = 0; i < marked.length; i++) {
      marked[i].classList.remove("wasla-tour-target");
      marked[i].removeAttribute("data-tour-positioned");
    }
  }

  function closeUi() {
    clearTarget();
    var backdrop = document.querySelector(".wasla-tour-backdrop");
    var dialog = document.querySelector(".wasla-tour");
    if (backdrop) backdrop.hidden = true;
    if (dialog) dialog.hidden = true;
  }

  // Guided demos (they carry a finishUrl) complete on reaching the success step; other tours on Done.
  function completesTraining(config, index, length) {
    return !!(config && config.finishUrl) && length > 1 && index === length - 1;
  }

  function persist(config) {
    if (!config) return;
    try {
      var body = "key=" + encodeURIComponent(config.key || "") +
        "&__RequestVerificationToken=" + encodeURIComponent(config.token || "");
      if (config.completeUrl) {
        fetch(config.completeUrl, {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded", "X-Requested-With": "XMLHttpRequest" },
          body: body,
          credentials: "same-origin"
        }).catch(function () { });
      }
      if (config.finishUrl) {
        fetch(config.finishUrl, {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded", "X-Requested-With": "XMLHttpRequest" },
          body: "__RequestVerificationToken=" + encodeURIComponent(config.token || ""),
          credentials: "same-origin"
        }).catch(function () { });
      }
    } catch (e) { }
  }

  function finish(session, save) {
    if (!session) return;
    var restore = session.previousFocus;
    closeUi();
    restoreScroll(session);
    active = null;
    session.closed = true;
    if (save) dismissed = true;
    window.clearTimeout(session.readyTimer);
    var notice = document.querySelector(".wasla-tour-pause");
    if (notice) notice.hidden = true;
    if (save && !session.saved) persist(session.config);
    if (restore && typeof restore.focus === "function") {
      try { restore.focus(); } catch (e) { }
    }
  }

  function skipModal(session) {
    var selector = session && session.config.skipConfirmModal;
    var modal = selector ? document.querySelector(selector) : null;
    return modal && window.bootstrap && window.bootstrap.Modal ? modal : null;
  }

  // Skip training is a permanent opt-out, so a tour with a confirmation dialog asks first.
  // Cancelling returns to the same step; tours without a dialog skip at once as before.
  function requestSkip(session) {
    if (!session) return;
    var modal = skipModal(session);
    if (!skipNeedsConfirmation(session.config, !!modal)) {
      leave(session, "skip");
      return;
    }
    var origin = document.activeElement;
    if (modal.getAttribute("data-tour-skip-bound") !== "true") {
      modal.setAttribute("data-tour-skip-bound", "true");
      modal.querySelector("[data-tour-skip-confirm]").addEventListener("click", function () {
        modal.setAttribute("data-tour-skip-confirmed", "true");
        window.bootstrap.Modal.getOrCreateInstance(modal).hide();
        if (active) leave(active, "skip");
      });
      modal.addEventListener("hidden.bs.modal", function () {
        var confirmed = modal.getAttribute("data-tour-skip-confirmed") === "true";
        modal.removeAttribute("data-tour-skip-confirmed");
        if (active) active.confirmingSkip = false;
        if (confirmed || !active) return;
        // Show the same step again first: focus cannot land on a hidden coachmark.
        if (!active.suspended) place(active);
        // Return focus to the Skip control that opened the dialog, else the visible Skip control.
        var back = modal.__waslaSkipOrigin;
        if (!back || back === document.body || !document.contains(back) || typeof back.focus !== "function"
            || (back.closest && back.closest("[hidden]")))
          back = document.querySelector(".wasla-tour:not([hidden]) [data-tour-skip], .wasla-tour-pause:not([hidden]) [data-tour-pause-skip]");
        if (back) back.focus({ preventScroll: true });
      });
    }
    modal.__waslaSkipOrigin = origin;
    // The coachmark sits above Bootstrap's modal layer; hide it while the dialog is open, and keep
    // live updates from re-placing it. Cancelling re-places it on the same step.
    session.confirmingSkip = true;
    closeUi();
    window.bootstrap.Modal.getOrCreateInstance(modal).show();
  }

  function leave(session, reason) {
    if (!session) return;
    var plan = exitPlan(reason);
    finish(session, plan.persist);
    if (plan.stayClosedOnPage) dismissed = true;
  }

  function demoOnScreen(session) {
    var selector = session.config.resumeWhenPresent;
    return selector ? !!document.querySelector(selector) : true;
  }

  function place(session) {
    if (session.closed || session.suspended || session.confirmingSkip || active !== session) return;
    var step = session.steps[session.index];
    if (stepCompleted(step) && session.index < session.steps.length - 1) {
      // Reached through live renders only (no polling). show() saves the training on its final step.
      show(session, session.index + 1);
      return;
    }
    if (step.readySelector && !readyTarget(step)) {
      // The visible demo already moved past this step, e.g. after a reload or a Focus selection change.
      var later = lastReadyIndex(session.steps);
      if (later > session.index) {
        show(session, later);
        return;
      }
    }
    var element = targetOf(step);
    var anchor = resolveAnchor(step, element);
    var selectingDemo = false;
    if (!anchor && step.advance === "event" && !step.primaryAction) {
      // Narrow Focus hides the queue while a real order is open; wait instead of floating over the header.
      var entry = document.querySelector("[data-focus-select][data-wasla-demo]");
      anchor = isShown(entry) ? entry : null;
      selectingDemo = !!anchor;
    }
    if (step.readySelector && !anchor) {
      window.clearTimeout(session.readyTimer);
      if (!session.waiting) {
        session.waiting = true;
        session.rendersWithoutDemo = 0;
      }
      if (shouldStopWaiting(step, demoOnScreen(session), session.rendersWithoutDemo)) {
        // The demo is gone; stop polling. A new demo resumes guidance on its own.
        finish(session, false);
        return;
      }
      // Snapshot refresh can be slow or temporarily fail. Never skip a required action.
      closeUi();
      session.readyTimer = window.setTimeout(function () { place(session); }, 250);
      return;
    }
    session.waiting = false;
    if (!element) {
      if (anchor) element = anchor;
      else {
      session.steps.splice(session.index, 1);
      if (session.steps.length === 0) {
        finish(session, false);
        return;
      }
      if (session.index >= session.steps.length) session.index = session.steps.length - 1;
      place(session);
      return;
      }
    }

    anchor = anchor || element;
    var waitsForAction = waitsOnDemo(step);
    var passive = step.advance === "state";
    var last = session.index === session.steps.length - 1;
    var demoStep = selectingDemo || !!(anchor.closest && anchor.closest("[data-wasla-demo]"));
    if (anchor.getAttribute("data-order-id") && !selectingDemo) session.demoId = anchor.getAttribute("data-order-id");
    if (last && !waitsForAction) restoreScroll(session);
    if (!session.scrolled || session.recheckReveal) {
      session.scrolled = true;
      session.recheckReveal = false;
      var shifted = demoStep && revealInline(session, anchor);
      var visible = anchor.getBoundingClientRect();
      var out = visible.width < 2 || visible.top < 160 || visible.bottom > window.innerHeight - 160 || visible.right > window.innerWidth || visible.left < 0;
      if (out && typeof anchor.scrollIntoView === "function") {
        if (demoStep) measureLiveCards(anchor);
        var box = demoStep ? demoBoxOf(anchor) : anchor;
        if (box.getBoundingClientRect().height > window.innerHeight - 200) box = anchor;
        box.scrollIntoView({ block: "center", inline: "nearest", behavior: "auto" });
      }
      if (shifted || out) {
        session.revealAttempts = (session.revealAttempts || 0) + 1;
        window.setTimeout(function () {
          if (active !== session) return;
          // Check again once the cards keep their real size, but never after the user scrolls on their own.
          session.recheckReveal = out && session.revealAttempts < 4 && !session.userScrolled;
          place(session);
        }, 280);
        return;
      }
    }

    clearTarget();
    anchor.classList.add("wasla-tour-target");
    if (window.getComputedStyle(anchor).position === "static") {
      anchor.setAttribute("data-tour-positioned", "true");
    }

    var ui = session.ui;
    ui.backdrop.hidden = waitsForAction || session.config.finishUrl != null;
    ui.dialog.hidden = false;
    ui.dialog.style.inlineSize = Math.min(352, Math.max(220, window.innerWidth - 24)) + "px";
    var progress = ui.dialog.querySelector("[data-tour-progress]");
    progress.textContent = formatProgress(session.config.copy.progress, session.index + 1, session.steps.length);
    progress.hidden = selectingDemo;
    ui.dialog.querySelector("[data-tour-title]").textContent = selectingDemo ? session.config.copy.selectDemo : step.title || "";
    ui.dialog.querySelector("[data-tour-body]").textContent = selectingDemo ? session.config.copy.selectDemoBody : step.body || "";
    var back = ui.dialog.querySelector("[data-tour-back]");
    back.hidden = session.index === 0 || !!session.config.finishUrl;
    var next = ui.dialog.querySelector("[data-tour-next]");
    var hasPrimary = !!step.primaryAction;
    ui.dialog.setAttribute("data-tour-kind", last && !waitsForAction ? "success"
      : passive ? "waiting"
      : waitsForAction && !hasPrimary ? "action" : "intro");
    var skip = ui.dialog.querySelector("[data-tour-skip]");
    skip.textContent = session.config.finishUrl ? session.config.copy.skipTraining : session.config.copy.skip;
    // The demo's completion step has nothing left to skip; its primary action closes the training.
    skip.hidden = last && !waitsForAction && !!session.config.finishUrl;
    next.hidden = waitsForAction && !hasPrimary;
    next.disabled = currentLaunchBusy();
    next.textContent = hasPrimary
      ? (step.primaryLabel || session.config.copy.continueLabel)
      : (last ? session.config.copy.finish : session.config.copy.continueLabel);

    var popover = {
      width: ui.dialog.offsetWidth || Math.min(352, window.innerWidth - 24),
      height: ui.dialog.offsetHeight || 168
    };
    var chosen = choosePlacement(selectingDemo ? "inline-end" : step.placement, {
      target: rectOf(anchor),
      popover: popover,
      viewport: { width: window.innerWidth, height: window.innerHeight },
      rtl: document.documentElement.getAttribute("dir") === "rtl",
      avoid: demoStep ? demoAvoidRects(anchor) : []
    });
    chosen.left = clamp(chosen.left, margin, Math.max(margin, window.innerWidth - popover.width - margin));
    chosen.top = clamp(chosen.top, margin, Math.max(margin, window.innerHeight - popover.height - margin));
    ui.dialog.setAttribute("data-placement", chosen.placement);
    var rtl = document.documentElement.getAttribute("dir") === "rtl";
    var side = chosen.placement;
    if (side === "inline-start") side = rtl ? "right" : "left";
    if (side === "inline-end") side = rtl ? "left" : "right";
    ui.dialog.setAttribute("data-side", side);
    ui.dialog.setAttribute("data-detached", chosen.fits ? "false" : "true");
    ui.dialog.style.top = Math.round(chosen.top) + "px";
    ui.dialog.style.left = Math.round(chosen.left) + "px";
    var targetRect = rectOf(anchor);
    var arrow = chosen.placement === "top" || chosen.placement === "bottom"
      ? targetRect.left + targetRect.width / 2 - chosen.left
      : targetRect.top + targetRect.height / 2 - chosen.top;
    ui.dialog.style.setProperty("--wasla-tour-arrow", Math.round(clamp(arrow, 16, (chosen.placement === "top" || chosen.placement === "bottom" ? popover.width : popover.height) - 16)) + "px");
    if (!session.focused) {
      session.focused = true;
      // A passive waiting step leaves focus where the user is working.
      if (!passive) {
        if (waitsForAction && !hasPrimary && typeof anchor.focus === "function") anchor.focus({ preventScroll: true });
        else next.focus({ preventScroll: true });
      }
    }
  }

  function currentLaunchBusy() {
    var form = document.querySelector(".wasla-demo-launch");
    return !!form && form.getAttribute("aria-busy") === "true";
  }

  function show(session, index) {
    session.index = moveIndex(session.steps.length, index, 0);
    session.scrolled = false;
    session.recheckReveal = false;
    session.revealAttempts = 0;
    session.userScrolled = false;
    session.focused = false;
    session.readyTries = 0;
    // However the success step is reached (live advance, resume after a pause, reload), it completes the training.
    if (!session.saved && completesTraining(session.config, session.index, session.steps.length)) {
      session.saved = true;
      persist(session.config);
    }
    place(session);
  }

  function onKey(event) {
    if (!active || document.querySelector(".modal.show")) return;
    // Escape inside a dialog (e.g. cancelling the Skip confirmation) belongs to that dialog.
    // Bootstrap closes it first, so the open-modal check above no longer sees it.
    if (active.confirmingSkip || (event.target && event.target.closest && event.target.closest(".modal"))) return;
    if (event.key === "Escape") {
      event.preventDefault();
      leave(active, "escape");
      return;
    }
    // This is a non-modal coachmark. Keyboard users must reach every order action.
  }

  function onLayout() {
    if (layoutPending) return;
    layoutPending = true;
    window.requestAnimationFrame(function () {
      layoutPending = false;
      if (active && !active.closed && !active.suspended) place(active);
    });
  }

  function bind(session) {
    var dialog = session.ui.dialog;
    if (dialog.getAttribute("data-tour-bound") === "true") return;
    dialog.setAttribute("data-tour-bound", "true");
    dialog.querySelector("[data-tour-skip]").addEventListener("click", function () {
      if (active) requestSkip(active);
    });
    dialog.querySelector("[data-tour-back]").addEventListener("click", function () {
      if (active) show(active, active.index - 1);
    });
    dialog.querySelector("[data-tour-next]").addEventListener("click", function () {
      if (!active) return;
      var current = active.steps[active.index];
      if (current && current.primaryAction === "demo-start") {
        var form = document.querySelector(".wasla-demo-launch");
        if (form && typeof form.requestSubmit === "function") form.requestSubmit();
        return;
      }
      if (active.index >= active.steps.length - 1) finish(active, true);
      else show(active, active.index + 1);
    });
    document.addEventListener("keydown", onKey);
    var userScroll = function (event) {
      if (!active) return;
      if (event.type !== "keydown" || /^(PageUp|PageDown|Home|End|ArrowUp|ArrowDown| )$/.test(event.key)) active.userScrolled = true;
    };
    document.addEventListener("wheel", userScroll, { passive: true });
    document.addEventListener("touchmove", userScroll, { passive: true });
    document.addEventListener("keydown", userScroll);
    window.addEventListener("resize", onLayout);
    window.addEventListener("scroll", onLayout, true);
    document.addEventListener("wasla:order-action-completed", onTourEvent);
    document.addEventListener("wasla:demo-order-started", onTourEvent);
    document.addEventListener("wasla:demo-launch-state", onLayout);
    document.addEventListener("wasla:live-rendered", function () {
      if (active && active.waiting)
        active.rendersWithoutDemo = demoOnScreen(active) ? 0 : (active.rendersWithoutDemo || 0) + 1;
      onLayout();
    });
    document.addEventListener("wasla:real-order-arrived", function (event) {
      if (!active || active.closed || active.suspended) return;
      suspend(active, event.detail && event.detail.orderId);
    });
    document.addEventListener("shown.bs.modal", function (event) {
      // The Skip confirmation belongs to the tour; any other modal (e.g. an order detail) pauses it.
      if (active && event.target === skipModal(active)) return;
      if (active && !active.suspended) suspend(active, null);
    });
    var host = document.getElementById("ordersLiveScreenHost");
    if (host && window.MutationObserver) {
      new MutationObserver(onLayout).observe(host, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-order-status"] });
    }
  }

  function pauseNotice() {
    var notice = document.querySelector(".wasla-tour-pause");
    if (notice) return notice;
    notice = document.createElement("div");
    notice.className = "wasla-tour-pause";
    notice.setAttribute("role", "status");
    notice.hidden = true;
    notice.innerHTML =
      '<p class="wasla-tour-pause__text" data-tour-pause-text></p>' +
      '<div class="wasla-tour-pause__actions">' +
      '<button type="button" class="btn btn-primary btn-sm" data-tour-view-real></button>' +
      '<button type="button" class="btn btn-outline-secondary btn-sm" data-tour-resume></button>' +
      '<button type="button" class="btn btn-link btn-sm" data-tour-pause-skip></button>' +
      '</div>';
    var host = document.getElementById("ordersLiveScreenHost");
    if (host) host.before(notice);
    else document.body.appendChild(notice);
    notice.querySelector("[data-tour-view-real]").addEventListener("click", function () {
      var id = notice.getAttribute("data-order-id");
      // Look inside the Live Screen only: this notice carries the same data-order-id.
      var card = id ? document.querySelector('#ordersLiveScreenHost [data-order-id="' + id + '"]') : null;
      if (card && card.hasAttribute("data-focus-select")) card.click();
      if (card && typeof card.scrollIntoView === "function") {
        card.scrollIntoView({ block: "center", inline: "nearest", behavior: "smooth" });
      }
      var target = focusTargetFor(card);
      if (target && typeof target.focus === "function") {
        if (target === card && !target.hasAttribute("tabindex") && !card.matches("button, a[href]"))
          target.setAttribute("tabindex", "-1");
        target.focus({ preventScroll: true });
      }
    });
    notice.querySelector("[data-tour-resume]").addEventListener("click", function () {
      if (!active) return;
      active.suspended = false;
      notice.hidden = true;
      var live = window.WaslaOrders && window.WaslaOrders.liveStore;
      if (active.demoId && live && live.selectOrder) live.selectOrder(active.demoId);
      show(active, Math.max(active.index, resumeIndex(active.steps)));
    });
    notice.querySelector("[data-tour-pause-skip]").addEventListener("click", function () { if (active) requestSkip(active); });
    return notice;
  }

  function suspend(session, orderId) {
    if (!session || session.closed || session.suspended) return;
    session.suspended = true;
    closeUi();
    var notice = pauseNotice();
    var copy = (session.config && session.config.copy) || {};
    notice.querySelector("[data-tour-pause-text]").textContent = orderId ? copy.realOrderPaused : copy.trainingPaused;
    var view = notice.querySelector("[data-tour-view-real]");
    view.textContent = copy.viewRealOrder || "";
    view.hidden = !orderId;
    if (orderId) notice.setAttribute("data-order-id", orderId);
    else notice.removeAttribute("data-order-id");
    notice.querySelector("[data-tour-resume]").textContent = copy.continueTraining || "";
    var pauseSkip = notice.querySelector("[data-tour-pause-skip]");
    pauseSkip.textContent = (session.config && session.config.finishUrl ? copy.skipTraining : copy.skip) || "";
    // As on the success step itself, a finished demo has nothing left to skip.
    var current = session.steps[session.index];
    pauseSkip.hidden = !!(session.config && session.config.finishUrl)
      && session.index === session.steps.length - 1 && !waitsOnDemo(current);
    notice.hidden = false;
  }

  function onTourEvent(event) {
    if (!active || active.closed) return;
    var step = active.steps[active.index];
    if (!matchesTourEvent(step, event.type, event.detail)) return;
    if (event.detail && event.detail.orderId && active.demoId && event.detail.orderId !== active.demoId) return;
    if (event.detail && event.detail.orderId) active.demoId = event.detail.orderId;
    if (active.suspended) { active.index = moveIndex(active.steps.length, active.index, 1); return; }
    if (active.index >= active.steps.length - 1) finish(active, true);
    else show(active, active.index + 1);
  }

  function run(config, manual) {
    if (active) finish(active, false);
    if (typeof document === "undefined") return;
    if (!manual && document.querySelector(".modal.show")) return;
    var steps = filterSteps(config.steps || [], function (step) {
      if (step.advance === "event" || step.readySelector) return true;
      return !!targetOf(step);
    });
    if (!steps.length) return;
    var ui = ensureUi();
    ui.dialog.querySelector("[data-tour-skip]").textContent = config.copy.skip;
    ui.dialog.querySelector("[data-tour-back]").textContent = config.copy.back;
    ui.dialog.setAttribute("aria-label", config.copy.close || "");
    var session = {
      config: config,
      steps: steps.slice(),
      index: 0,
      ui: ui,
      previousFocus: document.activeElement,
      scrolled: false,
      focused: false,
      closed: false
    };
    active = session;
    bind(session);
    var resuming = !!(config.resumeWhenPresent && document.querySelector(config.resumeWhenPresent));
    show(session, startIndex(session.steps, lastReadyIndex(session.steps), resuming, manual));
  }

  // Restores guidance for this user's in-progress demo after a reload or a demo started outside the tour.
  function resumeIfPresent(config) {
    if (active || dismissed || mounted !== config || !config.resumeWhenPresent) return;
    if (!document.querySelector(config.resumeWhenPresent)) return;
    try { run(config, false); } catch (e) { }
  }

  function whenReady(config, callback) {
    var selector = config.waitUntilGone;
    if (!selector || !document.querySelector(selector)) {
      callback();
      return;
    }
    var pending = true;
    var observer = new MutationObserver(function () {
      if (!pending || document.querySelector(selector)) return;
      pending = false;
      observer.disconnect();
      callback();
    });
    observer.observe(document.body, { childList: true, subtree: true });
    window.setTimeout(function () {
      if (!pending) return;
      // A resumable demo tour must not guess before the first snapshot shows whether a demo already exists.
      if (config.resumeWhenPresent && document.querySelector(selector)) return;
      pending = false;
      observer.disconnect();
      callback();
    }, 4000);
  }

  function mount(config) {
    mounted = config;
    if (!config || typeof document === "undefined") return;
    document.addEventListener("click", function (event) {
      var button = event.target && event.target.closest ? event.target.closest("[data-wasla-tour-replay]") : null;
      if (!button || !mounted) return;
      event.preventDefault();
      try { run(mounted, true); } catch (e) { }
    });
    if (config.resumeWhenPresent) {
      document.addEventListener("wasla:live-rendered", function () {
        // Let the render's own observers (e.g. hiding the demo launch bar) settle first.
        window.setTimeout(function () { resumeIfPresent(config); }, 0);
      });
      document.addEventListener("wasla:demo-order-started", function () { dismissed = false; });
    }
    if (!config.autoStart) return;
    var start = function () {
      whenReady(config, function () {
        if (active) return;
        try { run(config, false); } catch (e) { }
      });
    };
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
    else start();
  }

  function replay() {
    if (mounted) {
      try { run(mounted, true); } catch (e) { }
    }
  }

  return {
    filterSteps: filterSteps,
    moveIndex: moveIndex,
    choosePlacement: choosePlacement,
    formatProgress: formatProgress,
    matchesTourEvent: matchesTourEvent,
    exitPlan: exitPlan,
    skipNeedsConfirmation: skipNeedsConfirmation,
    completesTraining: completesTraining,
    startIndex: startIndex,
    waitsOnDemo: waitsOnDemo,
    stepCompleted: stepCompleted,
    shouldStopWaiting: shouldStopWaiting,
    focusTargetFor: focusTargetFor,
    resumeIndex: resumeIndex,
    mount: mount,
    replay: replay
  };
});
