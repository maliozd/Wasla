(function () {
  "use strict";

  const STORAGE_KEY = "orderhub.theme";
  const DEFAULT_THEME = "light";

  function getStoredTheme() {
    try {
      return localStorage.getItem(STORAGE_KEY) || DEFAULT_THEME;
    } catch {
      return DEFAULT_THEME;
    }
  }

  function setStoredTheme(value) {
    try {
      localStorage.setItem(STORAGE_KEY, value);
    } catch { /* no-op */ }
  }

  function applyTheme(name) {
    const t = name === "dark" ? "dark" : "light";
    document.documentElement.setAttribute("data-bs-theme", t);
    document.body && document.body.setAttribute("data-bs-theme", t);
    setStoredTheme(t);
    updateToggles();
  }

  function setTopbarVar() {
    const el = document.getElementById("ohAppHeader");
    if (!el) return;
    const h = Math.round(el.getBoundingClientRect().height);
    document.documentElement.style.setProperty(
      "--oh-topbar-h",
      (h > 0 ? h : 56) + "px"
    );
  }

  function updateToggles() {
    const t = getStoredTheme();
    document.querySelectorAll(".oh-theme-toggle").forEach(function (btn) {
      btn.setAttribute("aria-pressed", t === "dark" ? "true" : "false");
      if (t === "dark") {
        btn.setAttribute("title", btn.getAttribute("data-title-dark") || "");
      } else {
        btn.setAttribute("title", btn.getAttribute("data-title-light") || "");
      }
    });
  }

  function bindToggles() {
    document.addEventListener("click", function (e) {
      const btn = e.target.closest(".oh-theme-toggle");
      if (!btn) return;
      e.preventDefault();
      const next = getStoredTheme() === "dark" ? "light" : "dark";
      applyTheme(next);
    });
  }

  window.addEventListener("resize", setTopbarVar);
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () {
      applyTheme(getStoredTheme());
      setTopbarVar();
    });
  } else {
    applyTheme(getStoredTheme());
    setTopbarVar();
  }

  bindToggles();
})();
