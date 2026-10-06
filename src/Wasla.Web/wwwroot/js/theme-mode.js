(function () {
  "use strict";

  // The theme itself is resolved, applied and stored by theme-preference.js (loaded in <head>).
  // This script only reflects it on the toggle buttons and turns a click into an explicit choice.
  const theme = window.WaslaTheme || null;

  function setTopbarVar() {
    const el = document.getElementById("waslaAppHeader");
    if (!el) {
      if (document.body && document.body.classList.contains("wasla-tenant")) {
        document.documentElement.style.setProperty("--wasla-topbar-h", "0px");
      }
      return;
    }
    const h = Math.round(el.getBoundingClientRect().height);
    document.documentElement.style.setProperty(
      "--wasla-topbar-h",
      (h > 0 ? h : 56) + "px"
    );
  }

  function updateToggles() {
    if (!theme) return;
    const t = theme.current().theme;
    document.querySelectorAll(".wasla-theme-toggle").forEach(function (btn) {
      btn.setAttribute("aria-pressed", t === "dark" ? "true" : "false");
      if (t === "dark") {
        btn.setAttribute("title", btn.getAttribute("data-go-light") || "");
      } else {
        btn.setAttribute("title", btn.getAttribute("data-go-dark") || "");
      }
      const moon = btn.querySelector(".wasla-theme-moon");
      const sun = btn.querySelector(".wasla-theme-sun");
      if (moon && sun) {
        if (t === "dark") {
          moon.classList.add("d-none");
          sun.classList.remove("d-none");
        } else {
          moon.classList.remove("d-none");
          sun.classList.add("d-none");
        }
      }
    });
  }

  function bindToggles() {
    document.addEventListener("click", function (e) {
      const btn = e.target.closest(".wasla-theme-toggle");
      if (!btn || !theme) return;
      e.preventDefault();
      theme.toggle();
    });
  }

  window.addEventListener("resize", setTopbarVar);
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () {
      updateToggles();
      setTopbarVar();
      requestAnimationFrame(setTopbarVar);
    });
  } else {
    updateToggles();
    setTopbarVar();
    requestAnimationFrame(setTopbarVar);
  }

  // A system-theme change or another tab can change the theme too.
  if (theme) theme.subscribe(updateToggles);
  bindToggles();
})();
