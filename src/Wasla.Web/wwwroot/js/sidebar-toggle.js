(function () {
  "use strict";

  var STORAGE_KEY = "Wasla.sidebarCollapsed";
  var MOBILE_MQ = "(max-width: 991.98px)";
  var OPEN_CLASS = "wasla-nav-open";
  var LOCK_CLASS = "wasla-nav-lock";
  var SHELL_DESKTOP_CLASS = "wasla-shell-desktop";
  var SHELL_MOBILE_CLASS = "wasla-shell-mobile";

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
  }

  function isMobileNav() {
    return window.matchMedia(MOBILE_MQ).matches;
  }

  document.addEventListener("DOMContentLoaded", function () {
    var btn = document.getElementById("tenantSidebarToggle");
    apply(isCollapsed());
    if (btn) {
      btn.addEventListener("click", function (e) {
        e.preventDefault();
        if (isMobileNav()) {
          return;
        }
        var next = !document.body.classList.contains("sidebar-collapsed");
        setCollapsed(next);
        apply(next);
      });
    }

    initMobileNav();
  });

  function initMobileNav() {
    if (!document.body.classList.contains("wasla-tenant-shell")) {
      return;
    }

    var sidebar = document.getElementById("app-sidebar-tenant");
    var main = document.getElementById("tenant-main");
    var wrapper = document.querySelector(".app-wrapper");
    var openBtn = document.getElementById("tenantNavOpen");
    var closeBtn = document.getElementById("tenantNavClose");
    var backdrop = document.getElementById("tenantNavBackdrop");
    if (!sidebar || !openBtn || openBtn.getAttribute("data-wasla-nav-owner") === "sidebar-toggle") {
      return;
    }
    openBtn.setAttribute("data-wasla-nav-owner", "sidebar-toggle");

    var media = window.matchMedia(MOBILE_MQ);

    function setExpanded(open) {
      openBtn.setAttribute("aria-expanded", open ? "true" : "false");
      openBtn.setAttribute(
        "aria-label",
        open
          ? (closeBtn && closeBtn.getAttribute("aria-label")) || openBtn.getAttribute("aria-label") || ""
          : openBtn.getAttribute("data-label-open") || openBtn.getAttribute("aria-label") || ""
      );
    }

    if (!openBtn.getAttribute("data-label-open")) {
      openBtn.setAttribute("data-label-open", openBtn.getAttribute("aria-label") || "");
    }

    function isFocusInside(el) {
      return !!(el && document.activeElement && el.contains(document.activeElement));
    }

    function moveFocusToTrigger() {
      if (openBtn && typeof openBtn.focus === "function") {
        openBtn.focus();
      }
    }

    function revealSidebar() {
      if ("inert" in sidebar) {
        sidebar.inert = false;
      }
      sidebar.removeAttribute("aria-hidden");
    }

    function hideSidebarAfterFocusMoved() {
      if (isFocusInside(sidebar)) {
        moveFocusToTrigger();
      }
      if (media.matches) {
        sidebar.setAttribute("aria-hidden", "true");
        if ("inert" in sidebar) {
          sidebar.inert = true;
        }
      } else {
        sidebar.removeAttribute("aria-hidden");
        if ("inert" in sidebar) {
          sidebar.inert = false;
        }
      }
    }

    function setMainInert(locked) {
      if (main && "inert" in main) {
        main.inert = !!locked;
      }
      if (wrapper && "inert" in wrapper) {
        wrapper.inert = false;
      }
      if ("inert" in document.body) {
        document.body.inert = false;
      }
    }

    function setOpenChrome(open) {
      document.body.classList.toggle(OPEN_CLASS, open);
      document.body.classList.toggle(LOCK_CLASS, open);
      if (backdrop) {
        backdrop.hidden = !open;
      }
      setExpanded(open);
    }

    function applyShellBreakpoint(isMobile) {
      document.body.classList.toggle(SHELL_MOBILE_CLASS, isMobile);
      document.body.classList.toggle(SHELL_DESKTOP_CLASS, !isMobile);
    }

    var INLINE_LAYOUT_PROPS = [
      "left",
      "right",
      "top",
      "bottom",
      "inset",
      "inset-inline-start",
      "inset-inline-end",
      "transform",
      "margin",
      "margin-left",
      "margin-right",
      "margin-inline-start",
      "margin-inline-end",
      "width",
      "max-width",
      "min-width",
      "position"
    ];

    function clearInlineLayout(el) {
      if (!el || !el.style) {
        return;
      }
      for (var i = 0; i < INLINE_LAYOUT_PROPS.length; i++) {
        el.style.removeProperty(INLINE_LAYOUT_PROPS[i]);
      }
    }

    function resetViewportScroll() {
      if (typeof window.scrollTo === "function") {
        window.scrollTo(0, window.scrollY || window.pageYOffset || 0);
      }
    }

    function restoreDesktopShell() {
      var closeHasFocus = !!(closeBtn && document.activeElement === closeBtn);
      setMainInert(false);
      if (closeHasFocus && document.activeElement && typeof document.activeElement.blur === "function") {
        document.activeElement.blur();
      }
      setOpenChrome(false);
      sidebar.removeAttribute("aria-hidden");
      if ("inert" in sidebar) {
        sidebar.inert = false;
      }
      clearInlineLayout(sidebar);
      clearInlineLayout(main);
      if (wrapper) {
        clearInlineLayout(wrapper);
      }
      applyShellBreakpoint(false);
      apply(isCollapsed());
      resetViewportScroll();
    }

    function closeNav(restoreFocus) {
      var wasOpen = document.body.classList.contains(OPEN_CLASS);
      if (isFocusInside(sidebar) || (restoreFocus && wasOpen)) {
        moveFocusToTrigger();
      }
      setMainInert(false);
      hideSidebarAfterFocusMoved();
      setOpenChrome(false);
    }

    function openNav() {
      if (!media.matches) {
        return;
      }
      revealSidebar();
      setOpenChrome(true);
      setMainInert(true);
      var focusTarget = closeBtn || sidebar.querySelector("a, button");
      if (focusTarget && typeof focusTarget.focus === "function") {
        focusTarget.focus();
      }
    }

    function onBreakpointChange() {
      clearInlineLayout(sidebar);
      clearInlineLayout(main);
      if (wrapper) {
        clearInlineLayout(wrapper);
      }
      if (!media.matches) {
        restoreDesktopShell();
        return;
      }
      applyShellBreakpoint(true);
      closeNav(false);
      apply(isCollapsed());
      resetViewportScroll();
    }

    openBtn.addEventListener("click", function (e) {
      e.preventDefault();
      if (document.body.classList.contains(OPEN_CLASS)) {
        closeNav(true);
      } else {
        openNav();
      }
    });

    if (closeBtn) {
      closeBtn.addEventListener("click", function (e) {
        e.preventDefault();
        closeNav(true);
      });
    }

    if (backdrop) {
      backdrop.addEventListener("click", function () {
        closeNav(true);
      });
    }

    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && document.body.classList.contains(OPEN_CLASS)) {
        e.preventDefault();
        closeNav(true);
      }
    });

    sidebar.addEventListener("click", function (e) {
      if (!media.matches || !document.body.classList.contains(OPEN_CLASS)) {
        return;
      }
      var navLink = e.target.closest("a[href]");
      if (!navLink || !sidebar.contains(navLink)) {
        return;
      }
      closeNav(true);
    });

    if (typeof media.addEventListener === "function") {
      media.addEventListener("change", onBreakpointChange);
    } else if (typeof media.addListener === "function") {
      media.addListener(onBreakpointChange);
    }

    onBreakpointChange();
  }
})();
