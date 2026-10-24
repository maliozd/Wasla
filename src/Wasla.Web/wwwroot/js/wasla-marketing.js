// Public marketing header (WAS-155): the mobile menu and the disclosure menus (products, language).
// Everything opens on click or keyboard, never on hover, and Escape closes the open menu and returns focus.
(function () {
  "use strict";

  var root = document.documentElement;
  root.classList.remove("wm-no-js");

  function closeDisclosure(button, restoreFocus) {
    var panel = document.getElementById(button.getAttribute("aria-controls"));
    if (!panel || panel.hidden) return;
    panel.hidden = true;
    button.setAttribute("aria-expanded", "false");
    if (restoreFocus) button.focus();
  }

  function openDisclosures() {
    return Array.prototype.slice.call(document.querySelectorAll("[data-wm-disclosure][aria-expanded='true']"));
  }

  function init() {
    var disclosures = document.querySelectorAll("[data-wm-disclosure]");
    Array.prototype.forEach.call(disclosures, function (button) {
      var panel = document.getElementById(button.getAttribute("aria-controls"));
      if (!panel) return;
      panel.hidden = true;
      button.setAttribute("aria-expanded", "false");

      button.addEventListener("click", function () {
        var willOpen = panel.hidden;
        openDisclosures().forEach(function (other) {
          if (other !== button) closeDisclosure(other, false);
        });
        panel.hidden = !willOpen;
        button.setAttribute("aria-expanded", willOpen ? "true" : "false");
      });

      var item = button.parentElement;
      item.addEventListener("focusout", function (event) {
        if (event.relatedTarget && !item.contains(event.relatedTarget)) closeDisclosure(button, false);
      });
    });

    var toggle = document.querySelector("[data-wm-menu-toggle]");
    var nav = toggle ? document.getElementById(toggle.getAttribute("aria-controls")) : null;

    function setMenu(open, restoreFocus) {
      if (!toggle || !nav) return;
      nav.classList.toggle("is-open", open);
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
      toggle.setAttribute("aria-label", open ? toggle.getAttribute("data-label-close") : toggle.getAttribute("data-label-open"));
      if (!open && restoreFocus) toggle.focus();
    }

    if (toggle && nav) {
      toggle.addEventListener("click", function () {
        setMenu(toggle.getAttribute("aria-expanded") !== "true", false);
      });

      // Following an in-page link closes the menu so the target is not hidden behind it.
      nav.addEventListener("click", function (event) {
        var link = event.target.closest ? event.target.closest("a[href]") : null;
        if (link && toggle.getAttribute("aria-expanded") === "true") setMenu(false, false);
      });

      var desktop = window.matchMedia("(min-width: 1100px)");
      var onChange = function (query) {
        if (query.matches) setMenu(false, false);
      };
      if (desktop.addEventListener) desktop.addEventListener("change", onChange);
      else if (desktop.addListener) desktop.addListener(onChange);
    }

    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      var open = openDisclosures();
      if (open.length) {
        open.forEach(function (button) { closeDisclosure(button, true); });
        return;
      }
      if (toggle && toggle.getAttribute("aria-expanded") === "true") setMenu(false, true);
    });

    document.addEventListener("click", function (event) {
      openDisclosures().forEach(function (button) {
        if (!button.parentElement.contains(event.target)) closeDisclosure(button, false);
      });
    });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
