(function () {
  "use strict";

  var root = document.querySelector("[data-users-index]");
  if (!root) return;

  var search = root.querySelector("[data-users-filter=\"search\"]");
  var role = root.querySelector("[data-users-filter=\"role\"]");
  var status = root.querySelector("[data-users-filter=\"status\"]");
  var rows = root.querySelectorAll("[data-user-row]");
  var tableWrap = root.querySelector(".wasla-table-wrap");
  var noResults = root.querySelector("[data-users-no-results]");

  if (!rows.length) return;

  function fold(value) {
    return String(value || "")
      .replace(/İ/g, "i")
      .replace(/I/g, "i")
      .replace(/ı/g, "i")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLocaleLowerCase();
  }

  function setRowVisibility(row, show) {
    row.hidden = !show;
    if ("inert" in row) {
      row.inert = !show;
    }
    row.querySelectorAll("a, button, input, select, textarea").forEach(function (control) {
      if (show) {
        control.removeAttribute("tabindex");
      } else {
        control.setAttribute("tabindex", "-1");
      }
    });
  }

  function applyFilter() {
    var query = fold(search && search.value);
    var roleValue = fold(role && role.value);
    var statusValue = fold(status && status.value);
    var visible = 0;

    rows.forEach(function (row) {
      var haystack = fold(row.getAttribute("data-search"));
      var rowRole = fold(row.getAttribute("data-role"));
      var rowStatus = fold(row.getAttribute("data-status"));
      var matchesQuery = !query || haystack.indexOf(query) !== -1;
      var matchesRole = !roleValue || rowRole === roleValue;
      var matchesStatus = !statusValue || rowStatus === statusValue;
      var show = matchesQuery && matchesRole && matchesStatus;
      setRowVisibility(row, show);
      if (show) visible += 1;
    });

    var empty = visible === 0;
    if (tableWrap) tableWrap.hidden = empty;
    if (noResults) {
      noResults.hidden = !empty;
      noResults.classList.toggle("d-none", !empty);
    }
  }

  ["input", "change"].forEach(function (eventName) {
    root.addEventListener(eventName, function (event) {
      var target = event.target;
      if (!target || !target.getAttribute) return;
      if (!target.getAttribute("data-users-filter")) return;
      applyFilter();
    });
  });
})();
