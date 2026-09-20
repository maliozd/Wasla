// Live Screen view switch: operational cards or compact list.
// One rendered card tree is reused; the switch only changes the host layout class,
// so no order state, polling, sound, highlight, or notification state is reset.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  const VIEWS = ["cards", "list"];
  const DEFAULT_VIEW = "cards";
  const STORAGE_KEY_FALLBACK = "Wasla.liveScreen.viewMode";

  let currentView = DEFAULT_VIEW;

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }

  function storageKey() {
    return O.opts.viewModeStorageKey || STORAGE_KEY_FALLBACK;
  }

  function normalize(view) {
    return VIEWS.indexOf(view) >= 0 ? view : DEFAULT_VIEW;
  }

  function readStoredView() {
    try {
      return normalize(localStorage.getItem(storageKey()));
    } catch (e) {
      return DEFAULT_VIEW;
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
      el.classList.toggle("oh-live-screen-host--cards", currentView === "cards");
      el.classList.toggle("oh-live-screen-host--list", currentView === "list");
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

  function setView(view, options) {
    const opts = options || {};
    currentView = normalize(view);
    if (opts.persist !== false) persistView(currentView);
    applyView();
  }

  function init() {
    if (!isLiveDisplayPage()) return;

    setView(readStoredView(), { persist: false });

    // Single delegated listener: switch buttons live in the static header, never re-rendered by polling.
    document.addEventListener("click", function (ev) {
      const target = ev.target;
      const btn = target && target.closest ? target.closest("button[data-live-screen-view]") : null;
      if (!btn) return;
      const view = btn.getAttribute("data-live-screen-view");
      if (!view || view === currentView) return;
      setView(view);
    });
  }

  O.liveView = {
    getView: function () { return currentView; },
    setView: setView
  };

  document.addEventListener("DOMContentLoaded", init);
})(window);
