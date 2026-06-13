// Orders display view modes: table, compact cards, kitchen cards.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  const STORAGE_KEY = "orderhub.orders.viewMode";
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

  function localize(key) {
    return O.getMessage(key);
  }

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }

  function normalizeMode(mode) {
    return MODES.indexOf(mode) >= 0 ? mode : "table";
  }

  function getMode() {
    return currentMode;
  }

  function readStoredMode() {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      if (v && MODES.indexOf(v) >= 0) return v;
    } catch (e) { /* ignore */ }
    return "table";
  }

  function persistMode(mode) {
    try {
      localStorage.setItem(STORAGE_KEY, mode);
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

  function buildImageHtml(row, mode) {
    const src = row.displayImage || O.opts.defaultFoodImage || "/images/demo-food/chicken-rice-01.svg";
    const alt = row.firstProductName || row.customer || "";
    return (
      "<div class=\"oh-orders-card__image-wrap oh-orders-card__image-wrap--" + mode + "\">" +
      "<img class=\"oh-orders-card__image\" src=\"" + O.escapeHtml(src) + "\" alt=\"" + O.escapeHtml(alt) + "\" loading=\"lazy\" />" +
      "</div>"
    );
  }

  function renderEmptyState(host) {
    if (!host) return;
    host.innerHTML =
      "<div class=\"oh-orders-empty text-center py-5\">" +
      "<div class=\"oh-orders-empty__title fw-semibold mb-1\">" + O.escapeHtml(localize("noLiveOrders")) + "</div>" +
      "<div class=\"oh-orders-empty__desc text-muted\">" + O.escapeHtml(localize("noLiveOrdersDescription")) + "</div>" +
      "</div>";
  }

  function buildCardHtml(row, mode) {
    const muted =
      row.status === "Delivered" || row.status === "Cancelled" || row.status === "Failed"
        ? " oh-orders-card--muted"
        : "";
    const badge = statusBadgeClass(row.status);
    const actionsHtml = scaleActionsHtml(row.actionsHtml, mode);
    const moreItemsHtml = row.itemCount > 1
      ? "<div class=\"oh-orders-card__more-items text-muted small\">" + O.escapeHtml(formatItemsMore(row.itemCount - 1)) + "</div>"
      : "";

    return (
      "<article class=\"oh-orders-card oh-orders-card--" + mode + muted + "\" role=\"listitem\" data-order-id=\"" + O.escapeHtml(row.id) + "\">" +
      buildImageHtml(row, mode) +
      "<div class=\"oh-orders-card__content\">" +
      "<div class=\"oh-orders-card__head\">" +
      "<div class=\"oh-orders-card__platform\">" + row.platformHtml + "</div>" +
      "<span class=\"" + badge + "\">" + O.escapeHtml(row.statusText) + "</span>" +
      "</div>" +
      "<div class=\"oh-orders-card__code\">" + row.codeHtml + "</div>" +
      moreItemsHtml +
      "<div class=\"oh-orders-card__customer\"><span class=\"oh-orders-card__label\">" + O.escapeHtml(localize("ordersFullscreenCustomer")) + "</span> " + O.escapeHtml(row.customer) + "</div>" +
      "<div class=\"oh-orders-card__meta\">" +
      "<span><span class=\"oh-orders-card__label\">" + O.escapeHtml(localize("ordersFullscreenTotal")) + "</span> " + O.escapeHtml(row.total) + "</span>" +
      "<span><span class=\"oh-orders-card__label\">" + O.escapeHtml(localize("ordersFullscreenReceived")) + "</span> " + O.escapeHtml(row.received) + "</span>" +
      "</div>" +
      (actionsHtml ? "<div class=\"oh-orders-card__actions\">" + actionsHtml + "</div>" : "") +
      "</div>" +
      "</article>"
    );
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

  function init() {
    setMode(readStoredMode(), { persist: false });
    bindSelectors();
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
