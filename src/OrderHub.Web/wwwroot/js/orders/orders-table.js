// Orders table: fetch /orders/table partial, diff data-order-id, new-order UI only in default live view.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.audio || !O.notificationSettings) {
    return;
  }

  const T = O.table;

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
    const p = new URLSearchParams(global.location.search);
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

  function detectNewOrderIds(incomingIds) {
    const newIds = [];
    for (let i = 0; i < incomingIds.length; i++) {
      const id = incomingIds[i];
      if (!T.knownOrderIds.has(id)) newIds.push(id);
    }
    return newIds;
  }

  function captureKnownOrderIdsFromContainer() {
    const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!container) return;
    T.knownOrderIds = new Set();
    container.querySelectorAll("[data-order-id]").forEach(function (r) {
      const id = r.getAttribute("data-order-id");
      if (id) T.knownOrderIds.add(id);
    });
  }

  function markOrdersAsRecentlyNew(orderIds) {
    const expiresAt = Date.now() + T.NEW_ORDER_HIGHLIGHT_MS;
    orderIds.forEach(function (id) {
      T.recentlyNewOrderIds.set(id, expiresAt);
    });
  }

  function applyNewOrderVisualState() {
    const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!container) return;

    const now = Date.now();
    for (const entry of Array.from(T.recentlyNewOrderIds.entries())) {
      const id = entry[0];
      const exp = entry[1];
      if (exp <= now) {
        T.recentlyNewOrderIds.delete(id);
        continue;
      }
      const row = container.querySelector("[data-order-id=\"" + id + "\"]");
      if (!row) continue;
      row.classList.add("order-row-new");
      row.classList.add("order-row-new-flash");
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

  async function refreshOrdersTable() {
    const live = isDefaultLiveOrdersView();
    var audioPlayedOk = "-";

    try {
      const u = new URL(global.location.origin + O.opts.tableUrl);
      u.search = global.location.search || "";
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
      const hasDataOrderIds = tmp.querySelectorAll("[data-order-id]").length > 0;
      if (hasRows && !hasDataOrderIds) {
        if (O.isDebugEnabled()) {
          O.debugWarn("rows without data-order-id");
        }
        O.showOrdersWarning("orders-table-missing-data", O.getMessage("tableMissingDataOrderId"));
      }

      const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
      if (!container) {
        O.showOrdersWarning("orders-table-target-missing", O.getMessage("tableHostMissing"));
        return;
      }

      container.innerHTML = html;

      if (live && newIds.length > 0) {
        markOrdersAsRecentlyNew(newIds);
      }

      captureKnownOrderIdsFromContainer();
      applyNewOrderVisualState();

      if (live && newIds.length > 0 && O.state.notificationSettings) {
        const st = O.state.notificationSettings;
        if (st.newOrderSoundEnabled) {
          if (!O.audio.isSoundUnlocked()) {
            if (!T.hintShownForUnlock) {
              T.hintShownForUnlock = true;
              O.showMessage(O.getMessage("soundUnlockHint"), "info");
            }
            audioPlayedOk = "locked";
          } else {
            showSpeakerIndicators(newIds);
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
            } finally {
              hideSpeakerIndicators(newIds);
            }
          }
        } else {
          audioPlayedOk = "soundDisabled";
        }
        O.audio.showBrowserNotificationIfAllowed();
      }

      if (O.isDebugEnabled() && live && newIds.length > 0) {
        O.debugLog("poll", { newInTable: newIds.length, defaultLive: true, audio: audioPlayedOk });
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
