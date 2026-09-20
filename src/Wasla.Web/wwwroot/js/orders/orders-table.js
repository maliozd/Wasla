// Orders table: fetch /orders/table partial, diff data-order-id, new-order UI only in default live view.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  if (!O.table) {
    O.table = {
      knownOrderIds: new Set(),
      recentlyNewOrderIds: new Map(),
      hintShownForUnlock: false,
      NEW_ORDER_HIGHLIGHT_MS: 30000
    };
  }

  const T = O.table;

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }
  /** YYYY-MM-DD in local time (same as date input / server default-today). */
  function localDateYmd() {
    const d = new Date();
    return (
      d.getFullYear() +
      "-" +
      String(d.getMonth() + 1).padStart(2, "0") +
      "-" +
      String(d.getDate()).padStart(2, "0")
    );
  }

  /** Default live: page 1, receivedAt desc, and date range is "today" (incl. empty URL: server uses today). */
  function isDefaultLiveOrdersView() {
    if (isLiveDisplayPage()) return true;    const p = new URLSearchParams(global.location.search);
    const sortBy = (p.get("sortBy") || p.get("sort") || "receivedAt").toLowerCase();
    const sortDirection = (p.get("sortDirection") || p.get("dir") || "desc").toLowerCase();
    const page = (p.get("page") || "1").trim();
    if (page !== "1" || sortBy !== "receivedat" || sortDirection !== "desc") return false;

    const s = p.get("startDate");
    const e = p.get("endDate");
    const ymd = localDateYmd();
    if (s && e) {
      if (s !== ymd || e !== ymd) return false;
    } else if (s || e) {
      return false;
    }
    return true;
  }

  T.isDefaultLiveOrdersView = isDefaultLiveOrdersView;

  function extractOrderIdsFromHtml(html) {
    const tmp = document.createElement("div");
    tmp.innerHTML = html;
    const ids = [];
    tmp.querySelectorAll("[data-order-id]").forEach(function (r) {
      const id = r.getAttribute("data-order-id");
      if (id) ids.push(id);
    });
    return { ids: ids, tmp: tmp };
  }

  function updateLastUpdatedTimestamps() {
    try {
      const text = new Date().toLocaleTimeString();
      ["ordersLastUpdated", "ordersLiveDisplayLastUpdated"].forEach(function (id) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
      });
    } catch (e) {      if (O.isDebugEnabled()) {
        O.debugWarn("updateLastUpdatedTimestamps failed", e);
      }
    }
  }

  function updateTotalCountFromTmp(tmp) {
    try {
      const el = document.getElementById("ordersTotalCount");
      if (!el || !tmp) return;
      const meta = tmp.querySelector(".orders-table-meta");
      if (!meta) return;
      const v = meta.getAttribute("data-total-count");
      if (v == null) return;
      const n = parseInt(String(v), 10);
      if (isNaN(n)) return;
      el.textContent = String(n);
    } catch (e) {
      if (O.isDebugEnabled()) {
        O.debugWarn("updateTotalCountFromTmp failed", e);
      }
    }
  }

  function detectNewOrderIds(incomingIds) {
    const newIds = [];
    for (let i = 0; i < incomingIds.length; i++) {
      const id = incomingIds[i];
      if (!T.knownOrderIds.has(id)) newIds.push(id);
    }
    return newIds;
  }

  function getRefreshContainer() {
    if (isLiveDisplayPage()) {
      return document.getElementById("ordersLiveScreenHost");
    }
    return document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
  }

  function captureKnownOrderIdsFromContainer() {
    const container = getRefreshContainer();
    if (!container) return;
    T.knownOrderIds = new Set();
    container.querySelectorAll("[data-order-id]").forEach(function (r) {
      const id = r.getAttribute("data-order-id");
      if (id) T.knownOrderIds.add(id);
    });
  }

  function getNewOrderHighlightDurationMs() {
    var st = O.state.notificationSettings;
    if (O.notificationSettings && typeof O.notificationSettings.mergeDefaultNotificationState === "function") {
      st = O.notificationSettings.mergeDefaultNotificationState(st || {});
    }
    if (!st) return T.NEW_ORDER_HIGHLIGHT_MS;
    var sec = parseInt(st.newOrderHighlightDurationSeconds, 10);
    if (isNaN(sec) || sec < 1) return T.NEW_ORDER_HIGHLIGHT_MS;
    return sec * 1000;
  }

  function behaviorToClass(b) {
    if (!b) return "fade";
    var s = String(b);
    if (s.toLowerCase() === "borderglow") return "border-glow";
    return s.toLowerCase();
  }

  function getHighlightColorName() {
    var st = O.state.notificationSettings;
    if (O.notificationSettings && typeof O.notificationSettings.mergeDefaultNotificationState === "function") {
      st = O.notificationSettings.mergeDefaultNotificationState(st || {});
    }
    var c = (st && st.newOrderHighlightColor) ? String(st.newOrderHighlightColor).trim() : "yellow";
    if (!c) c = "yellow";
    if (/^#[0-9a-fA-F]{6}$/.test(c)) return c.toLowerCase();
    c = c.toLowerCase();
    if (["orange", "blue", "green", "yellow", "red"].indexOf(c) < 0) c = "yellow";
    return c;
  }

  function getHighlightBehaviorName() {
    var st = O.state.notificationSettings;
    if (O.notificationSettings && typeof O.notificationSettings.mergeDefaultNotificationState === "function") {
      st = O.notificationSettings.mergeDefaultNotificationState(st || {});
    }
    var b = (st && st.newOrderHighlightBehavior) ? String(st.newOrderHighlightBehavior) : "fade";
    return behaviorToClass(b);
  }

  function applyHighlightClassesToRow(row) {
    var color = getHighlightColorName();
    var beh = getHighlightBehaviorName();
    row.classList.remove(
      "order-row-new",
      "order-row-new-flash",
      "color-orange", "color-blue", "color-green", "color-yellow", "color-red", "color-custom",
      "behavior-fade", "behavior-pulse", "behavior-blink", "behavior-border-glow", "behavior-none"
    );
    row.style.removeProperty("--new-order-highlight-bg");
    row.style.removeProperty("--new-order-highlight-border");

    if (color && String(color).indexOf("#") === 0) {
      // Custom hex color -> CSS variables
      var h = String(color).replace("#", "");
      var r = parseInt(h.substring(0, 2), 16);
      var g = parseInt(h.substring(2, 4), 16);
      var b = parseInt(h.substring(4, 6), 16);
      row.style.setProperty("--new-order-highlight-bg", "rgba(" + r + "," + g + "," + b + ",0.18)");
      row.style.setProperty("--new-order-highlight-border", String(color));
      row.classList.add("order-row-new", "color-custom", "behavior-" + beh);
      return;
    }

    row.classList.add("order-row-new", "color-" + color, "behavior-" + beh);
  }

  function markOrdersAsRecentlyNew(orderIds) {
    const expiresAt = Date.now() + getNewOrderHighlightDurationMs();
    orderIds.forEach(function (id) {
      T.recentlyNewOrderIds.set(id, expiresAt);
    });
  }

  function applyNewOrderVisualState() {
    const container = getRefreshContainer();
    if (!container) return;

    function setNewBadgeVisible(row, isVisible) {
      const badge = row.querySelector("[data-new-badge]");
      if (!badge) return;
      if (isVisible) badge.classList.remove("d-none");
      else badge.classList.add("d-none");
    }

    const now = Date.now();
    for (const entry of Array.from(T.recentlyNewOrderIds.entries())) {
      const id = entry[0];
      const exp = entry[1];
      if (exp <= now) {
        const row = container.querySelector("[data-order-id=\"" + id + "\"]");
        if (row) {
          row.classList.remove("order-row-new", "order-row-new-flash");
          setNewBadgeVisible(row, false);
        }
        T.recentlyNewOrderIds.delete(id);
        continue;
      }
      const row = container.querySelector("[data-order-id=\"" + id + "\"]");
      if (!row) continue;
      applyHighlightClassesToRow(row);
      setNewBadgeVisible(row, true);
    }
  }

  function showSpeakerIndicators(orderIds) {
    const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!container) return;
    orderIds.forEach(function (id) {
      const el = container.querySelector("[data-order-speaker=\"" + id + "\"]");
      if (!el) return;
      el.classList.remove("d-none");
      el.classList.add("is-playing");
    });
  }

  function hideSpeakerIndicators(orderIds) {
    const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!container) return;
    orderIds.forEach(function (id) {
      const el = container.querySelector("[data-order-speaker=\"" + id + "\"]");
      if (!el) return;
      el.classList.remove("is-playing");
      el.classList.add("d-none");
    });
  }

  function updateLiveScreenSummaryFromTmp(tmp) {
    if (!isLiveDisplayPage() || !tmp) return;
    try {
      const meta = tmp.querySelector(".orders-live-screen-meta");
      if (!meta) return;
      [
        ["ordersLiveDisplayTodayCount", "data-total-count"],
        ["ordersLiveDisplayActiveCount", "data-active-count"],
        ["ordersLiveDisplayCancelledCount", "data-cancelled-count"]
      ].forEach(function (pair) {
        const el = document.getElementById(pair[0]);
        if (!el) return;
        const v = meta.getAttribute(pair[1]);
        if (v == null) return;
        const n = parseInt(String(v), 10);
        if (isNaN(n)) return;
        el.textContent = String(n);
      });
    } catch (e) {
      if (O.isDebugEnabled()) {
        O.debugWarn("updateLiveScreenSummaryFromTmp failed", e);
      }
    }
  }

  function buildPollUrl() {
    if (isLiveDisplayPage()) {
      const path = O.opts.liveScreenUrl || "/orders/live-screen";
      const u = new URL(global.location.origin + path);
      return u;
    }

    const u = new URL(global.location.origin + O.opts.tableUrl);
    u.search = global.location.search || "";
    return u;
  }

  async function refreshOrdersTable() {
    const live = isDefaultLiveOrdersView();
    var audioPlayedOk = "-";

    try {
      const u = buildPollUrl();
      u.searchParams.set("_", String(Date.now()));
      const resp = await fetch(u.toString(), {
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });

      if (O.isDebugEnabled()) {
        O.debugLog("orders poll", { url: u.toString(), status: resp.status, defaultLive: live });
      }

      if (!resp.ok) {
        O.showOrdersWarning("orders-table-failed", O.getMessage("tableRefreshFailed") + " (HTTP " + resp.status + ")");
        if (O.isDebugEnabled()) {
          O.debugWarn("table refresh failed", resp.status);
        }
        return;
      }

      const html = await resp.text();

      if (html.indexOf("<html") >= 0 || html.indexOf("<!DOCTYPE") >= 0) {
        O.showOrdersWarning("orders-table-html", O.getMessage("tableReturnedFullPage"));
        if (O.isDebugEnabled()) {
          O.debugWarn("table returned full document");
        }
        return;
      }

      const parsed = extractOrderIdsFromHtml(html);
      const ids = parsed.ids;
      const tmp = parsed.tmp;
      const newIds = detectNewOrderIds(ids);

      if (O.isDebugEnabled()) {
        O.debugLog("table diff", {
          incomingIdCount: ids.length,
          knownIdCount: T.knownOrderIds.size,
          newInTable: newIds.length,
          newIds: newIds.slice(0, 20),
          defaultLive: live
        });
      }

      const hasRows = tmp.querySelectorAll("tr").length > 0;
      const hasCards = tmp.querySelectorAll("[data-order-id]").length > 0;
      const hasDataOrderIds = hasCards;
      if (hasRows && !hasDataOrderIds && !isLiveDisplayPage()) {
        if (O.isDebugEnabled()) {
          O.debugWarn("rows without data-order-id");
        }
        O.showOrdersWarning("orders-table-missing-data", O.getMessage("tableMissingDataOrderId"));
      }

      const container = getRefreshContainer();
      if (!container) {
        O.showOrdersWarning("orders-table-target-missing", O.getMessage("tableHostMissing"));
        return;
      }

      container.innerHTML = html;
      if (isLiveDisplayPage()) {
        updateLiveScreenSummaryFromTmp(tmp);
      } else {
        updateTotalCountFromTmp(tmp);
      }
      updateLastUpdatedTimestamps();

      if (!isLiveDisplayPage() && O.viewMode && typeof O.viewMode.syncFromTable === "function") {
        O.viewMode.syncFromTable();
      }

      if (live && newIds.length > 0) {
        markOrdersAsRecentlyNew(newIds);
      }

      captureKnownOrderIdsFromContainer();
      applyNewOrderVisualState();

      // Phase 2B1: Live Screen owns automatic new-order sound (one sequence per poll with new IDs).
      // Orders management polling must not auto-trigger sound.
      if (isLiveDisplayPage() && newIds.length > 0 && O.state.notificationSettings && O.audio) {
        const st = O.state.notificationSettings;
        if (st.newOrderSoundEnabled) {
          if (!O.audio.isSoundUnlocked()) {
            if (!T.hintShownForUnlock) {
              T.hintShownForUnlock = true;
              O.showMessage(O.getMessage("soundUnlockHint"), "info");
            }
            audioPlayedOk = "locked";
          } else {
            try {
              await O.audio.playSoundNow({
                newOrderSoundEnabled: true,
                newOrderSoundName: st.newOrderSoundName,
                newOrderSoundRepeatCount: st.newOrderSoundRepeatCount,
                newOrderSoundVolumePercent: st.newOrderSoundVolumePercent != null
                  ? st.newOrderSoundVolumePercent
                  : Math.round((st.newOrderSoundVolume || 1) * 100),
                showBrowserNotification: st.showBrowserNotification
              });
              audioPlayedOk = "ok";
            } catch (e) {
              audioPlayedOk = "error";
              if (O.isDebugEnabled()) {
                O.debugWarn("playSoundNow", e);
              }
            }
          }
        } else {
          audioPlayedOk = "soundDisabled";
        }
      }

      if (O.isDebugEnabled() && live && newIds.length > 0) {
        O.debugLog("poll", {
          newInTable: newIds.length,
          defaultLive: true,
          liveDisplay: isLiveDisplayPage(),
          audio: audioPlayedOk
        });
      }
    } catch (error) {
      O.showOrdersWarning("orders-table-exception", O.getMessage("tableRefreshException"));
      if (O.isDebugEnabled()) {
        O.debugWarn("refreshOrdersTable", error);
      }
    }
  }

  function initPolling() {
    setInterval(function () {
      refreshOrdersTable();
    }, O.opts.pollingIntervalMs);
  }

  T.captureKnownOrderIdsFromContainer = captureKnownOrderIdsFromContainer;
  T.extractOrderIdsFromHtml = extractOrderIdsFromHtml;
  T.detectNewOrderIds = detectNewOrderIds;
  T.markOrdersAsRecentlyNew = markOrdersAsRecentlyNew;
  T.applyNewOrderVisualState = applyNewOrderVisualState;
  T.showSpeakerIndicators = showSpeakerIndicators;
  T.hideSpeakerIndicators = hideSpeakerIndicators;
  T.refreshOrdersTable = refreshOrdersTable;
  T.initPolling = initPolling;
})(window);
