// Public marketing header (WAS-155): the mobile menu and the disclosure menus (products, language).
// Everything opens on click or keyboard, never on hover, and Escape closes the open menu and returns focus.
// Homepage pricing (issue #65): the billing period tabs.
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

  // The server renders every plan card with both periods' prices, already formatted, and the monthly ones visible.
  // These tabs only choose which period's block is shown; nothing here computes or derives a price. The tabs stay
  // hidden until this has run, so without JavaScript the monthly prices show and there is no control that does nothing.
  function initPeriodTabs() {
    var tablist = document.querySelector("[data-wm-period-tabs]");
    if (!tablist) return;
    var tabs = Array.prototype.slice.call(tablist.querySelectorAll("[role='tab']"));
    var panel = tabs.length ? document.getElementById(tabs[0].getAttribute("aria-controls")) : null;
    if (!panel) return;
    var status = document.querySelector("[data-wm-period-status]");
    var current = tabs.filter(function (tab) { return tab.getAttribute("aria-selected") === "true"; })[0] || tabs[0];

    function select(tab, announce) {
      var period = tab.getAttribute("data-period");
      var changed = tab !== current;
      current = tab;
      tabs.forEach(function (other) {
        var selected = other === tab;
        other.setAttribute("aria-selected", selected ? "true" : "false");
        other.tabIndex = selected ? 0 : -1;
      });
      panel.setAttribute("aria-labelledby", tab.id);
      Array.prototype.forEach.call(panel.querySelectorAll("[data-plan-period]"), function (block) {
        block.hidden = block.getAttribute("data-plan-period") !== period;
      });
      if (announce && changed && status) status.textContent = tab.getAttribute("data-announce") || "";
    }

    tabs.forEach(function (tab) {
      tab.addEventListener("click", function () { select(tab, true); });
    });

    // Roving focus: the arrow keys (mirrored right to left), Home and End move to a tab and select it.
    tablist.addEventListener("keydown", function (event) {
      var index = tabs.indexOf(document.activeElement);
      if (index < 0) return;
      var forward = window.getComputedStyle(tablist).direction === "rtl" ? "ArrowLeft" : "ArrowRight";
      var backward = forward === "ArrowLeft" ? "ArrowRight" : "ArrowLeft";
      var next;
      if (event.key === forward) next = (index + 1) % tabs.length;
      else if (event.key === backward) next = (index - 1 + tabs.length) % tabs.length;
      else if (event.key === "Home") next = 0;
      else if (event.key === "End") next = tabs.length - 1;
      else return;
      event.preventDefault();
      tabs[next].focus();
      select(tabs[next], true);
    });

    panel.setAttribute("role", "tabpanel");
    select(current, false);
    tablist.hidden = false;
  }

  function start() {
    init();
    initPeriodTabs();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
