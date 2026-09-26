// One selected view, reconciled from the coordinator's accepted snapshot.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) return;

  const VIEWS = ["board", "list", "focus"];
  const DEFAULT_VIEW = "board";
  const STORAGE_KEY_FALLBACK = "Wasla.liveScreen.viewMode";

  let currentView = DEFAULT_VIEW;

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }

  function storageKey() {
    return O.opts.viewModeStorageKey || STORAGE_KEY_FALLBACK;
  }

  function normalize(view) {
    if (view === "cards") return DEFAULT_VIEW;
    return VIEWS.indexOf(view) >= 0 ? view : DEFAULT_VIEW;
  }

  function readStoredView() {
    try {
      return localStorage.getItem(storageKey());
    } catch (e) {
      return null;
    }
  }

  function persistView(view) {
    try {
      localStorage.setItem(storageKey(), view);
    } catch (e) { /* preference is best-effort */ }
  }

  function host() {
    return document.getElementById("ordersLiveScreenHost");
  }

  function applyView() {
    const el = host();
    if (el) {
      el.classList.toggle("wasla-live-screen-host--board", currentView === "board");
      el.classList.toggle("wasla-live-screen-host--cards", currentView === "cards");
      el.classList.toggle("wasla-live-screen-host--list", currentView === "list");
      el.classList.toggle("wasla-live-screen-host--focus", currentView === "focus");
      el.setAttribute("data-live-screen-view", currentView);
    }

    document.querySelectorAll("[data-live-screen-view]").forEach(function (btn) {
      if (btn === el) return;
      const view = btn.getAttribute("data-live-screen-view");
      const selected = view === currentView;
      btn.classList.toggle("active", selected);
      btn.setAttribute("aria-pressed", selected ? "true" : "false");
    });
  }

  const ENTER_CLASS = "wasla-live-view-enter";
  const FORWARD_CLASS = "wasla-live-view-enter--forward";
  const BACK_CLASS = "wasla-live-view-enter--back";
  let motionToken = 0;

  function prefersReducedMotion() {
    if (!global.matchMedia) return false;
    return global.matchMedia("(prefers-reduced-motion: reduce)").matches === true;
  }

  function viewDirection(from, to) {
    return VIEWS.indexOf(to) < VIEWS.indexOf(from) ? -1 : 1;
  }

  function clearViewMotion(el) {
    el.classList.remove(ENTER_CLASS, FORWARD_CLASS, BACK_CLASS);
  }

  function playViewTransition(direction) {
    const el = host();
    if (!el || !el.classList || prefersReducedMotion()) return;
    motionToken += 1;
    const token = motionToken;
    clearViewMotion(el);
    if (typeof el.offsetWidth === "number") void el.offsetWidth;
    el.classList.add(ENTER_CLASS, direction < 0 ? BACK_CLASS : FORWARD_CLASS);
    function done(event) {
      if (event && event.target !== el) return;
      if (token !== motionToken) return;
      clearViewMotion(el);
      if (el.removeEventListener) el.removeEventListener("animationend", done);
    }
    if (el.addEventListener) el.addEventListener("animationend", done);
  }

  function setView(view, options) {
    const opts = options || {};
    const next = normalize(view);
    const previous = currentView;
    const changed = next !== previous;
    currentView = next;
    if (opts.persist !== false) persistView(currentView);
    applyView();
    if (O.liveStore) O.liveStore.refreshView();
    if (opts.animate && changed) playViewTransition(viewDirection(previous, next));
  }

  function init() {
    if (!isLiveDisplayPage()) return;

    const stored = readStoredView();
    setView(stored == null ? DEFAULT_VIEW : normalize(stored), { persist: stored === "cards" });

    // Single delegated listener: switch buttons live in the static header, never re-rendered by polling.
    document.addEventListener("click", function (ev) {
      const target = ev.target;
      if (!target || !target.closest) return;
      const back = target.closest("[data-focus-back]");
      if (back) {
        if (O.liveStore && O.liveStore.showQueue) O.liveStore.showQueue();
        return;
      }
      const retry = target.closest("[data-focus-detail-retry]");
      if (retry) {
        if (O.liveStore && O.liveStore.retrySelectedDetail) O.liveStore.retrySelectedDetail();
        return;
      }
      const entry = target.closest("[data-focus-select]");
      if (entry && entry.closest("#ordersLiveScreenHost")) {
        const id = entry.getAttribute("data-order-id");
        if (id && O.liveStore && O.liveStore.selectOrder) O.liveStore.selectOrder(id, true);
        return;
      }
      const btn = target.closest("button[data-live-screen-view]");
      if (!btn) return;
      const view = btn.getAttribute("data-live-screen-view");
      if (!view || view === currentView) return;
      setView(view, { animate: true });
    });
  }

  O.liveView = {
    getView: function () { return currentView; },
    setView: setView
  };

  document.addEventListener("DOMContentLoaded", init);
})(window);
