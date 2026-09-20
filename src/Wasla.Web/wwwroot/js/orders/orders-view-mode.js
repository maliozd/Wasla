// Orders display view modes: table, compact cards, kitchen cards.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  const DEFAULT_STORAGE_KEY = "Wasla.orders.viewMode";
  const FILTERS_OPEN_KEY = "Wasla.orders.filtersOpen";
  const MODES = ["table", "compact", "kitchen"];

  const STATUS_SORT = {
    New: 0,
    Accepted: 1,
    Preparing: 2,
    ReadyForPickup: 3,
    OnTheWay: 4,
    Delivered: 5,
    Cancelled: 6,
    Failed: 7
  };

  const ACTIVE_STATUSES = new Set([
    "New",
    "Accepted",
    "Preparing",
    "ReadyForPickup",
    "OnTheWay"
  ]);

  let currentMode = "table";
  let filtersManuallyOpen = false;

  function localize(key) {
    return O.getMessage(key);
  }

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }

  /** Orders and Live Screen keep separate preferences so one page cannot override the other's default. */
  function storageKey() {
    return O.opts.viewModeStorageKey || DEFAULT_STORAGE_KEY;
  }

  function defaultMode() {
    return MODES.indexOf(O.opts.defaultViewMode) >= 0 ? O.opts.defaultViewMode : "table";
  }

  /** Modes the current page actually offers; a stored mode without a selector would be unreachable. */
  function availableModes() {
    const modes = [];
    document.querySelectorAll("[data-orders-view-mode]").forEach(function (btn) {
      const mode = btn.getAttribute("data-orders-view-mode");
      if (mode && MODES.indexOf(mode) >= 0 && modes.indexOf(mode) < 0) modes.push(mode);
    });
    return modes.length ? modes : MODES.slice();
  }

  function normalizeMode(mode) {
    return MODES.indexOf(mode) >= 0 ? mode : defaultMode();
  }

  function getMode() {
    return currentMode;
  }

  function readStoredMode() {
    try {
      const v = localStorage.getItem(storageKey());
      if (v && availableModes().indexOf(v) >= 0) return v;
    } catch (e) { /* ignore */ }
    return defaultMode();
  }

  function readFiltersOpenState() {
    try {
      return sessionStorage.getItem(FILTERS_OPEN_KEY) === "true";
    } catch (e) {
      return false;
    }
  }

  function persistMode(mode) {
    try {
      localStorage.setItem(storageKey(), mode);
    } catch (e) { /* ignore */ }
  }

  function persistFiltersOpenState(open) {
    try {
      sessionStorage.setItem(FILTERS_OPEN_KEY, open ? "true" : "false");
    } catch (e) { /* ignore */ }
  }

  function statusBadgeClass(status) {
    switch (status) {
      case "New":
        return "oh-dash-status oh-dash-status--new";
      case "Accepted":
        return "oh-dash-status oh-dash-status--accepted";
      case "Preparing":
      case "ReadyForPickup":
      case "OnTheWay":
        return "oh-dash-status oh-dash-status--progress";
      case "Delivered":
        return "oh-dash-status oh-dash-status--delivered";
      case "Cancelled":
        return "oh-dash-status oh-dash-status--cancelled";
      case "Failed":
        return "oh-dash-status oh-dash-status--failed";
      default:
        return "oh-dash-status";
    }
  }

  function parseRowsFromContainer(container) {
    const rows = [];
    if (!container) return rows;

    container.querySelectorAll("tr[data-order-id]").forEach(function (tr) {
      const cells = tr.querySelectorAll("td");
      if (cells.length < 7) return;

      const statusCell = cells[3];
      const statusBadge = statusCell.querySelector("[data-new-badge]");
      let statusText = statusCell.textContent.trim();
      if (statusBadge) {
        statusText = statusText.replace(statusBadge.textContent.trim(), "").trim();
      }

      const itemCount = parseInt(tr.getAttribute("data-item-count") || "0", 10);
      const firstProduct = (tr.getAttribute("data-first-product") || "").trim();

      rows.push({
        id: tr.getAttribute("data-order-id") || "",
        status: tr.getAttribute("data-order-status") || "",
        receivedAt: tr.getAttribute("data-received-at") || "",
        displayImage: tr.getAttribute("data-display-image") || O.opts.defaultFoodImage || "",
        itemCount: isNaN(itemCount) ? 0 : itemCount,
        firstProductName: firstProduct,
        platformHtml: cells[0].innerHTML,
        codeHtml: cells[1].innerHTML,
        customer: cells[2].textContent.trim(),
        statusText: statusText,
        total: cells[4].textContent.trim(),
        received: cells[5].textContent.trim(),
        actionsHtml: cells[6].innerHTML
      });
    });

    return rows;
  }

  function sortRows(rows) {
    return rows.slice().sort(function (a, b) {
      const sa = STATUS_SORT[a.status] != null ? STATUS_SORT[a.status] : 99;
      const sb = STATUS_SORT[b.status] != null ? STATUS_SORT[b.status] : 99;
      if (sa !== sb) return sa - sb;
      return (b.receivedAt || "").localeCompare(a.receivedAt || "");
    });
  }

  function scaleActionsHtml(html, mode) {
    if (!html) return "";
    if (mode === "kitchen") {
      return html.replace(/\bbtn-sm\b/g, "btn-lg");
    }
    if (mode === "compact") {
      return html.replace(/\bbtn-lg\b/g, "btn-sm");
    }
    return html;
  }

  function formatItemsMore(extraCount) {
    const template = localize("itemsMore");
    return template.replace("{0}", String(extraCount));
  }

  function formatCardReceived(row, mode) {
    const iso = row.receivedAt;
    if (iso) {
      try {
        const d = new Date(iso);
        if (!isNaN(d.getTime())) {
          const timeStr = d.toLocaleTimeString(undefined, {
            hour: "2-digit",
            minute: "2-digit",
            timeZone: "Europe/Istanbul"
          });
          const dateStr = d.toLocaleDateString("en-CA", { timeZone: "Europe/Istanbul" });
          const todayYmd = O.opts.todayYmd || "";

          if (mode === "kitchen") {
            if (dateStr === todayYmd) return timeStr;
            return d.toLocaleString(undefined, {
              timeZone: "Europe/Istanbul",
              dateStyle: "short",
              timeStyle: "short"
            });
          }
          if (mode === "compact" && dateStr === todayYmd) {
            return timeStr;
          }

          return d.toLocaleString(undefined, {
            timeZone: "Europe/Istanbul",
            dateStyle: "short",
            timeStyle: "short"
          });
        }
      } catch (_) { /* ignore */ }
    }
    return row.received;
  }

  function buildImageHtml(row, mode) {
    const src = row.displayImage || O.opts.defaultFoodImage || "/images/demo-food/chicken-rice-01.svg";
    const alt = row.firstProductName || row.customer || "";
    return (
      "<div class=\"orders-card-image orders-card-image--" + mode + "\">" +
      "<img src=\"" + O.escapeHtml(src) + "\" alt=\"" + O.escapeHtml(alt) + "\" loading=\"lazy\" />" +
      "</div>"
    );
  }

  function buildTopRowHtml(row) {
    const badge = statusBadgeClass(row.status);
    return (
      "<div class=\"orders-card-top\">" +
      "<div class=\"orders-card-badges\">" +
      "<div class=\"orders-card-platform\">" + row.platformHtml + "</div>" +
      "<span class=\"" + badge + " orders-card-status\">" + O.escapeHtml(row.statusText) + "</span>" +
      "</div>" +
      "</div>"
    );
  }

  function buildActionsHtml(actionsHtml) {
    if (!actionsHtml) return "";
    return "<div class=\"orders-card-actions\">" + actionsHtml + "</div>";
  }

  function buildCompactCard(row) {
    const muted =
      row.status === "Delivered" || row.status === "Cancelled" || row.status === "Failed"
        ? " orders-card--muted"
        : "";
    const actionsHtml = scaleActionsHtml(row.actionsHtml, "compact");
    const receivedDisplay = formatCardReceived(row, "compact");
    const customerLabel = localize("ordersFullscreenCustomer");

    return (
      "<article class=\"orders-card orders-card--compact oh-orders-card oh-orders-card--compact" + muted + "\" role=\"listitem\" data-order-id=\"" + O.escapeHtml(row.id) + "\">" +
      buildImageHtml(row, "compact") +
      "<div class=\"orders-card-body\">" +
      buildTopRowHtml(row) +
      "<div class=\"orders-card-code\">" + row.codeHtml + "</div>" +
      "<div class=\"orders-card-customer\"><span class=\"orders-card-meta__label\">" + O.escapeHtml(customerLabel) + ":</span> " + O.escapeHtml(row.customer) + "</div>" +
      "<div class=\"orders-card-meta orders-card-meta--compact\">" +
      "<span class=\"orders-card-meta__amount\">" + O.escapeHtml(row.total) + "</span>" +
      "<span class=\"orders-card-meta__sep\" aria-hidden=\"true\">·</span>" +
      "<span class=\"orders-card-meta__time\">" + O.escapeHtml(receivedDisplay) + "</span>" +
      "</div>" +
      buildActionsHtml(actionsHtml) +
      "</div>" +
      "</article>"
    );
  }

  function buildKitchenCard(row) {
    const muted =
      row.status === "Delivered" || row.status === "Cancelled" || row.status === "Failed"
        ? " orders-card--muted"
        : "";
    const actionsHtml = scaleActionsHtml(row.actionsHtml, "kitchen");
    const receivedDisplay = formatCardReceived(row, "kitchen");
    const customerLabel = localize("ordersFullscreenCustomer");
    const totalLabel = localize("ordersFullscreenTotal");
    const receivedLabel = localize("ordersFullscreenReceived");
    const moreItemsHtml = row.itemCount > 1
      ? "<div class=\"orders-card-items-more text-muted\">" + O.escapeHtml(formatItemsMore(row.itemCount - 1)) + "</div>"
      : "";

    return (
      "<article class=\"orders-card orders-card--kitchen oh-orders-card oh-orders-card--kitchen" + muted + "\" role=\"listitem\" data-order-id=\"" + O.escapeHtml(row.id) + "\">" +
      buildImageHtml(row, "kitchen") +
      "<div class=\"orders-card-body\">" +
      buildTopRowHtml(row) +
      "<div class=\"orders-card-code\">" + row.codeHtml + "</div>" +
      "<div class=\"orders-card-customer\"><span class=\"orders-card-meta__label\">" + O.escapeHtml(customerLabel) + ":</span> " + O.escapeHtml(row.customer) + "</div>" +
      moreItemsHtml +
      "<div class=\"orders-card-meta orders-card-meta--kitchen\">" +
      "<div class=\"orders-card-meta__row\"><span class=\"orders-card-meta__label\">" + O.escapeHtml(totalLabel) + ":</span> " + O.escapeHtml(row.total) + "</div>" +
      "<div class=\"orders-card-meta__row\"><span class=\"orders-card-meta__label\">" + O.escapeHtml(receivedLabel) + ":</span> " + O.escapeHtml(receivedDisplay) + "</div>" +
      "</div>" +
      buildActionsHtml(actionsHtml) +
      "</div>" +
      "</article>"
    );
  }

  function buildCardHtml(row, mode) {
    if (mode === "kitchen") return buildKitchenCard(row);
    return buildCompactCard(row);
  }

  function renderEmptyState(host) {
    if (!host) return;
    host.innerHTML =
      "<div class=\"oh-orders-empty text-center py-5\">" +
      "<div class=\"oh-orders-empty__title fw-semibold mb-1\">" + O.escapeHtml(localize("noLiveOrders")) + "</div>" +
      "<div class=\"oh-orders-empty__desc text-muted\">" + O.escapeHtml(localize("noLiveOrdersDescription")) + "</div>" +
      "</div>";
  }

  function renderCards(host, rows, mode) {
    if (!host) return;

    if (!rows.length) {
      renderEmptyState(host);
      return;
    }

    const sorted = sortRows(rows);
    host.innerHTML = sorted.map(function (r) {
      return buildCardHtml(r, mode);
    }).join("");
  }

  function updateSummary(totalCount, rows) {
    let active = 0;
    let cancelled = 0;
    rows.forEach(function (r) {
      if (ACTIVE_STATUSES.has(r.status)) active++;
      if (r.status === "Cancelled") cancelled++;
    });

    [
      ["ordersLiveDisplayTodayCount", totalCount],
      ["ordersLiveDisplayActiveCount", active],
      ["ordersLiveDisplayCancelledCount", cancelled]
    ].forEach(function (pair) {
      const el = document.getElementById(pair[0]);
      if (el) el.textContent = String(pair[1]);
    });
  }

  function getCardsHost() {
    if (isLiveDisplayPage()) {
      return document.getElementById("ordersLiveDisplayCardsHost");
    }
    return document.getElementById("ordersCardsHost");
  }

  function getTableSlot() {
    if (isLiveDisplayPage()) {
      return document.getElementById("ordersLiveDisplayTableSlot");
    }
    return document.getElementById("ordersNormalTableSlot");
  }

  function applyLayoutClasses() {
    document.body.classList.toggle("oh-orders-view-table", currentMode === "table");
    document.body.classList.toggle("oh-orders-view-compact", currentMode === "compact");
    document.body.classList.toggle("oh-orders-view-kitchen", currentMode === "kitchen");
  }

  function updateSelectorUi() {
    document.querySelectorAll("[data-orders-view-mode]").forEach(function (btn) {
      const mode = btn.getAttribute("data-orders-view-mode");
      const selected = mode === currentMode;
      btn.classList.toggle("active", selected);
      btn.setAttribute("aria-pressed", selected ? "true" : "false");
    });
  }

  function updateFilterToggleUi(visible) {
    const toggle = document.getElementById("ordersFilterToggle");
    if (!toggle) return;

    toggle.textContent = visible
      ? localize("hideFilters")
      : localize("showFilters");
    toggle.setAttribute("aria-expanded", visible ? "true" : "false");
  }

  function applyFilterVisibility() {
    if (isLiveDisplayPage()) return;

    const panel = document.getElementById("ordersFiltersPanel");
    const toggle = document.getElementById("ordersFilterToggle");
    const isCardView = currentMode === "compact" || currentMode === "kitchen";
    const visible = !isCardView || filtersManuallyOpen;

    if (panel) {
      panel.classList.toggle("d-none", !visible);
      panel.hidden = !visible;
    }

    if (toggle) {
      toggle.classList.toggle("d-none", !isCardView);
    }

    updateFilterToggleUi(visible);
  }

  function toggleFiltersPanel() {
    filtersManuallyOpen = !filtersManuallyOpen;
    persistFiltersOpenState(filtersManuallyOpen);
    applyFilterVisibility();
  }

  function applyLayoutVisibility() {
    const tableSlot = getTableSlot();
    const cardsHost = getCardsHost();
    const tableCard = document.querySelector(".oh-orders-table-card");

    if (tableCard && tableSlot && tableCard.parentElement !== tableSlot) {
      tableSlot.appendChild(tableCard);
    }

    if (tableSlot) {
      const showTable = currentMode === "table";
      tableSlot.classList.toggle("d-none", !showTable);
      tableSlot.hidden = !showTable;
    }

    if (cardsHost) {
      const showCards = currentMode !== "table";
      cardsHost.classList.toggle("d-none", !showCards);
      cardsHost.hidden = !showCards;
    }

    applyFilterVisibility();
  }

  function setMode(mode, options) {
    const opts = options || {};
    currentMode = normalizeMode(mode);
    if (opts.persist !== false) persistMode(currentMode);
    applyLayoutClasses();
    updateSelectorUi();
    applyLayoutVisibility();
    if (opts.render !== false) syncFromTable();
  }

  function syncFromTable() {
    const container = document.getElementById("ordersTableHost");
    const rows = parseRowsFromContainer(container);
    const meta = container && container.querySelector(".orders-table-meta");
    let total = rows.length;
    if (meta) {
      const v = parseInt(meta.getAttribute("data-total-count") || "", 10);
      if (!isNaN(v)) total = v;
    }

    if (isLiveDisplayPage()) {
      updateSummary(total, rows);
    }

    if (currentMode === "compact" || currentMode === "kitchen") {
      renderCards(getCardsHost(), rows, currentMode);
    }
  }

  function bindSelectors() {
    document.querySelectorAll("[data-orders-view-mode]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        const mode = btn.getAttribute("data-orders-view-mode");
        if (!mode || mode === currentMode) return;
        setMode(mode);
      });
    });
  }

  function bindFilterToggle() {
    const toggle = document.getElementById("ordersFilterToggle");
    if (!toggle) return;
    toggle.addEventListener("click", function () {
      toggleFiltersPanel();
    });
  }

  function init() {
    filtersManuallyOpen = readFiltersOpenState();
    setMode(readStoredMode(), { persist: false });
    bindSelectors();
    bindFilterToggle();
  }

  O.viewMode = {
    getMode: getMode,
    setMode: setMode,
    syncFromTable: syncFromTable,
    applyLayoutVisibility: applyLayoutVisibility,
    parseRowsFromContainer: parseRowsFromContainer,
    isLiveDisplayPage: isLiveDisplayPage
  };

  document.addEventListener("DOMContentLoaded", init);
})(window);
