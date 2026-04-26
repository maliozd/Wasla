// Orders table: polling, table partial refresh, new-order detection (API + table diff), highlights, speaker.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.audio || !O.notificationSettings) {
    return;
  }

  const T = O.table;

  function isDefaultLiveOrdersView() {
    const params = new URLSearchParams(global.location.search);
    const sortBy = (params.get("sortBy") || "receivedAt").toLowerCase();
    const sortDirection = (params.get("sortDirection") || "desc").toLowerCase();
    const page = (params.get("page") || "1").trim();
    return sortBy === "receivedat" && sortDirection === "desc" && page === "1";
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
    for (const id of incomingIds) {
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

  function isOrderIdInTableDom(orderId) {
    const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
    if (!container) return false;
    return !!container.querySelector("[data-order-id=\"" + orderId + "\"]");
  }

  function pruneSoundDedupe() {
    const now = Date.now();
    for (const entry of T.recentlySoundPlayed.entries()) {
      if (entry[1] <= now) T.recentlySoundPlayed.delete(entry[0]);
    }
  }

  async function processPendingFromCheck(visibleTableNewIds) {
    const pending = T.pendingFromCheck;
    T.pendingFromCheck = null;
    if (!pending || !pending.length) return;
    const st = O.state.notificationSettings;

    pruneSoundDedupe();
    const needPlay = [];
    for (const id of pending) {
      if (!T.recentlySoundPlayed.has(id)) {
        needPlay.push(id);
      }
    }
    if (needPlay.length > 0) {
      const now = Date.now();
      const until = now + T.SOUND_DEDUPE_MS;
      for (const id of needPlay) {
        T.recentlySoundPlayed.set(id, until);
      }
    }

    if (needPlay.length > 0 && st && st.newOrderSoundEnabled) {
      if (!O.audio.isSoundUnlocked()) {
        if (!T.hintShownForUnlock) {
          T.hintShownForUnlock = true;
          O.showMessage(O.getMessage("soundUnlockHint"), "info");
        }
      } else {
        const forSpeaker = visibleTableNewIds.filter(function (id) {
          return needPlay.indexOf(id) >= 0;
        });
        if (forSpeaker.length) {
          showSpeakerIndicators(forSpeaker);
        }
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
        } finally {
          if (forSpeaker && forSpeaker.length) {
            hideSpeakerIndicators(forSpeaker);
          }
        }
      }
    }

    O.audio.showBrowserNotificationIfAllowed();

    const anyNotVisible = pending.some(function (id) {
      return !isOrderIdInTableDom(id);
    });
    if (anyNotVisible || !isDefaultLiveOrdersView()) {
      O.showMessage(O.getMessage("newOrdersAvailable") + " " + O.getMessage("refreshToSeeNewOrders"), "info");
    }
  }

  async function checkNewOrders() {
    const base = (O.opts.newOrdersCheckUrl || "/orders/new-orders/check").replace(/\/$/, "");
    const u = new URL(base, global.location.origin);
    u.searchParams.set("sinceReceivedAtUtc", T.lastKnownLatestReceivedAtUtc || "2000-01-01T00:00:00.000Z");
    u.searchParams.set("_", String(Date.now()));

    O.debugLog("Check new orders", { url: u.toString() });

    const resp = await fetch(u.toString(), { headers: { "X-Requested-With": "fetch" } });
    if (!resp.ok) {
      O.debugWarn("new-orders check HTTP failed", resp.status);
      return;
    }
    const j = await resp.json();
    if (j.latestReceivedAtUtc) {
      T.lastKnownLatestReceivedAtUtc = j.latestReceivedAtUtc;
    }
    if (j.hasNewOrders && j.newOrderIds && j.newOrderIds.length) {
      T.pendingFromCheck = j.newOrderIds.map(String);
    }
  }

  async function refreshOrdersTable() {
    const visibleNewForPending = [];
    try {
      const u = new URL(global.location.origin + O.opts.tableUrl);
      u.search = global.location.search || "";
      u.searchParams.set("_", Date.now().toString());

      O.debugLog("Polling orders table", { url: u.toString() });

      const resp = await fetch(u.toString(), {
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });

      O.debugLog("Orders table response", {
        status: resp.status,
        redirected: resp.redirected,
        responseUrl: resp.url,
        contentType: resp.headers.get("content-type")
      });

      if (!resp.ok) {
        const base = O.getMessage("tableRefreshFailed");
        O.showOrdersWarning("orders-table-failed", base + " (HTTP " + resp.status + ")");
        O.debugWarn("Orders table request failed", resp);
        await processPendingFromCheck([]);
        return;
      }

      const html = await resp.text();
      O.debugLog("Orders table HTML length", { length: html.length });

      if (html.indexOf("<html") >= 0 || html.indexOf("<!DOCTYPE") >= 0) {
        O.showOrdersWarning("orders-table-html", O.getMessage("tableReturnedFullPage"));
        O.debugWarn("Orders table returned full HTML instead of partial", html.substring(0, 500));
        await processPendingFromCheck([]);
        return;
      }

      const parsed = extractOrderIdsFromHtml(html);
      const ids = parsed.ids;
      const tmp = parsed.tmp;
      O.debugLog("Incoming order ids", { count: ids.length, ids: ids.slice(0, 30) });
      O.debugLog("Known order ids before compare", { count: T.knownOrderIds.size });

      const hasRows = tmp.querySelectorAll("tr").length > 0;
      const hasDataOrderIds = tmp.querySelectorAll("[data-order-id]").length > 0;
      if (hasRows && !hasDataOrderIds) {
        O.debugWarn("Orders table rows exist but no data-order-id attributes were found.");
        O.showOrdersWarning("orders-table-missing-data", O.getMessage("tableMissingDataOrderId"));
      }

      const newIds = detectNewOrderIds(ids);
      O.debugLog("Detected new order ids in table", { count: newIds.length, newIds: newIds });

      const container = document.getElementById("ordersTableHost") || document.getElementById("ordersTableContainer");
      if (!container) {
        O.showOrdersWarning("orders-table-target-missing", O.getMessage("tableHostMissing"));
        O.debugWarn("Missing orders table host element (#ordersTableHost)");
        await processPendingFromCheck([]);
        return;
      }

      container.innerHTML = html;
      O.debugLog("Orders table replaced", { replaced: true });

      captureKnownOrderIdsFromContainer();
      applyNewOrderVisualState();

      if (newIds.length > 0) {
        markOrdersAsRecentlyNew(newIds);
        applyNewOrderVisualState();
        newIds.forEach(function (id) { visibleNewForPending.push(id); });
      }
    } catch (error) {
      O.showOrdersWarning("orders-table-exception", O.getMessage("tableRefreshException"));
      O.debugWarn("refreshOrdersTable failed", error);
    }
    await processPendingFromCheck(visibleNewForPending);
  }

  async function runPollCycle() {
    try {
      if (!T.lastKnownLatestReceivedAtUtc) {
        T.lastKnownLatestReceivedAtUtc = O.opts.latestReceivedAtUtc || "2000-01-01T00:00:00.000Z";
      }
      await checkNewOrders();
    } catch (err) {
      O.debugWarn("checkNewOrders failed", err);
    }
    await refreshOrdersTable();
  }

  function initPolling() {
    T.lastKnownLatestReceivedAtUtc = O.opts.latestReceivedAtUtc || "2000-01-01T00:00:00.000Z";
    setInterval(function () {
      runPollCycle();
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
  T.checkNewOrders = checkNewOrders;
  T.runPollCycle = runPollCycle;
  T.initPolling = initPolling;
})(window);
