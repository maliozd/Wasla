(function () {
  "use strict";

  var STORAGE_KEY = "Wasla.sidebarCollapsed";

  function isCollapsed() {
    return localStorage.getItem(STORAGE_KEY) === "true";
  }

  function setCollapsed(v) {
    try {
      localStorage.setItem(STORAGE_KEY, v ? "true" : "false");
    } catch (e) { /* no-op */ }
  }

  function apply(collapsed) {
    document.body.classList.toggle("sidebar-collapsed", collapsed);
    var btn = document.getElementById("tenantSidebarToggle");
    if (btn) {
      var expandT = btn.getAttribute("data-title-expand") || "";
      var collapseT = btn.getAttribute("data-title-collapse") || "";
      btn.setAttribute("title", collapsed ? expandT : collapseT);
      var toggleLbl = btn.getAttribute("data-label-toggle") || "";
      if (toggleLbl) {
        btn.setAttribute("aria-label", toggleLbl);
      }
      btn.setAttribute("aria-expanded", collapsed ? "false" : "true");
    }
    if (window.dispatchEvent) {
      window.dispatchEvent(new Event("resize"));
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    var btn = document.getElementById("tenantSidebarToggle");
    apply(isCollapsed());
    if (btn) {
      btn.addEventListener("click", function (e) {
        e.preventDefault();
        var next = !document.body.classList.contains("sidebar-collapsed");
        setCollapsed(next);
        apply(next);
      });
    }
  });
})();
