(() => {
  const LS_KEY = "Wasla.layout";
  const LS_DEBUG = "Wasla.layoutDebug";

  function isLayoutDebug() {
    return localStorage.getItem(LS_DEBUG) === "true";
  }

  function layoutDebug(...args) {
    if (isLayoutDebug()) {
      console.debug("[OrderHub layout]", ...args);
    }
  }

  function getMode() {
    const v = localStorage.getItem(LS_KEY);
    return v === "navbar" || v === "sidebar" ? v : "sidebar";
  }

  function setMode(mode) {
    localStorage.setItem(LS_KEY, mode);
  }

  function applyMode(mode) {
    document.documentElement.classList.remove("layout-sidebar", "layout-navbar");
    document.documentElement.classList.add("layout-" + mode);
    layoutDebug("applyMode", mode, document.documentElement.className);
  }

  function updateLayoutToggleButton(mode) {
    const btn = document.getElementById("layoutToggle");
    if (!btn) return;

    const icon = btn.querySelector("i");
    if (icon) {
      icon.className = mode === "sidebar" ? "bi bi-layout-sidebar-inset" : "bi bi-layout-text-sidebar-reverse";
    }

    const nextLabel = mode === "sidebar"
      ? "Switch to top navigation"
      : "Switch to sidebar navigation";
    btn.setAttribute("title", nextLabel);
    btn.setAttribute("aria-label", nextLabel);
  }

  function toggleMode() {
    const next = getMode() === "sidebar" ? "navbar" : "sidebar";
    setMode(next);
    applyMode(next);
    updateLayoutToggleButton(next);
    layoutDebug("toggleMode ->", next);
  }

  function updateTopbarHeightVar() {
    const el = document.getElementById("ohTopbar") || document.querySelector("header.oh-topbar");
    if (!el) return;
    const h = Math.round(el.getBoundingClientRect().height);
    if (h > 0) {
      document.documentElement.style.setProperty("--oh-topbar-h", h + "px");
      layoutDebug("topbar height (CSS var --oh-topbar-h)", h);
    }
  }

  document.addEventListener("DOMContentLoaded", () => {
    const initial = getMode();
    applyMode(initial);
    updateLayoutToggleButton(initial);
    updateTopbarHeightVar();
    window.addEventListener("resize", () => {
      updateTopbarHeightVar();
    }, { passive: true });

    const toggleBtn = document.getElementById("layoutToggle");
    if (toggleBtn) {
      toggleBtn.addEventListener("click", () => toggleMode());
    } else {
      layoutDebug("no #layoutToggle — toggle handler not attached");
    }

    if (!document.querySelector(".oh-sidebar")) {
      if (isLayoutDebug()) {
        console.warn("[OrderHub layout] .oh-sidebar is missing; sidebar mode layout will look wrong.");
      }
    }

    const hamburger = document.querySelector(".oh-hamburger");
    if (hamburger) {
      hamburger.addEventListener("click", () => {
        document.body.classList.toggle("sidebar-open");
        layoutDebug("hamburger", document.body.classList.contains("sidebar-open") ? "open" : "closed");
      });
    }

    document.querySelectorAll(".oh-side-link").forEach((a) => {
      a.addEventListener("click", () => {
        document.body.classList.remove("sidebar-open");
        layoutDebug("oh-side-link click, closed mobile sidebar");
      });
    });
  });
})();
