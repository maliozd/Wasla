/*
 * Central Admin mobile navigation (loaded only by _AdminLayout, after adminlte.js).
 *
 * AdminLTE's PushMenu owns the drawer's state: below the lg breakpoint the sidebar is off-canvas, the
 * [data-lte-toggle="sidebar"] buttons switch body.sidebar-open / body.sidebar-collapse, and its .sidebar-overlay
 * backdrop collapses the drawer when clicked or touched. This script adds no second state and no toggle click
 * handler. It only follows those classes and supplies what PushMenu lacks:
 *   - aria-expanded on the toggle;
 *   - inert (and aria-hidden) on the off-canvas drawer while it is closed, and on the header and page content while it
 *     is open, so keyboard focus can neither reach hidden links nor fall behind the open drawer;
 *   - focus moves into the drawer when it opens and back to the toggle when it closes;
 *   - Escape and a click on a drawer link close it (through PushMenu's own collapse()).
 * Background scrolling is locked in CSS from the same classes (wasla-admin-layout.css). Desktop is untouched.
 */
(function () {
  "use strict";

  var MOBILE_QUERY = "(max-width: 991.98px)";
  var OPEN = "sidebar-open";

  function init() {
    var root = document.documentElement;
    if (root.getAttribute("data-wasla-admin-nav") === "ready") {
      return;
    }

    var body = document.body;
    var sidebar = document.getElementById("waslaAdminSidebar");
    var toggle = document.getElementById("waslaAdminNavToggle");
    var header = document.getElementById("waslaAppHeader");
    var main = document.querySelector(".app-wrapper > .app-main");
    if (!sidebar || !toggle) {
      return;
    }
    root.setAttribute("data-wasla-admin-nav", "ready");

    var media = window.matchMedia(MOBILE_QUERY);
    var wasOpen = false;

    function isOpen() {
      return media.matches && body.classList.contains(OPEN);
    }

    function setInert(element, value) {
      if (!element) {
        return;
      }
      element.inert = value;
      if (value) {
        element.setAttribute("aria-hidden", "true");
      } else {
        element.removeAttribute("aria-hidden");
      }
    }

    // A breakpoint change is not a user action: it updates the state without moving focus. (Crossing to mobile,
    // AdminLTE's own resize handler collapses its desktop sidebar-open right after.)
    function sync(fromBreakpoint) {
      var mobile = media.matches;
      var open = isOpen();

      toggle.setAttribute("aria-expanded", open ? "true" : "false");
      // Mobile and closed: the drawer is off-canvas, so its links must not take focus.
      setInert(sidebar, mobile && !open);
      // Mobile and open: everything behind the drawer is out of reach until it closes.
      setInert(header, open);
      setInert(main, open);

      if (fromBreakpoint === true) {
        wasOpen = false;
        return;
      }
      if (open && !wasOpen) {
        var first = sidebar.querySelector("[data-wasla-admin-nav-close], a[href], button:not([disabled])");
        if (first) {
          first.focus();
        }
      } else if (!open && wasOpen) {
        var active = document.activeElement;
        if (mobile && (!active || active === body || sidebar.contains(active))) {
          toggle.focus();
        }
      }
      wasOpen = open;
    }

    function collapse() {
      if (!isOpen()) {
        return;
      }
      if (window.adminlte && typeof window.adminlte.PushMenu === "function") {
        new window.adminlte.PushMenu(sidebar, {}).collapse();
      }
    }

    new MutationObserver(function () { sync(false); }).observe(body, { attributes: true, attributeFilter: ["class"] });
    if (typeof media.addEventListener === "function") {
      media.addEventListener("change", function () { sync(true); });
    } else if (typeof media.addListener === "function") {
      media.addListener(function () { sync(true); });
    }

    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape" || !isOpen()) {
        return;
      }
      // AdminLTE's own Escape handling closes an open dropdown or modal first.
      if (document.querySelector(".modal.show, .dropdown-menu.show")) {
        return;
      }
      event.preventDefault();
      collapse();
    });

    // A navigation link closes the drawer (also when it opens in a new tab).
    sidebar.addEventListener("click", function (event) {
      var link = event.target.closest ? event.target.closest("a[href]") : null;
      if (link && sidebar.contains(link) && isOpen()) {
        collapse();
      }
    });

    sync(true);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
