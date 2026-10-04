const test = require("node:test");
const assert = require("node:assert/strict");
const tour = require("../../../src/Wasla.Web/wwwroot/js/wasla-tour.js");

test("missing targets are removed before a tour starts", function () {
  const steps = [
    { target: "live-orders" },
    { target: "live-order-actions" },
    { target: "live-timer" },
    { target: "live-view-selector" }
  ];
  const available = new Set(["live-orders", "live-view-selector"]);
  const kept = tour.filterSteps(steps, function (step) { return available.has(step.target); });
  assert.deepEqual(kept.map(function (step) { return step.target; }), ["live-orders", "live-view-selector"]);
});

test("next and back stay inside the step list", function () {
  assert.equal(tour.moveIndex(3, 0, 1), 1);
  assert.equal(tour.moveIndex(3, 2, 1), 2);
  assert.equal(tour.moveIndex(3, 0, -1), 0);
  assert.equal(tour.moveIndex(3, 2, -1), 1);
});

test("preferred placement falls back when it does not fit", function () {
  const space = {
    target: { top: 20, left: 80, width: 200, height: 40 },
    popover: { width: 180, height: 90 },
    viewport: { width: 420, height: 320 },
    rtl: false
  };
  const placed = tour.choosePlacement("top", space);
  assert.equal(placed.placement, "bottom");
  assert.equal(placed.fits, true);
  assert.ok(placed.top >= 12);
  assert.ok(placed.left >= 12);
});

test("inline-start follows the document direction", function () {
  const space = {
    target: { top: 80, left: 160, width: 40, height: 40 },
    popover: { width: 100, height: 80 },
    viewport: { width: 500, height: 400 },
    rtl: false
  };
  const ltr = tour.choosePlacement("inline-start", space);
  assert.equal(ltr.placement, "inline-start");
  assert.ok(ltr.left < space.target.left);

  const rtl = tour.choosePlacement("inline-start", Object.assign({}, space, { rtl: true }));
  assert.equal(rtl.placement, "inline-start");
  assert.ok(rtl.left > space.target.left);
});

test("a tall target is kept inside the viewport", function () {
  const placed = tour.choosePlacement("bottom", {
    target: { top: 0, left: 0, width: 800, height: 900 },
    popover: { width: 280, height: 140 },
    viewport: { width: 390, height: 700 },
    rtl: false
  });
  assert.ok(placed.top >= 12);
  assert.ok(placed.top + 140 <= 700 - 12);
  assert.ok(placed.left + 280 <= 390 - 12);
});

test("a fitting placement that leaves avoided areas readable is preferred", function () {
  const badge = { top: 200, left: 20, width: 60, height: 24 };
  const placed = tour.choosePlacement("top", {
    target: { top: 300, left: 20, width: 90, height: 40 },
    popover: { width: 200, height: 90 },
    viewport: { width: 800, height: 600 },
    rtl: false,
    avoid: [badge]
  });
  assert.equal(placed.fits, true);
  assert.notEqual(placed.placement, "top");
  const covers = Math.min(placed.left + 200, badge.left + badge.width) > Math.max(placed.left, badge.left)
    && Math.min(placed.top + 90, badge.top + badge.height) > Math.max(placed.top, badge.top);
  assert.equal(covers, false);
});

test("a side placement may align with the action's bottom edge to keep the badge and the next card readable", function () {
  const target = { top: 300, left: 20, width: 80, height: 40 };
  const placed = tour.choosePlacement("top", {
    target,
    popover: { width: 200, height: 150 },
    viewport: { width: 800, height: 600 },
    rtl: false,
    avoid: [{ top: 180, left: 20, width: 60, height: 24 }, { top: 360, left: 10, width: 250, height: 200 }]
  });
  assert.equal(placed.placement, "inline-end");
  assert.equal(placed.top + 150, target.top + target.height);
  assert.equal(placed.left, target.left + target.width + 12);
});

test("avoided areas never push the coachmark off the preferred fitting placement when nothing else fits", function () {
  const space = {
    target: { top: 300, left: 12, width: 90, height: 40 },
    popover: { width: 352, height: 120 },
    viewport: { width: 375, height: 700 },
    rtl: false
  };
  const plain = tour.choosePlacement("top", space);
  const avoided = tour.choosePlacement("top", Object.assign({}, space, {
    avoid: [{ top: 0, left: 0, width: 375, height: 290 }, { top: 350, left: 0, width: 375, height: 350 }]
  }));
  assert.deepEqual(avoided, plain);
});

test("skip training is a permanent opt-out, Escape only closes the coachmark for this page", function () {
  const skip = tour.exitPlan("skip");
  assert.equal(skip.persist, true, "skip saves completion and ends the demo");
  assert.equal(skip.stayClosedOnPage, true);

  const escape = tour.exitPlan("escape");
  assert.equal(escape.persist, false, "escape never saves completion or finishes the demo");
  assert.equal(escape.stayClosedOnPage, true, "escape does not reopen on the next live render");
});

test("a demo action step stops waiting only after two live renders without the demo", function () {
  const approve = { advance: "event", eventName: "wasla:order-action-completed", readySelector: "[data-order-action='approve'][data-wasla-demo]" };
  assert.equal(tour.shouldStopWaiting(approve, true, 5), false, "a visible demo keeps the step alive");
  assert.equal(tour.shouldStopWaiting(approve, false, 0), false, "no render yet");
  assert.equal(tour.shouldStopWaiting(approve, false, 1), false, "one stale render right after starting a demo");
  assert.equal(tour.shouldStopWaiting(approve, false, 2), true, "rejected, expired or finished elsewhere");

  const start = { advance: "event", primaryAction: "demo-start", readySelector: "[data-tour='demo-order-start']" };
  assert.equal(tour.shouldStopWaiting(start, false, 9), false, "the start step exists before any demo");
  assert.equal(tour.shouldStopWaiting({ target: "live-orders" }, false, 9), false, "informational steps never wait on a demo");
});

// The guided demo's delivery steps, as ProductTourCatalog defines them.
const deliverySteps = [
  { advance: "event", primaryAction: "demo-start", readySelector: "[data-tour='demo-order-start']" },
  { advance: "event", readySelector: "[data-order-action='approve'][data-wasla-demo]" },
  { advance: "event", readySelector: "[data-order-action='start-preparing'][data-wasla-demo]" },
  { advance: "event", readySelector: "[data-order-action='mark-ready'][data-wasla-demo]" },
  {
    advance: "state",
    readySelector: "[data-wasla-demo][data-order-status='ReadyForPickup']",
    completeSelector: "[data-wasla-demo][data-order-status='OnTheWay']"
  },
  {
    advance: "state",
    readySelector: "[data-wasla-demo][data-order-status='OnTheWay']",
    completeSelector: "[data-wasla-demo][data-order-status='Delivered']"
  },
  { target: "live-view-selector", primaryAction: "demo-finish", resumeSelector: "[data-wasla-demo][data-order-status='Delivered']" }
];

function withDocument(visibleSelectors, run) {
  const previous = globalThis.document;
  const shown = { closest: () => null, getClientRects: () => [{}] };
  globalThis.document = {
    querySelector: (selector) => visibleSelectors.indexOf(selector) >= 0 ? shown : null,
    querySelectorAll: (selector) => visibleSelectors.indexOf(selector) >= 0 ? [shown] : []
  };
  try { return run(); } finally { globalThis.document = previous; }
}

for (const [index, name, action, from, to] of [
  [4, "pickup", "hand-to-courier", "ReadyForPickup", "OnTheWay"],
  [5, "delivery", "mark-delivered", "OnTheWay", "Delivered"]
]) {
  test("the courier " + name + " step is passive: no user action, bounded wait, done once the demo shows " + to, function () {
    const waiting = deliverySteps[index];
    assert.equal(tour.waitsOnDemo(waiting), true, "no Next button while the courier works");
    assert.equal(tour.matchesTourEvent(waiting, "wasla:order-action-completed", { action, demo: true }), false,
      "no user action can complete this step");
    assert.equal(tour.shouldStopWaiting(waiting, false, 2), true, "an expired or removed demo ends the wait");
    assert.equal(tour.shouldStopWaiting(waiting, true, 9), false, "a slow Worker leaves the step stable across polls");

    withDocument(["[data-wasla-demo][data-order-status='" + from + "']"], () => {
      assert.equal(tour.stepCompleted(waiting), false);
    });
    withDocument(["[data-wasla-demo][data-order-status='" + to + "']"], () => {
      assert.equal(tour.stepCompleted(waiting), true);
    });
  });
}

test("reloading resumes at the pickup wait, the delivery wait, or the success step", function () {
  withDocument(["[data-wasla-demo][data-order-status='ReadyForPickup']"], () => {
    assert.equal(tour.resumeIndex(deliverySteps), 4);
  });
  withDocument(["[data-wasla-demo][data-order-status='OnTheWay']"], () => {
    assert.equal(tour.resumeIndex(deliverySteps), 5);
  });
  withDocument(["[data-wasla-demo][data-order-status='Delivered']"], () => {
    assert.equal(tour.resumeIndex(deliverySteps), 6);
  });
});

test("a replay starts a new training; an open demo resumes its step; a first-run reload resumes success", function () {
  assert.equal(tour.startIndex(deliverySteps, 6, false, true), 0, "replay while the old delivered demo is still shown");
  assert.equal(tour.startIndex(deliverySteps, -1, false, true), 0, "replay after skipping");
  assert.equal(tour.startIndex(deliverySteps, 5, true, true), 5, "replay while a demo is on the way resumes it");
  assert.equal(tour.startIndex(deliverySteps, 4, true, false), 4, "reload during the pickup wait");
  assert.equal(tour.startIndex(deliverySteps, 0, true, false), 1, "an open demo never restarts at send a demo");
  assert.equal(tour.startIndex(deliverySteps, 6, false, false), 6, "first-run reload after delivery shows success");
});

test("Skip training asks for confirmation when the tour has a dialog; Escape never skips", function () {
  const demoTour = { finishUrl: "/orders/demo/finish", skipConfirmModal: "#waslaTourSkipModal" };
  assert.equal(tour.skipNeedsConfirmation(demoTour, true), true, "the first-run demo confirms the permanent opt-out");
  assert.equal(tour.skipNeedsConfirmation(demoTour, false), false, "without a dialog, Skip still works");
  assert.equal(tour.skipNeedsConfirmation({ finishUrl: null }, true), false, "screen intro tours skip at once");
  assert.equal(tour.exitPlan("escape").persist, false, "Escape sends no completion or finish request");
  assert.equal(tour.exitPlan("skip").persist, true, "a confirmed Skip opts out and ends the demo");
});

test("a guided demo completes on its success step however that step is reached; other tours on Done", function () {
  const demo = { finishUrl: "/orders/demo/finish" };
  assert.equal(tour.completesTraining(demo, 6, 7), true, "success step, including after a pause or reload");
  assert.equal(tour.completesTraining(demo, 5, 7), false, "waiting for delivery is not completion");
  assert.equal(tour.completesTraining({ finishUrl: null }, 2, 3), false, "screen tours complete only when Done is clicked");
  assert.equal(tour.completesTraining(demo, 0, 1), false);
});

test("show real order focuses the first usable order action, else the order itself", function () {
  const approve = { name: "approve" };
  const card = {
    matches: () => false,
    querySelector: (selector) => selector.indexOf("[data-order-action]:not([disabled])") === 0 ? approve : null
  };
  assert.equal(tour.focusTargetFor(card), approve);

  const noActions = { matches: () => false, querySelector: () => null };
  assert.equal(tour.focusTargetFor(noActions), noActions);

  const focusEntry = { matches: (selector) => selector === "button, a[href]", querySelector: () => approve };
  assert.equal(tour.focusTargetFor(focusEntry), focusEntry, "a Focus queue entry is already a button");
  assert.equal(tour.focusTargetFor(null), null);
});

test("an action event advances only the matching demo step", function () {
  const step = {
    advance: "event",
    eventName: "wasla:order-action-completed",
    eventMatch: { action: "approve", demo: true }
  };
  assert.equal(tour.matchesTourEvent(step, "wasla:order-action-completed", { action: "approve", demo: true }), true);
  assert.equal(tour.matchesTourEvent(step, "wasla:order-action-completed", { action: "approve", demo: false }), false);
  assert.equal(tour.matchesTourEvent(step, "wasla:order-action-completed", { action: "reject", demo: true }), false);
});

test("progress text uses the localized template", function () {
  assert.equal(tour.formatProgress("{0} / {1}", 2, 4), "2 / 4");
});

for (const rtl of [false, true]) {
  test("narrow edge placement stays near the action without covering it, rtl=" + rtl, () => {
    const target = { top: 250, left: 12, width: 90, height: 40 };
    const result = tour.choosePlacement("top", {
      target, popover: { width: 352, height: 180 }, viewport: { width: 390, height: 700 }, rtl
    });
    assert.equal(result.placement, "top");
    assert.equal(result.top + 180, target.top - 12);
    assert.ok(result.left >= 12 && result.left + 352 <= 378);
  });
  test("wide layout follows the action instead of the column edge, rtl=" + rtl, () => {
    const result = tour.choosePlacement("top", {
      target: { top: 400, left: 1300, width: 100, height: 40 },
      popover: { width: 352, height: 180 }, viewport: { width: 1920, height: 1080 }, rtl
    });
    assert.equal(result.left + 176, 1350);
    assert.equal(result.top, 208);
  });
}
