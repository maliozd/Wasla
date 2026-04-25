(() => {
  const LS_KEY = "orderhub.layout";

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
  }

  function toggleMode() {
    const next = getMode() === "sidebar" ? "navbar" : "sidebar";
    setMode(next);
    applyMode(next);
  }

  document.addEventListener("DOMContentLoaded", () => {
    // Ensure applied even if inline script failed
    applyMode(getMode());

    const toggleBtn = document.getElementById("layoutToggle");
    if (toggleBtn) {
      toggleBtn.addEventListener("click", () => toggleMode());
    }

    const hamburger = document.querySelector(".oh-hamburger");
    if (hamburger) {
      hamburger.addEventListener("click", () => {
        document.body.classList.toggle("sidebar-open");
      });
    }

    // Close sidebar after navigation on mobile
    document.querySelectorAll(".oh-side-link").forEach((a) => {
      a.addEventListener("click", () => {
        document.body.classList.remove("sidebar-open");
      });
    });
  });
})();

