(function (root, factory) {
  "use strict";

  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document) api.init(root.document);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  // The whole user list is rendered on the page (no pagination), so sorting is client-side and stable.
  // Keys: name (display name, else email), role (permission level: Owner first), created (date added).
  var FIRST_DIRECTION = { name: "asc", role: "asc", created: "desc" };

  function fold(value) {
    return String(value || "")
      .replace(/İ/g, "i")
      .replace(/I/g, "i")
      .replace(/ı/g, "i")
      .normalize("NFD")
      .replace(/[̀-ͯ]/g, "")
      .toLocaleLowerCase();
  }

  function primary(row, key, collator) {
    return function (other) {
      if (key === "role") return Number(row.role) - Number(other.role);
      if (key === "created") return String(row.created).localeCompare(String(other.created));
      return collator.compare(row.name, other.name);
    };
  }

  /**
   * Compares two rows ({ name, role, created, email, index }) by key and direction. Ties fall back to
   * the email (unique per user) and then the rendered position, so the order never depends on chance.
   */
  function compareRows(a, b, key, direction, collator) {
    var c = collator || new Intl.Collator(undefined, { sensitivity: "base", numeric: true });
    var result = primary(a, key, c)(b);
    if (direction === "desc") result = -result;
    if (result !== 0) return result;
    result = c.compare(a.email, b.email);
    if (result !== 0) return result;
    return a.index - b.index;
  }

  function nextDirection(key, currentKey, currentDirection) {
    if (key !== currentKey) return FIRST_DIRECTION[key] || "asc";
    return currentDirection === "asc" ? "desc" : "asc";
  }

  function init(doc) {
    var root = doc.querySelector("[data-users-index]");
    if (!root) return;

    var search = root.querySelector("[data-users-filter=\"search\"]");
    var role = root.querySelector("[data-users-filter=\"role\"]");
    var status = root.querySelector("[data-users-filter=\"status\"]");
    var body = root.querySelector("[data-users-table-body]");
    var rows = Array.prototype.slice.call(root.querySelectorAll("[data-user-row]"));
    var tableWrap = root.querySelector(".wasla-table-wrap");
    var noResults = root.querySelector("[data-users-no-results]");

    if (!rows.length) return;

    var collator = new Intl.Collator(doc.documentElement.lang || undefined, { sensitivity: "base", numeric: true });
    var entries = rows.map(function (row, index) {
      return {
        row: row,
        name: row.getAttribute("data-sort-name") || "",
        role: row.getAttribute("data-sort-role") || "0",
        created: row.getAttribute("data-sort-created") || "",
        email: row.getAttribute("data-sort-email") || "",
        index: index
      };
    });
    var sortKey = null;
    var sortDirection = null;

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

    function renderSortState() {
      root.querySelectorAll("[data-users-sort]").forEach(function (button) {
        var key = button.getAttribute("data-users-sort");
        var header = button.closest("th");
        var icon = button.querySelector(".wasla-table__sort-icon");
        var active = key === sortKey;
        if (header) {
          if (active) header.setAttribute("aria-sort", sortDirection === "asc" ? "ascending" : "descending");
          else header.removeAttribute("aria-sort");
        }
        button.classList.toggle("is-active", active);
        if (icon) {
          icon.classList.remove("bi-arrow-up", "bi-arrow-down", "bi-arrow-down-up");
          icon.classList.add(active ? (sortDirection === "asc" ? "bi-arrow-up" : "bi-arrow-down") : "bi-arrow-down-up");
        }
      });
    }

    function sortBy(key) {
      sortDirection = nextDirection(key, sortKey, sortDirection);
      sortKey = key;
      entries.slice()
        .sort(function (a, b) { return compareRows(a, b, sortKey, sortDirection, collator); })
        .forEach(function (entry) { if (body) body.appendChild(entry.row); });
      renderSortState();
    }

    ["input", "change"].forEach(function (eventName) {
      root.addEventListener(eventName, function (event) {
        var target = event.target;
        if (!target || !target.getAttribute) return;
        if (!target.getAttribute("data-users-filter")) return;
        applyFilter();
      });
    });

    root.addEventListener("click", function (event) {
      var button = event.target && event.target.closest ? event.target.closest("[data-users-sort]") : null;
      if (!button || !root.contains(button)) return;
      sortBy(button.getAttribute("data-users-sort"));
    });
  }

  return { compareRows: compareRows, nextDirection: nextDirection, fold: fold, init: init };
});
