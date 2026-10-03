// Live Screen authoritative snapshot: one order store and one refresh coordinator.
// Orders management HTML refresh stays in orders-table.js.
(function (root, factory) {
  "use strict";

  const api = factory();
  api.attachBrowser = attachBrowser;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }

  if (!root || !root.WaslaOrders) return;

  const O = root.WaslaOrders;
  O.applyDetailCurrency = api.applyDetailCurrency;
  const browser = attachBrowser(O, root, api);
  O.liveStore = browser;
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  const ACTIVE_STATUSES = {
    New: true,
    Accepted: true,
    Preparing: true,
    ReadyForPickup: true,
    OnTheWay: true
  };

  const DEMO_IMAGES = [
    "/images/demo-food/chicken-rice-01.svg",
    "/images/demo-food/chicken-rice-02.svg",
    "/images/demo-food/chicken-rice-03.svg",
    "/images/demo-food/chicken-rice-04.svg",
    "/images/demo-food/chicken-rice-05.svg"
  ];

  function stableHash(value) {
    let hash = 17;
    for (let i = 0; i < value.length; i++) {
      hash = (Math.imul(hash, 31) + value.charCodeAt(i)) | 0;
    }
    return hash;
  }

  function demoImageUrl(order) {
    const first = order.items && order.items.length ? order.items[0].productName : "";
    const seed = (first && String(first).trim())
      || (order.displayNumber && String(order.displayNumber).trim())
      || String(order.id || "order").replace(/-/g, "");
    const hash = stableHash(seed);
    const index = (hash < 0 ? -hash : hash) % DEMO_IMAGES.length;
    return DEMO_IMAGES[index];
  }

  function orderContentSignature(order) {
    const items = order.items || [];
    const itemPart = items.map(function (item) {
      return [item.productName || "", item.quantity, item.notes || ""].join("\u001f");
    }).join("\u001e");
    return [
      order.status || "",
      order.displayNumber || "",
      order.platform || "",
      order.customerName || "",
      order.customerAddress || "",
      order.customerNote || "",
      order.totalAmount,
      order.receivedAtUtc || "",
      order.deliveredAtUtc || "",
      itemPart
    ].join("\u001d");
  }

  const liveMoneyFormats = new Map();

  function liveMoneyFormat(culture) {
    const locale = culture || "tr-TR";
    let format = liveMoneyFormats.get(locale);
    if (!format) {
      format = new Intl.NumberFormat(locale, {
        style: "currency",
        currency: "TRY",
        currencyDisplay: "narrowSymbol",
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
      });
      liveMoneyFormats.set(locale, format);
    }
    return format;
  }

  function formatAmount(amount, culture) {
    const value = typeof amount === "number" ? amount : Number(amount);
    if (!isFinite(value)) return "";
    return liveMoneyFormat(culture).format(value);
  }

  function applyDetailCurrency(root, culture) {
    if (!root || typeof root.querySelectorAll !== "function") return;
    const nodes = root.querySelectorAll("[data-money]");
    for (let i = 0; i < nodes.length; i++) {
      const node = nodes[i];
      const raw = node.getAttribute("data-money");
      if (raw == null || String(raw).trim() === "") continue;
      const formatted = formatAmount(raw, culture);
      if (!formatted) continue;
      node.textContent = node.getAttribute("data-money-wrap") === "surcharge"
        ? "(+" + formatted + ")"
        : formatted;
    }
  }

  function elapsedMinutes(serverTimeUtc, receivedAtUtc) {
    const ms = Date.parse(serverTimeUtc) - Date.parse(receivedAtUtc);
    if (!isFinite(ms)) return 0;
    return Math.max(0, Math.floor(ms / 60000));
  }

  function collectNewIds(known, orders, baselineReady) {
    if (!baselineReady) return [];
    const fresh = [];
    for (let i = 0; i < orders.length; i++) {
      const id = String(orders[i].id);
      if (orders[i].isDemo || known.has(id)) continue;
      fresh.push(id);
    }
    return fresh;
  }

  function rememberOrderIds(known, orders) {
    for (let i = 0; i < orders.length; i++) known.add(String(orders[i].id));
  }

  /**
   * True for an isolated trainee's snapshot: the tenant is still in setup and this user is in order training, so the
   * server left real orders out and sent only how many arrived.
   */
  function isIsolatedSnapshot(snapshot) {
    return !!(snapshot && snapshot.training && snapshot.training.isolated === true);
  }

  const SUPPORTED_STATUSES = {
    New: true,
    Accepted: true,
    Preparing: true,
    ReadyForPickup: true,
    OnTheWay: true,
    Delivered: true,
    Cancelled: true,
    Failed: true
  };

  function isGuid(value) {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(String(value || ""));
  }

  function isTimestamp(value) {
    return typeof value === "string" && value.length > 0 && isFinite(Date.parse(value));
  }

  function isWholeNumber(value) {
    return typeof value === "number" && isFinite(value) && Math.floor(value) === value && value >= 0;
  }

  function validateSnapshot(snapshot) {
    if (!snapshot || typeof snapshot !== "object" || Array.isArray(snapshot)) return { ok: false, reason: "shape" };
    if (!isTimestamp(snapshot.serverTimeUtc)) return { ok: false, reason: "serverTime" };
    if (!Array.isArray(snapshot.orders)) return { ok: false, reason: "orders" };
    if (!isWholeNumber(snapshot.todayOrderCount)) return { ok: false, reason: "today" };
    if (!isWholeNumber(snapshot.cancelledOrderCount)) return { ok: false, reason: "cancelled" };
    if (snapshot.training != null) {
      const training = snapshot.training;
      if (typeof training !== "object" || Array.isArray(training)) return { ok: false, reason: "training" };
      if (!isWholeNumber(training.realOrdersReceived)) return { ok: false, reason: "training" };
    }
    const seen = new Set();
    for (let i = 0; i < snapshot.orders.length; i++) {
      const order = snapshot.orders[i];
      if (!order || typeof order !== "object" || Array.isArray(order)) return { ok: false, reason: "order" };
      const id = String(order.id || "").toLowerCase();
      if (!isGuid(id) || seen.has(id)) return { ok: false, reason: "id" };
      seen.add(id);
      if (typeof order.displayNumber !== "string") return { ok: false, reason: "displayNumber" };
      if (typeof order.platform !== "string" || !order.platform) return { ok: false, reason: "platform" };
      if (!SUPPORTED_STATUSES[order.status]) return { ok: false, reason: "status" };
      if (!isTimestamp(order.receivedAtUtc)) return { ok: false, reason: "received" };
      if (order.deliveredAtUtc != null && !isTimestamp(order.deliveredAtUtc)) return { ok: false, reason: "delivered" };
      if (typeof order.customerName !== "string") return { ok: false, reason: "customer" };
      if (order.customerAddress != null && typeof order.customerAddress !== "string") return { ok: false, reason: "address" };
      if (order.customerNote != null && typeof order.customerNote !== "string") return { ok: false, reason: "orderNote" };
      if (typeof order.totalAmount !== "number" || !isFinite(order.totalAmount)) return { ok: false, reason: "amount" };
      if (!Array.isArray(order.items)) return { ok: false, reason: "items" };
      for (let n = 0; n < order.items.length; n++) {
        const item = order.items[n];
        if (!item || typeof item !== "object" || Array.isArray(item)) return { ok: false, reason: "item" };
        if (typeof item.productName !== "string") return { ok: false, reason: "product" };
        if (typeof item.quantity !== "number" || !isFinite(item.quantity)) return { ok: false, reason: "quantity" };
        if (item.notes != null && typeof item.notes !== "string") return { ok: false, reason: "notes" };
      }
    }
    return { ok: true };
  }

  function looksLikeHtml(text) {
    const sample = String(text || "").slice(0, 400).toLowerCase();
    return sample.indexOf("<!doctype") >= 0 || sample.indexOf("<html") >= 0;
  }

  function nextBackoffMs(attempt, maxBackoffMs) {
    const cap = maxBackoffMs || 30000;
    const shift = Math.min(attempt, 4);
    return Math.min(cap, 2000 * Math.pow(2, shift));
  }

  function createRefreshCoordinator(options) {
    const intervalMs = options.intervalMs || 10000;
    const timeoutMs = options.timeoutMs || 15000;
    const maxBackoffMs = options.maxBackoffMs || 30000;
    const known = new Set();
    let generation = 0;
    let current = null;
    let timer = null;
    let retryAttempt = 0;
    let stopped = false;
    let baselineReady = false;
    // Whether the last accepted snapshot was an isolated trainee's (see isIsolatedSnapshot).
    let lastIsolated = false;
    let lastSnapshot = null;
    let pendingMutations = 0;

    function clearTimer() {
      if (timer) options.clearTimeout(timer);
      timer = null;
    }

    function schedule(delay) {
      if (stopped) return;
      clearTimer();
      timer = options.setTimeout(function () {
        timer = null;
        start("interval");
      }, delay);
    }

    function invalidateInFlight() {
      generation += 1;
      clearTimer();
      if (current) {
        current.invalid = true;
        if (typeof current.abort === "function") current.abort();
      }
    }

    function beginMutation() {
      pendingMutations += 1;
      invalidateInFlight();
      let ended = false;
      return function endMutation() {
        if (ended) return;
        ended = true;
        pendingMutations = Math.max(0, pendingMutations - 1);
        invalidateInFlight();
        if (pendingMutations === 0) requestRefresh();
      };
    }

    function requestRefresh() {
      if (stopped) return Promise.resolve({ applied: false, reason: "stopped" });
      if (pendingMutations > 0) return Promise.resolve({ applied: false, reason: "pending-mutation" });
      if (current && !current.invalid) {
        current.followUp = true;
        return current.promise;
      }
      return start("refresh");
    }

    function start(reason) {
      if (stopped) return Promise.resolve({ applied: false, reason: "stopped" });
      if (pendingMutations > 0) return Promise.resolve({ applied: false, reason: "pending-mutation" });
      if (current && !current.invalid) {
        current.followUp = true;
        return current.promise;
      }

      clearTimer();
      const controller = options.createAbortController();
      const ticket = {
        gen: generation,
        invalid: false,
        followUp: false,
        outcome: "pending",
        abort: function () { controller.abort(); },
        promise: null
      };
      current = ticket;

      let timedOut = false;
      const timeoutId = options.setTimeout(function () {
        timedOut = true;
        controller.abort();
      }, timeoutMs);

      ticket.promise = Promise.resolve()
        .then(function () { return options.fetchSnapshot(controller.signal); })
        .then(function (result) {
          if (ticket.gen !== generation || ticket.invalid || pendingMutations > 0) {
            ticket.outcome = "stale";
            return { applied: false, reason: "stale" };
          }
          if (!result || result.kind === "session") {
            stopped = true;
            clearTimer();
            ticket.outcome = "session";
            options.onStatus("session");
            return { applied: false, reason: "session" };
          }
          if (result.kind !== "ok" || !validateSnapshot(result.snapshot).ok) {
            ticket.outcome = "error";
            options.onStatus("stale");
            return { applied: false, reason: "error", kept: lastSnapshot };
          }

          // The first snapshot is the baseline: orders already there are not announced. So is the first snapshot after
          // entering or leaving isolated order training (e.g. the tenant went live): the real orders it reveals arrived
          // earlier and get no sound, browser notification or highlight. Only orders after that are new.
          const isolated = isIsolatedSnapshot(result.snapshot);
          const isBaseline = !baselineReady || isolated !== lastIsolated;
          const newIds = collectNewIds(known, result.snapshot.orders, !isBaseline);
          const meta = {
            newIds: newIds,
            isBaseline: isBaseline,
            isolated: isolated,
            reason: reason,
            payloadBytes: result.payloadBytes || 0,
            parseMs: result.parseMs || 0
          };
          return Promise.resolve(options.onAccepted(result.snapshot, meta)).then(function (accepted) {
            if (ticket.gen !== generation || ticket.invalid || pendingMutations > 0) {
              ticket.outcome = "stale";
              return { applied: false, reason: "stale" };
            }
            if (accepted && accepted.accepted === false) {
              ticket.outcome = "error";
              options.onStatus("stale");
              return { applied: false, reason: "render", kept: lastSnapshot };
            }
            rememberOrderIds(known, result.snapshot.orders);
            baselineReady = true;
            lastIsolated = isolated;
            lastSnapshot = result.snapshot;
            retryAttempt = 0;
            ticket.outcome = "ok";
            options.onStatus("ok");
            // Rendering has succeeded and the IDs are committed. Notifications must
            // neither delay polling nor roll back acceptance if audio fails.
            if (!isBaseline && newIds.length && typeof options.onNotify === "function") {
              Promise.resolve().then(function () {
                return options.onNotify(result.snapshot, meta);
              }).catch(function () { /* Notification failure does not invalidate the snapshot. */ });
            }
            return { applied: true, newIds: newIds, isBaseline: isBaseline };
          });
        })
        .catch(function () {
          if (ticket.gen !== generation || ticket.invalid || pendingMutations > 0) {
            ticket.outcome = "stale";
            return { applied: false, reason: "stale" };
          }
          ticket.outcome = "error";
          options.onStatus("stale");
          return { applied: false, reason: timedOut ? "timeout" : "error", kept: lastSnapshot };
        })
        .finally(function () {
          options.clearTimeout(timeoutId);
          if (current === ticket) current = null;
          if (stopped || ticket.gen !== generation || pendingMutations > 0) return;
          if (ticket.followUp) {
            start("follow-up");
            return;
          }
          if (ticket.outcome === "ok") schedule(intervalMs);
          else if (ticket.outcome === "error") schedule(nextBackoffMs(retryAttempt++, maxBackoffMs));
        });

      return ticket.promise;
    }

    return {
      beginMutation: beginMutation,
      requestRefresh: requestRefresh,
      start: function () { return start("start"); },
      lastSnapshot: function () { return lastSnapshot; },
      isBaselineReady: function () { return baselineReady; }
    };
  }

  function isLoginRedirect(response) {
    if (!response || !response.redirected) return false;
    const url = String(response.url || "").toLowerCase();
    return url.indexOf("/auth/login") >= 0 || url.indexOf("/account/login") >= 0;
  }

  function classifyResponse(response, text, parse) {
    const type = String((response && response.headers && response.headers.get && response.headers.get("content-type")) || "").toLowerCase();
    const status = response ? response.status : 0;
    if (status === 401 || status === 403 || isLoginRedirect(response)) return { kind: "session", status: status };
    if (!response || !response.ok) return { kind: "error", status: status };
    if (type.indexOf("text/html") >= 0 || type.indexOf("application/xhtml") >= 0 || looksLikeHtml(text)) {
      return { kind: "error", status: status };
    }
    if (type.indexOf("json") < 0) return { kind: "error", status: status };
    try {
      const snapshot = parse(text);
      if (!validateSnapshot(snapshot).ok) return { kind: "error", status: status };
      return { kind: "ok", snapshot: snapshot };
    } catch (e) {
      return { kind: "error", status: status };
    }
  }

  return {
    ACTIVE_STATUSES: ACTIVE_STATUSES,
    demoImageUrl: demoImageUrl,
    orderContentSignature: orderContentSignature,
    formatAmount: formatAmount,
    applyDetailCurrency: applyDetailCurrency,
    elapsedMinutes: elapsedMinutes,
    collectNewIds: collectNewIds,
    isIsolatedSnapshot: isIsolatedSnapshot,
    validateSnapshot: validateSnapshot,
    looksLikeHtml: looksLikeHtml,
    nextBackoffMs: nextBackoffMs,
    createRefreshCoordinator: createRefreshCoordinator,
    classifyResponse: classifyResponse
  };
});

function attachBrowser(O, global, api) {
  const hostId = "ordersLiveScreenHost";
  let coordinator = null;
  let timeFormat = null;
  let dateTimeFormat = null;
  let dayFormat = null;
  let actionFocus = null;
  let selectedOrderId = null;
  let selectionDismissed = false;
  let focusActive = false;
  let focusHadOrders = false;
  let narrowDetail = false;
  let focusOpener = null;
  let queueScrollTop = 0;
  let loadedOrderId = null;
  let loadedSignature = null;
  let failedOrderId = null;
  let failedSignature = null;
  let loadingOrderId = null;
  let loadingSignature = null;
  let detailStale = false;
  let detailGeneration = 0;
  let renderedSnapshot = null;
  let narrowMedia = null;
  let boardOverflowObserver = null;
  let boardItemsResizeObserver = null;
  let boardItemsResizePending = null;
  let boardOverflowResizeBound = false;
  const cardTimes = new WeakMap();
  const cardItems = new WeakMap();
  const lastTimings = {
    payloadBytes: 0,
    parseMs: 0,
    renderMs: 0,
    orderCount: 0,
    changedCards: 0,
    addedCards: 0,
    removedCards: 0,
    unchangedCards: 0
  };

  function message(key) {
    return O.getMessage(key);
  }

  function culture() {
    return O.opts.displayCulture || (document.documentElement && document.documentElement.lang) || "tr-TR";
  }

  function timeZone() {
    return O.opts.timeZoneId || "Europe/Istanbul";
  }

  function formatMoney(amount) {
    return api.formatAmount(amount, culture());
  }

  function formatListMoney(amount) {
    return formatMoney(amount);
  }

  function ensureDateFormatters() {
    if (timeFormat) return;
    const zone = timeZone();
    timeFormat = new Intl.DateTimeFormat(culture(), { timeZone: zone, timeStyle: "short" });
    dateTimeFormat = new Intl.DateTimeFormat(culture(), { timeZone: zone, dateStyle: "short", timeStyle: "short" });
    dayFormat = new Intl.DateTimeFormat("en-CA", {
      timeZone: zone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit"
    });
  }

  function formatReceived(receivedAtUtc, serverTimeUtc) {
    const received = new Date(receivedAtUtc);
    const server = new Date(serverTimeUtc);
    if (isNaN(received.getTime())) return "";
    ensureDateFormatters();
    const receivedDay = dayFormat.format(received);
    const serverDay = isNaN(server.getTime()) ? "" : dayFormat.format(server);
    return receivedDay === serverDay ? timeFormat.format(received) : dateTimeFormat.format(received);
  }

  function markReceivedDay(row, receivedAtUtc, serverTimeUtc) {
    if (!row) return;
    const received = new Date(receivedAtUtc);
    const server = new Date(serverTimeUtc);
    if (isNaN(received.getTime()) || isNaN(server.getTime())) {
      row.setAttribute("data-same-day", "false");
      return;
    }
    ensureDateFormatters();
    row.setAttribute("data-same-day", dayFormat.format(received) === dayFormat.format(server) ? "true" : "false");
  }

  function formatElapsed(serverTimeUtc, receivedAtUtc) {
    const minutes = api.elapsedMinutes(serverTimeUtc, receivedAtUtc);
    if (minutes >= 60) {
      const template = message("elapsedHoursMinutes");
      const hours = Math.floor(minutes / 60);
      const rest = minutes % 60;
      if (template.indexOf("{0}") >= 0) return template.replace("{0}", String(hours)).replace("{1}", String(rest));
    }
    const template = message("elapsedMinutes");
    return template.indexOf("{0}") >= 0 ? template.replace("{0}", String(minutes)) : String(minutes);
  }

  function setText(el, value) {
    if (el) el.textContent = value == null ? "" : String(value);
  }

  function connectionEl() {
    return document.getElementById("ordersLiveDisplayConnection");
  }

  function onStatus(kind) {
    const el = connectionEl();
    if (!el) return;
    if (kind === "ok") {
      el.hidden = true;
      el.textContent = "";
      return;
    }
    el.hidden = false;
    el.classList.toggle("text-danger", kind === "session");
    el.classList.toggle("text-warning", kind !== "session");
    setText(el, message(kind === "session" ? "liveScreenSessionExpired" : "liveScreenStale"));
  }

  function setSummary(snapshot) {
    let active = 0;
    const orders = snapshot.orders || [];
    for (let i = 0; i < orders.length; i++) {
      if (orders[i].isDemo) continue;
      if (api.ACTIVE_STATUSES[orders[i].status]) active += 1;
    }
    const activeEl = document.getElementById("ordersLiveDisplayActiveCount");
    if (activeEl) activeEl.textContent = String(active);
    const todayEl = document.getElementById("ordersLiveDisplayTodayCount");
    if (todayEl) todayEl.textContent = String(snapshot.todayOrderCount);
    const cancelledEl = document.getElementById("ordersLiveDisplayCancelledCount");
    if (cancelledEl) cancelledEl.textContent = String(snapshot.cancelledOrderCount);
  }

  function touchUpdated() {
    const el = document.getElementById("ordersLiveDisplayLastUpdated");
    if (!el) return;
    try {
      el.textContent = new Date().toLocaleTimeString(culture());
    } catch (e) {
      el.textContent = new Date().toLocaleTimeString();
    }
  }

  function platformSpec(platform) {
    if (platform === "Yemeksepeti") {
      return { css: "platform-badge--yemeksepeti platform-badge--wordmark", icon: "/images/platforms/yemeksepeti.svg", label: message("platformYemeksepeti") };
    }
    if (platform === "GetirYemek") {
      return { css: "platform-badge--getiryemek platform-badge--wordmark", icon: "/images/platforms/getiryemek.svg", label: message("platformGetirYemek"), wrap: true };
    }
    if (platform === "TrendyolYemek") {
      return { css: "platform-badge--trendyol platform-badge--wordmark", icon: "/images/platforms/trendyolgo.svg", label: message("platformTrendyolYemek") };
    }
    return { css: "", icon: "", label: platform || "" };
  }

  function statusClass(status) {
    if (status === "New") return "wasla-dash-status wasla-dash-status--new";
    if (status === "Accepted") return "wasla-dash-status wasla-dash-status--accepted";
    if (status === "Preparing") return "wasla-dash-status wasla-dash-status--progress";
    if (status === "ReadyForPickup") return "wasla-dash-status wasla-dash-status--ready";
    if (status === "OnTheWay") return "wasla-dash-status wasla-dash-status--on-the-way";
    if (status === "Delivered") return "wasla-dash-status wasla-dash-status--delivered";
    if (status === "Cancelled") return "wasla-dash-status wasla-dash-status--cancelled";
    if (status === "Failed") return "wasla-dash-status wasla-dash-status--failed";
    return "wasla-dash-status";
  }

  function statusLabel(status) {
    return message("status" + status) || status;
  }

  function actionButtons(status, isDemo) {
    if (!O.opts.canManageOrders) return [];
    if (status === "New") {
      const approve = { action: "approve", className: "btn btn-success btn-lg", label: message("ordersApprove") };
      // A practice order is only approved: rejecting it would end the training, and the server refuses it.
      if (isDemo) return [approve];
      return [
        approve,
        { action: "reject", className: "btn btn-outline-danger btn-lg", label: message("ordersReject") }
      ];
    }
    if (status === "Accepted") return [{ action: "start-preparing", className: "btn btn-primary btn-lg", label: message("ordersStartPreparing") }];
    if (status === "Preparing") return [{ action: "mark-ready", className: "btn btn-primary btn-lg", label: message("ordersMarkReady") }];
    // Mark ready is the restaurant's last action: the platform courier reports OnTheWay and
    // Delivered through provider sync, so ReadyForPickup and OnTheWay have no action.
    return [];
  }

  function fillActions(container, order) {
    while (container.firstChild) container.removeChild(container.firstChild);
    const buttons = actionButtons(order.status, !!order.isDemo);
    if (!buttons.length) {
      container.hidden = true;
      container.removeAttribute("data-tour");
      return;
    }
    container.hidden = false;
    if (order.isDemo) {
      container.setAttribute("data-tour", "demo-order-actions");
    } else {
      container.removeAttribute("data-tour");
    }
    buttons.forEach(function (spec) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = spec.className;
      button.setAttribute("data-order-action", spec.action);
      button.setAttribute("data-order-id", String(order.id));
      if (order.isDemo) button.setAttribute("data-wasla-demo", "true");
      button.textContent = spec.label;
      container.appendChild(button);
    });
  }

  function markDemo(article, order) {
    if (!order || !order.isDemo) return;
    article.setAttribute("data-wasla-demo", "true");
    article.setAttribute("data-wasla-demo-card", "true");
    article.setAttribute("data-tour", "demo-order-card");
  }

  function createCard(order, serverTimeUtc) {
    const article = document.createElement("article");
    const muted = order.status === "Delivered" || order.status === "Cancelled" || order.status === "Failed";
    article.className = "orders-card orders-card--kitchen wasla-orders-card wasla-orders-card--kitchen wasla-live-screen-card"
      + (muted ? " orders-card--muted" : "");
    article.setAttribute("role", "listitem");
    article.setAttribute("data-order-id", String(order.id));
    article.setAttribute("data-order-status", order.status || "");
    article.setAttribute("data-received-at", order.receivedAtUtc || "");
    article.setAttribute("data-live-layout", "board");
    markDemo(article, order);

    const imageWrap = document.createElement("div");
    imageWrap.className = "orders-card-image orders-card-image--kitchen";
    const image = document.createElement("img");
    image.src = api.demoImageUrl(order);
    image.alt = (order.items && order.items[0] && order.items[0].productName) || "";
    image.loading = "lazy";
    imageWrap.appendChild(image);

    const body = document.createElement("div");
    body.className = "orders-card-body";

    const head = document.createElement("div");
    head.className = "wasla-live-card__head";
    const platformHost = document.createElement("div");
    platformHost.className = "orders-card-platform";
    platformHost.appendChild(createPlatformBadge(order.platform, order));
    const status = document.createElement("span");
    status.className = statusClass(order.status);
    status.setAttribute("data-status-badge", "");
    status.textContent = statusLabel(order.status);
    const fresh = document.createElement("span");
    fresh.className = "badge text-bg-primary d-none wasla-orders-new-badge";
    fresh.setAttribute("data-new-badge", "");
    fresh.textContent = message("statusNewBadge");
    const elapsed = document.createElement("span");
    elapsed.className = "wasla-live-screen-card__elapsed";
    elapsed.setAttribute("data-elapsed", "");
    elapsed.setAttribute("data-tour", "live-timer");
    elapsed.textContent = formatElapsed(serverTimeUtc, order.receivedAtUtc);
    head.appendChild(platformHost);
    head.appendChild(status);
    head.appendChild(fresh);
    head.appendChild(elapsed);

    const code = document.createElement("div");
    code.className = "orders-card-code wasla-live-screen-card__code";
    const link = document.createElement(order.isDemo ? "span" : "a");
    if (!order.isDemo) link.href = "/orders/details/" + encodeURIComponent(String(order.id));
    link.textContent = order.displayNumber || "";
    code.appendChild(link);

    const identity = document.createElement("div");
    identity.className = "wasla-live-card__identity";
    identity.appendChild(head);
    identity.appendChild(code);

    const itemsShell = document.createElement("div");
    itemsShell.className = "wasla-live-board-items";
    const itemsViewport = document.createElement("div");
    itemsViewport.className = "wasla-live-board-items__viewport";
    const items = document.createElement("ul");
    items.className = "wasla-live-screen-card__items list-unstyled";
    fillItems(items, order.items || []);
    const itemsMore = document.createElement("p");
    itemsMore.className = "wasla-live-board-items__more";
    itemsMore.textContent = message("boardItemsMore");
    itemsViewport.appendChild(items);
    itemsShell.appendChild(itemsViewport);
    itemsShell.appendChild(itemsMore);

    const totalRow = metaRow(message("ordersFullscreenTotal"), formatMoney(order.totalAmount), "data-total");
    const secondary = document.createElement("div");
    secondary.className = "wasla-live-card__secondary";
    secondary.appendChild(totalRow);

    const actions = document.createElement("div");
    actions.className = "orders-card-actions wasla-orders-card__actions";
    const actionGroup = document.createElement("div");
    actionGroup.className = "wasla-orders-actions";
    actionGroup.setAttribute("role", "group");
    actionGroup.setAttribute("aria-label", message("ordersActions"));
    actionGroup.setAttribute("data-order-actions", "");
    fillActions(actionGroup, order);
    const details = document.createElement("button");
    details.type = "button";
    details.className = "btn btn-outline-secondary wasla-live-screen-card__details";
    details.setAttribute("data-order-detail", String(order.id));
    details.textContent = message("viewDetails");
    if (order.isDemo) details.hidden = true;
    actions.appendChild(actionGroup);
    actions.appendChild(details);

    body.appendChild(identity);
    body.appendChild(itemsShell);
    body.appendChild(secondary);
    body.appendChild(actions);
    article.appendChild(imageWrap);
    article.appendChild(body);
    syncBoardOrderNote(article, order);
    article.setAttribute("data-live-signature", api.orderContentSignature(order));
    cardItems.set(article, JSON.stringify(order.items || []));
    cardTimes.set(article, timeKey(order, serverTimeUtc));
    return article;
  }

  function createListRow(order, serverTimeUtc) {
    const article = document.createElement("article");
    const muted = order.status === "Delivered" || order.status === "Cancelled" || order.status === "Failed";
    article.className = "wasla-live-screen-card wasla-live-list-row" + (muted ? " orders-card--muted" : "");
    article.setAttribute("role", "listitem");
    article.setAttribute("data-order-id", String(order.id));
    article.setAttribute("data-order-status", order.status || "");
    article.setAttribute("data-received-at", order.receivedAtUtc || "");
    article.setAttribute("data-live-layout", "list");
    markDemo(article, order);

    const identity = document.createElement("div");
    identity.className = "wasla-live-list-row__identity";
    const headline = document.createElement("div");
    headline.className = "wasla-live-list-row__headline";
    const platformHost = document.createElement("div");
    platformHost.className = "orders-card-platform";
    platformHost.appendChild(createPlatformBadge(order.platform, order));
    const code = document.createElement("div");
    code.className = "orders-card-code wasla-live-screen-card__code";
    const link = document.createElement(order.isDemo ? "span" : "a");
    if (!order.isDemo) link.href = "/orders/details/" + encodeURIComponent(String(order.id));
    link.textContent = order.displayNumber || "";
    code.appendChild(link);
    const statusSlot = document.createElement("div");
    statusSlot.className = "wasla-live-list-row__status";
    const status = document.createElement("span");
    status.className = statusClass(order.status);
    status.setAttribute("data-status-badge", "");
    status.textContent = statusLabel(order.status);
    const fresh = document.createElement("span");
    fresh.className = "badge text-bg-primary d-none wasla-orders-new-badge";
    fresh.setAttribute("data-new-badge", "");
    fresh.textContent = message("statusNewBadge");
    statusSlot.appendChild(status);
    statusSlot.appendChild(fresh);
    headline.appendChild(platformHost);
    headline.appendChild(code);
    headline.appendChild(statusSlot);
    const meta = document.createElement("div");
    meta.className = "wasla-live-list-row__meta";
    const received = document.createElement("div");
    received.className = "wasla-live-list-row__received";
    received.setAttribute("data-received-row", "");
    const receivedLabel = document.createElement("span");
    receivedLabel.className = "wasla-live-list-row__received-label";
    receivedLabel.textContent = message("ordersFullscreenReceived");
    const receivedValue = document.createElement("span");
    receivedValue.setAttribute("data-received", "");
    receivedValue.textContent = formatReceived(order.receivedAtUtc, serverTimeUtc);
    received.appendChild(receivedLabel);
    received.appendChild(receivedValue);
    markReceivedDay(received, order.receivedAtUtc, serverTimeUtc);
    meta.appendChild(received);
    identity.appendChild(headline);
    identity.appendChild(meta);

    const customer = document.createElement("div");
    customer.className = "wasla-live-list-row__customer";
    const customerIdentity = document.createElement("div");
    customerIdentity.className = "wasla-live-list-row__customer-identity";
    const customerMain = document.createElement("div");
    customerMain.className = "wasla-live-list-row__customer-main";
    const customerName = document.createElement("span");
    customerName.className = "wasla-live-list-row__customer-name";
    customerName.setAttribute("data-customer-name", "");
    const customerAddress = document.createElement("p");
    customerAddress.className = "wasla-live-list-row__customer-address";
    customerAddress.setAttribute("data-customer-address", "");
    customerMain.appendChild(customerName);
    customerIdentity.appendChild(customerMain);
    customerIdentity.appendChild(customerAddress);
    customer.appendChild(customerIdentity);
    syncListCustomer(customer, order);
    const total = document.createElement("div");
    total.className = "wasla-live-list-row__total";
    const totalLabel = document.createElement("span");
    totalLabel.className = "wasla-live-list-row__total-label";
    totalLabel.textContent = message("listTotal");
    const totalValue = document.createElement("span");
    totalValue.className = "wasla-live-list-row__total-value";
    totalValue.setAttribute("data-total", "");
    totalValue.textContent = formatListMoney(order.totalAmount);
    total.appendChild(totalLabel);
    total.appendChild(totalValue);

    const items = document.createElement("ul");
    items.className = "wasla-live-screen-card__items wasla-live-list-row__items list-unstyled";
    fillItems(items, order.items || []);

    const elapsed = document.createElement("span");
    elapsed.className = "wasla-live-screen-card__elapsed wasla-live-list-row__elapsed";
    elapsed.setAttribute("data-elapsed", "");
    elapsed.setAttribute("data-tour", "live-timer");
    elapsed.textContent = formatElapsed(serverTimeUtc, order.receivedAtUtc);

    const aside = document.createElement("div");
    aside.className = "wasla-live-list-row__aside";

    const actions = document.createElement("div");
    actions.className = "orders-card-actions wasla-orders-card__actions wasla-live-list-row__actions";
    const actionGroup = document.createElement("div");
    actionGroup.className = "wasla-orders-actions";
    actionGroup.setAttribute("role", "group");
    actionGroup.setAttribute("aria-label", message("ordersActions"));
    actionGroup.setAttribute("data-order-actions", "");
    fillActions(actionGroup, order);
    const details = document.createElement("button");
    details.type = "button";
    details.className = "btn btn-outline-secondary wasla-live-screen-card__details";
    details.setAttribute("data-order-detail", String(order.id));
    details.textContent = message("viewDetails");
    if (order.isDemo) details.hidden = true;
    actions.appendChild(actionGroup);
    actions.appendChild(details);
    aside.appendChild(actions);

    article.appendChild(identity);
    article.appendChild(items);
    article.appendChild(customer);
    article.appendChild(total);
    article.appendChild(elapsed);
    article.appendChild(aside);
    article.setAttribute("data-live-signature", api.orderContentSignature(order));
    cardItems.set(article, JSON.stringify(order.items || []));
    cardTimes.set(article, timeKey(order, serverTimeUtc));
    return article;
  }

  function createPlatformBadge(platform, order) {
    if (order && order.isDemo) {
      const badge = document.createElement("span");
      badge.className = "platform-badge wasla-demo-badge";
      const label = message("demoBadge");
      badge.setAttribute("aria-label", label);
      badge.textContent = label;
      return badge;
    }
    const spec = platformSpec(platform);
    const badge = document.createElement("span");
    badge.className = "platform-badge " + spec.css;
    badge.setAttribute("aria-label", spec.label);
    badge.title = spec.label;
    if (spec.icon) {
      const image = document.createElement("img");
      image.className = "platform-badge__icon";
      image.src = spec.icon;
      image.alt = "";
      image.setAttribute("aria-hidden", "true");
      if (spec.wrap) {
        const wrap = document.createElement("span");
        wrap.className = "platform-badge__icon-wrap";
        wrap.setAttribute("aria-hidden", "true");
        wrap.appendChild(image);
        badge.appendChild(wrap);
      } else {
        badge.appendChild(image);
      }
    }
    const text = document.createElement("span");
    text.className = "platform-badge__text";
    text.textContent = spec.label;
    badge.appendChild(text);
    return badge;
  }

  function metaRow(label, value, valueAttr) {
    const row = document.createElement("div");
    row.className = "orders-card-meta__row";
    const labelEl = document.createElement("span");
    labelEl.className = "orders-card-meta__label";
    labelEl.textContent = label + ":";
    const valueEl = document.createElement("span");
    valueEl.setAttribute(valueAttr, "");
    valueEl.textContent = " " + value;
    row.appendChild(labelEl);
    row.appendChild(valueEl);
    return row;
  }

  function fillItems(list, items) {
    const listLayout = list.className.indexOf("wasla-live-list-row__items") >= 0;
    while (list.firstChild) list.removeChild(list.firstChild);
    items.forEach(function (item) {
      const li = document.createElement("li");
      li.className = "wasla-live-screen-card__item";
      const qty = document.createElement("span");
      qty.className = "wasla-live-screen-card__qty";
      qty.textContent = String(item.quantity) + "×";
      const product = document.createElement("span");
      product.className = "wasla-live-screen-card__product";
      product.textContent = item.productName || "";
      li.appendChild(qty);
      li.appendChild(document.createTextNode(" "));
      li.appendChild(product);
      const noteText = item.notes == null ? "" : String(item.notes);
      if (noteText.trim()) {
        const note = document.createElement("div");
        if (listLayout) {
          note.className = "wasla-live-screen-card__note wasla-live-list-row__item-note";
          const icon = document.createElement("span");
          icon.className = "bi bi-sticky wasla-live-list-row__note-icon";
          icon.setAttribute("aria-hidden", "true");
          const label = document.createElement("span");
          label.className = "visually-hidden";
          label.textContent = message("itemNote");
          const text = document.createElement("span");
          text.className = "wasla-live-list-row__note-text";
          text.textContent = noteText;
          note.appendChild(icon);
          note.appendChild(label);
          note.appendChild(text);
        } else {
          note.className = "wasla-live-screen-card__note text-muted";
          note.textContent = noteText;
        }
        li.appendChild(note);
      }
      list.appendChild(li);
    });
    list.hidden = items.length === 0;
  }

  function orderNoteText(order) {
    if (!order || order.customerNote == null) return "";
    return String(order.customerNote).trim();
  }

  function syncBoardOrderNote(root, order) {
    const card = root.getAttribute && root.getAttribute("data-live-layout") === "board"
      ? root
      : (root.closest ? root.closest(".wasla-live-screen-card") : null);
    const body = card && card.querySelector ? card.querySelector(".orders-card-body") : null;
    if (!body) return;
    const note = orderNoteText(order);
    let block = body.querySelector("[data-order-note]");
    if (!note) {
      if (block) block.remove();
      return;
    }
    if (!block) {
      block = document.createElement("div");
      block.className = "wasla-live-board-order-note";
      block.setAttribute("data-order-note", "");
      const label = document.createElement("span");
      label.className = "wasla-live-board-order-note__label";
      label.textContent = message("orderNote");
      const text = document.createElement("p");
      text.className = "wasla-live-board-order-note__text";
      text.setAttribute("data-order-note-text", "");
      block.appendChild(label);
      block.appendChild(text);
    }
    const items = body.querySelector(".wasla-live-board-items");
    if (block.parentElement !== body || (items && block.nextElementSibling !== items)) {
      if (items) body.insertBefore(block, items);
      else body.appendChild(block);
    }
    const textEl = block.querySelector("[data-order-note-text]");
    if (textEl && textEl.textContent !== note) textEl.textContent = note;
    if (textEl) textEl.title = note;
  }

  function syncListOrderNote(customer, order) {
    const note = orderNoteText(order);
    let block = customer.querySelector("[data-order-note]");
    if (!note) {
      if (block) block.remove();
      customer.classList.toggle("wasla-live-list-row__customer--with-note", false);
      return;
    }
    if (!block) {
      block = document.createElement("div");
      block.className = "wasla-live-list-row__order-note";
      block.setAttribute("data-order-note", "");
      const label = document.createElement("span");
      label.className = "wasla-live-list-row__order-note-label";
      label.textContent = message("orderNote");
      const text = document.createElement("p");
      text.className = "wasla-live-list-row__order-note-text";
      text.setAttribute("data-order-note-text", "");
      block.appendChild(label);
      block.appendChild(text);
      customer.appendChild(block);
    }
    const textEl = block.querySelector("[data-order-note-text]");
    if (textEl && textEl.textContent !== note) textEl.textContent = note;
    if (textEl) textEl.title = note;
    customer.classList.toggle("wasla-live-list-row__customer--with-note", true);
  }

  function syncListCustomer(root, order) {
    const customer = root.className && root.className.indexOf("wasla-live-list-row__customer") >= 0
      ? root
      : root.querySelector(".wasla-live-list-row__customer");
    if (!customer) return;
    const name = order.customerName == null ? "" : String(order.customerName);
    const address = order.customerAddress == null ? "" : String(order.customerAddress).trim();
    const nameEl = customer.querySelector("[data-customer-name]");
    if (nameEl && nameEl.className.indexOf("wasla-live-list-row__customer-name") >= 0) {
      if (nameEl.textContent !== name) nameEl.textContent = name;
      nameEl.hidden = !name.trim();
    }
    const addressEl = customer.querySelector("[data-customer-address]");
    if (addressEl) {
      if (addressEl.textContent !== address) addressEl.textContent = address;
      addressEl.hidden = !address;
      addressEl.title = address;
    }
    syncListOrderNote(customer, order);
  }

  const ORDER_NOTE_ATTENTION_MS = 10000;
  const ORDER_NOTE_ATTENTION_CLASS = "wasla-order-note-attention";
  const ORDER_NOTE_ATTENTION_STATIC_CLASS = "wasla-order-note-attention-static";
  const sequencedOrderNotes = new Set();
  const noteAttention = new Map();

  function noteNow() {
    const date = global.Date || Date;
    return date.now();
  }

  function noteLater(fn, ms) {
    const schedule = global.setTimeout || setTimeout;
    return schedule(fn, ms);
  }

  function noteCancel(timerId) {
    if (timerId == null) return;
    const clear = global.clearTimeout || clearTimeout;
    clear(timerId);
  }

  function prefersReducedMotion() {
    try {
      if (!global.matchMedia) return false;
      return !!global.matchMedia("(prefers-reduced-motion: reduce)").matches;
    } catch (error) {
      return false;
    }
  }

  function findLiveOrderNote(orderId) {
    const host = document.getElementById(hostId);
    if (!host) return null;
    const cards = host.querySelectorAll(".wasla-live-screen-card, .wasla-live-detail");
    const id = String(orderId);
    for (let i = 0; i < cards.length; i++) {
      const card = cards[i];
      if (card.getAttribute("data-order-id") !== id) continue;
      if (card.classList.contains("wasla-live-detail")) return card.querySelector("[data-order-note]");
      const layout = card.getAttribute("data-live-layout");
      if (layout !== "list" && layout !== "board") return null;
      return card.querySelector("[data-order-note]");
    }
    return null;
  }

  function snapshotHasOrderNote(orderId) {
    const orders = renderedSnapshot && renderedSnapshot.orders;
    if (!orders) return false;
    const id = String(orderId);
    for (let i = 0; i < orders.length; i++) {
      if (String(orders[i].id) === id) return orderNoteText(orders[i]).length > 0;
    }
    return false;
  }

  function clearOrderNoteAttentionPaint(block) {
    if (!block || !block.classList) return;
    block.classList.remove(ORDER_NOTE_ATTENTION_CLASS, ORDER_NOTE_ATTENTION_STATIC_CLASS);
    if (block.style && typeof block.style.removeProperty === "function") {
      block.style.removeProperty("--wasla-order-note-attention-delay");
    }
    if (typeof block.removeAttribute === "function") block.removeAttribute("data-order-note-attention-delay");
  }

  function paintOrderNoteAttention(orderId) {
    const state = noteAttention.get(String(orderId));
    if (!state || state.phase !== "running") return;
    const block = findLiveOrderNote(orderId);
    if (!block || !block.classList) return;
    const reduced = prefersReducedMotion();
    const active = reduced ? ORDER_NOTE_ATTENTION_STATIC_CLASS : ORDER_NOTE_ATTENTION_CLASS;
    const inactive = reduced ? ORDER_NOTE_ATTENTION_CLASS : ORDER_NOTE_ATTENTION_STATIC_CLASS;
    if (block.classList.contains(active) && !block.classList.contains(inactive)) return;
    block.classList.remove(ORDER_NOTE_ATTENTION_CLASS, ORDER_NOTE_ATTENTION_STATIC_CLASS);
    if (!reduced) {
      const elapsed = Math.max(0, noteNow() - (state.endsAt - ORDER_NOTE_ATTENTION_MS));
      const delay = (-elapsed) + "ms";
      block.setAttribute("data-order-note-attention-delay", delay);
      if (block.style && typeof block.style.setProperty === "function") {
        block.style.setProperty("--wasla-order-note-attention-delay", delay);
      }
    }
    block.classList.add(active);
  }

  function endOrderNoteAttention(orderId) {
    const id = String(orderId);
    const state = noteAttention.get(id);
    if (state && state.timer != null) {
      noteCancel(state.timer);
      state.timer = null;
    }
    if (state) state.phase = "done";
    clearOrderNoteAttentionPaint(findLiveOrderNote(id));
  }

  function beginOrderNoteAttention(orderId) {
    const id = String(orderId);
    if (sequencedOrderNotes.has(id)) return;
    sequencedOrderNotes.add(id);
    if (!snapshotHasOrderNote(id)) return;
    const endsAt = noteNow() + ORDER_NOTE_ATTENTION_MS;
    const state = { phase: "running", endsAt: endsAt, timer: null };
    noteAttention.set(id, state);
    paintOrderNoteAttention(id);
    state.timer = noteLater(function () {
      try {
        const current = noteAttention.get(id);
        if (!current || current !== state || current.phase !== "running") return;
        current.timer = null;
        endOrderNoteAttention(id);
      } catch (error) {
        if (typeof O.debugWarn === "function") O.debugWarn("orderNoteAttention", error);
      }
    }, ORDER_NOTE_ATTENTION_MS);
  }

  function syncOrderNoteAttention() {
    const orders = (renderedSnapshot && renderedSnapshot.orders) || [];
    const present = new Set();
    for (let i = 0; i < orders.length; i++) present.add(String(orders[i].id));
    Array.from(noteAttention.keys()).forEach(function (id) {
      const state = noteAttention.get(id);
      if (!state || state.phase !== "running") return;
      if (!present.has(id)) {
        if (state.timer != null) {
          noteCancel(state.timer);
          state.timer = null;
        }
        state.phase = "done";
        return;
      }
      if (noteNow() >= state.endsAt || !snapshotHasOrderNote(id)) {
        endOrderNoteAttention(id);
        return;
      }
      paintOrderNoteAttention(id);
    });
  }

  function finishRender(result) {
    try { syncFocusOrderNoteFromSnapshot(); } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("focusOrderNote", error);
    }
    try {
      // The highlight map is the duration source of truth. Re-paint after
      // reconciliation so a reused or replaced list row keeps the class for
      // the remaining window without scheduling another timer.
      if (O.table && typeof O.table.applyNewOrderVisualState === "function") {
        O.table.applyNewOrderVisualState();
      }
    } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("applyNewOrderVisualState", error);
    }
    try {
      syncOrderNoteAttention();
    } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("orderNoteAttention", error);
    }
    return result;
  }

  function orderNoteAttentionState(orderId) {
    const id = String(orderId);
    const state = noteAttention.get(id);
    let phase = "idle";
    if (state) phase = state.phase;
    else if (sequencedOrderNotes.has(id)) phase = "skipped";
    return {
      sequenced: sequencedOrderNotes.has(id),
      phase: phase,
      endsAt: state ? state.endsAt : null,
      durationMs: ORDER_NOTE_ATTENTION_MS
    };
  }

  function timeKey(order, serverTimeUtc) {
    return Math.floor(Date.parse(serverTimeUtc) / 3600000) + "|" + api.elapsedMinutes(serverTimeUtc, order.receivedAtUtc) + "|" + order.receivedAtUtc;
  }

  function syncTimeLabels(card, order, serverTimeUtc) {
    const key = timeKey(order, serverTimeUtc);
    if (cardTimes.get(card) === key) return false;
    const elapsed = formatElapsed(serverTimeUtc, order.receivedAtUtc);
    const received = (card.getAttribute("data-live-layout") === "list" ? "" : " ") + formatReceived(order.receivedAtUtc, serverTimeUtc);
    let changed = false;
    const elapsedEl = card.querySelector("[data-elapsed]");
    if (elapsedEl && elapsedEl.textContent !== elapsed) {
      elapsedEl.textContent = elapsed;
      changed = true;
    }
    const receivedEl = card.querySelector("[data-received]");
    if (receivedEl && receivedEl.textContent !== received) {
      receivedEl.textContent = received;
      changed = true;
    }
    markReceivedDay(card.querySelector("[data-received-row]"), order.receivedAtUtc, serverTimeUtc);
    cardTimes.set(card, key);
    return changed;
  }

  function updateCard(card, order, serverTimeUtc) {
    const signature = api.orderContentSignature(order);
    if (card.getAttribute("data-live-signature") === signature) {
      syncTimeLabels(card, order, serverTimeUtc);
      return false;
    }
    const statusChanged = card.getAttribute("data-order-status") !== (order.status || "");
    const muted = order.status === "Delivered" || order.status === "Cancelled" || order.status === "Failed";
    card.classList.toggle("orders-card--muted", muted);
    card.setAttribute("data-order-status", order.status || "");
    card.setAttribute("data-received-at", order.receivedAtUtc || "");
    const image = card.querySelector("img");
    if (image && image.className.indexOf("platform-badge") < 0) {
      image.src = api.demoImageUrl(order);
      image.alt = (order.items && order.items[0] && order.items[0].productName)
        || (card.getAttribute("data-live-layout") === "board" ? "" : (order.customerName || ""));
    }
    const status = card.querySelector("[data-status-badge]");
    if (status) {
      status.className = statusClass(order.status);
      status.textContent = statusLabel(order.status);
    }
    const link = card.querySelector(".wasla-live-screen-card__code a");
    if (link) link.textContent = order.displayNumber || "";
    if (card.getAttribute("data-live-layout") === "list") syncListCustomer(card, order);
    else if (card.getAttribute("data-live-layout") === "board") {
      const customer = card.querySelector(".orders-card-customer");
      if (customer) customer.remove();
      syncBoardOrderNote(card, order);
    }
    const items = card.querySelector(".wasla-live-screen-card__items");
    const itemsSignature = JSON.stringify(order.items || []);
    if (items && cardItems.get(card) !== itemsSignature) {
      fillItems(items, order.items || []);
      cardItems.set(card, itemsSignature);
    }
    setText(
      card.querySelector("[data-total]"),
      card.getAttribute("data-live-layout") === "list"
        ? formatListMoney(order.totalAmount)
        : (" " + formatMoney(order.totalAmount)));
    setText(card.querySelector("[data-received]"), (card.getAttribute("data-live-layout") === "list" ? "" : " ") + formatReceived(order.receivedAtUtc, serverTimeUtc));
    setText(card.querySelector("[data-elapsed]"), formatElapsed(serverTimeUtc, order.receivedAtUtc));
    markReceivedDay(card.querySelector("[data-received-row]"), order.receivedAtUtc, serverTimeUtc);
    if (statusChanged) {
      const actions = card.querySelector("[data-order-actions]");
      if (actions) fillActions(actions, order);
    }
    card.setAttribute("data-live-signature", signature);
    cardTimes.set(card, timeKey(order, serverTimeUtc));
    return true;
  }

  const boardColumns = [
    { key: "new", label: "statusNew" },
    { key: "preparing", label: "statusPreparing" },
    { key: "ready", label: "statusReadyForPickup" },
    { key: "on-the-way", label: "statusOnTheWay" },
    { key: "completed", label: "boardCompleted" }
  ];

  function columnKey(status) {
    if (status === "New" || status === "Accepted") return "new";
    if (status === "Preparing") return "preparing";
    if (status === "ReadyForPickup") return "ready";
    if (status === "OnTheWay") return "on-the-way";
    // The endpoint currently returns Delivered as its only terminal status.
    // If that contract expands, retain the actual status rather than dropping orders.
    return "completed";
  }

  function groupRank(status) {
    const key = columnKey(status);
    if (key === "new") return 0;
    if (key === "preparing") return 1;
    if (key === "ready") return 2;
    if (key === "on-the-way") return 3;
    return 4;
  }

  // Newest received time first within a visual group, then the immutable id.
  // Actual status must not separate New from Accepted or reshuffle a group.
  function comparePresentation(a, b) {
    const group = groupRank(a.status) - groupRank(b.status);
    if (group) return group;
    const timeA = Date.parse(a.receivedAtUtc);
    const timeB = Date.parse(b.receivedAtUtc);
    const validA = timeA === timeA;
    const validB = timeB === timeB;
    if (validA && validB && timeA !== timeB) return timeB - timeA;
    if (validA !== validB) return validA ? -1 : 1;
    const idA = String(a.id);
    const idB = String(b.id);
    if (idA < idB) return -1;
    if (idA > idB) return 1;
    return 0;
  }

  function presentationOrders(orders) {
    return orders.slice().sort(comparePresentation);
  }

  function createGroupedLayout(mode) {
    const board = mode === "board";
    const focus = mode === "focus";
    const root = document.createElement("div");
    root.className = board ? "wasla-live-board" : (focus ? "wasla-live-focus__groups" : "wasla-live-groups");
    const prefix = board ? "live-board-" : (focus ? "live-focus-" : "live-list-");
    boardColumns.forEach(function (spec) {
      const column = document.createElement("section");
      column.className = board ? "wasla-live-board__column" : (focus ? "wasla-live-focus__group" : "wasla-live-group");
      column.setAttribute("data-board-column", spec.key);
      const heading = document.createElement("h2");
      heading.id = prefix + spec.key;
      heading.className = board ? "wasla-live-board__heading" : (focus ? "wasla-live-focus__heading" : "wasla-live-group__heading");
      column.setAttribute("aria-labelledby", heading.id);
      const title = document.createElement("span");
      title.textContent = message(spec.label);
      const count = document.createElement("span");
      count.className = board ? "wasla-live-board__count" : (focus ? "wasla-live-focus__count" : "wasla-live-group__count");
      count.setAttribute("data-board-count", "");
      heading.appendChild(title);
      heading.appendChild(count);
      const list = document.createElement("div");
      list.className = board ? "wasla-live-board__orders" : (focus ? "wasla-live-focus__orders" : "wasla-live-group__orders");
      list.setAttribute("data-board-list", spec.key);
      list.setAttribute("role", "list");
      list.setAttribute("aria-labelledby", heading.id);
      const empty = document.createElement("p");
      empty.className = board ? "wasla-live-board__empty" : (focus ? "wasla-live-focus__empty" : "wasla-live-group__empty");
      empty.setAttribute("data-board-empty", "");
      empty.textContent = message("boardEmpty");
      column.appendChild(heading);
      column.appendChild(list);
      column.appendChild(empty);
      root.appendChild(column);
    });
    return root;
  }

  function currentLayout() {
    const view = O.liveView && O.liveView.getView();
    if (view === "list" || view === "focus") return view;
    return "board";
  }

  function isNarrow() {
    if (narrowMedia) return !!narrowMedia.matches;
    if (!global.matchMedia) return false;
    try {
      return !!global.matchMedia("(max-width: 767.98px)").matches;
    } catch (e) {
      return false;
    }
  }

  function findOrder(id) {
    const orders = renderedSnapshot && renderedSnapshot.orders ? renderedSnapshot.orders : [];
    for (let i = 0; i < orders.length; i++) {
      if (String(orders[i].id) === String(id)) return orders[i];
    }
    return null;
  }

  function detailNode() {
    const host = document.getElementById(hostId);
    return host ? host.querySelector("[data-focus-detail]") : null;
  }

  function productSummary(items) {
    const list = items || [];
    if (!list.length) return "";
    const max = 2;
    const parts = [];
    const count = Math.min(list.length, max);
    for (let i = 0; i < count; i++) {
      parts.push(String(list[i].quantity) + "× " + (list[i].productName || ""));
    }
    if (list.length > max) {
      const extra = message("itemsMore");
      parts.push(extra.indexOf("{0}") >= 0 ? extra.replace("{0}", String(list.length - max)) : extra);
    }
    return parts.join(", ");
  }

  function queueSignature(order) {
    const items = order.items || [];
    const itemPart = items.map(function (item) {
      return String(item.quantity) + "\u001f" + (item.productName || "");
    }).join("\u001e");
    return [order.status || "", order.displayNumber || "", order.platform || "", itemPart].join("\u001d");
  }

  function createBrandedEmpty(kind) {
    const empty = document.createElement("div");
    empty.className = "wasla-live-empty";
    empty.setAttribute("data-live-empty", kind);
    empty.setAttribute("role", "status");
    const mark = document.createElement("div");
    mark.className = "wasla-live-empty__mark";
    mark.setAttribute("aria-hidden", "true");
    mark.textContent = "W";
    const title = document.createElement("h2");
    title.className = "wasla-live-empty__title";
    const description = document.createElement("p");
    description.className = "wasla-live-empty__text";
    if (kind === "selectOrder") {
      title.textContent = message("selectOrder");
      description.textContent = message("selectOrderDescription");
    } else {
      title.textContent = message("noDisplayableOrders");
      description.textContent = message("noDisplayableOrdersDescription");
    }
    empty.appendChild(mark);
    empty.appendChild(title);
    empty.appendChild(description);
    return empty;
  }

  function removeLivePresentations(host) {
    releaseBoardOverflow(host);
    const loading = host.querySelector("[data-live-loading]");
    if (loading) loading.remove();
    Array.from(host.querySelectorAll(".wasla-live-board")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".wasla-live-groups")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".wasla-live-focus")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".orders-card-grid")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".wasla-orders-empty")).forEach(function (el) { el.remove(); });
  }

  function hostEmpty(host) {
    const nodes = host.querySelectorAll(".wasla-live-empty");
    for (let i = 0; i < nodes.length; i++) {
      if (nodes[i].parentElement === host) return nodes[i];
    }
    return null;
  }

  function showAcceptedEmpty(host) {
    removeLivePresentations(host);
    const existing = hostEmpty(host);
    if (existing && existing.getAttribute("data-live-empty") === "none") return existing;
    if (existing) existing.remove();
    const empty = createBrandedEmpty("none");
    host.appendChild(empty);
    return empty;
  }

  function createFocusShell() {
    const root = document.createElement("div");
    root.className = "wasla-live-focus";
    const queue = document.createElement("div");
    queue.className = "wasla-live-focus__queue";
    const queueHeading = document.createElement("h2");
    queueHeading.id = "ordersLiveFocusQueueHeading";
    queueHeading.className = "visually-hidden";
    queueHeading.textContent = message("focusQueue");
    queue.setAttribute("role", "region");
    queue.setAttribute("aria-labelledby", queueHeading.id);
    queue.appendChild(queueHeading);
    queue.appendChild(createGroupedLayout("focus"));
    const panel = document.createElement("div");
    panel.className = "wasla-live-focus__panel";
    const panelHeading = document.createElement("h2");
    panelHeading.id = "ordersLiveFocusPanelHeading";
    panelHeading.className = "visually-hidden";
    panelHeading.textContent = message("viewDetails");
    panel.setAttribute("role", "region");
    panel.setAttribute("aria-labelledby", panelHeading.id);
    const back = document.createElement("button");
    back.className = "btn btn-link btn-sm px-0 wasla-live-focus__back";
    back.setAttribute("type", "button");
    back.setAttribute("data-focus-back", "");
    back.textContent = message("backToQueue");
    const detail = document.createElement("div");
    detail.id = "ordersLiveFocusDetail";
    detail.className = "wasla-live-focus__detail";
    detail.setAttribute("data-focus-detail", "");
    panel.appendChild(panelHeading);
    panel.appendChild(back);
    panel.appendChild(detail);
    root.appendChild(queue);
    root.appendChild(panel);
    return root;
  }

  function createFocusEntry(order, serverTimeUtc) {
    const button = document.createElement("button");
    button.className = "wasla-live-focus-entry";
    button.setAttribute("type", "button");
    button.setAttribute("data-focus-select", "");
    button.setAttribute("data-order-id", String(order.id));
    button.setAttribute("data-order-status", order.status || "");
    button.setAttribute("data-received-at", order.receivedAtUtc || "");
    button.setAttribute("aria-pressed", "false");
    if (order.isDemo) button.setAttribute("data-wasla-demo", "true");

    const code = document.createElement("span");
    code.className = "wasla-live-focus-entry__code";
    code.setAttribute("data-order-code", "");
    code.textContent = order.displayNumber || "";

    const meta = document.createElement("span");
    meta.className = "wasla-live-focus-entry__meta";
    const platformHost = document.createElement("span");
    platformHost.setAttribute("data-platform-host", "");
    platformHost.appendChild(createPlatformBadge(order.platform, order));
    const status = document.createElement("span");
    status.className = statusClass(order.status);
    status.setAttribute("data-status-badge", "");
    status.textContent = statusLabel(order.status);
    const elapsed = document.createElement("span");
    elapsed.className = "wasla-live-focus-entry__elapsed";
    elapsed.setAttribute("data-elapsed", "");
    elapsed.setAttribute("data-tour", "live-timer");
    elapsed.textContent = formatElapsed(serverTimeUtc, order.receivedAtUtc);
    meta.appendChild(platformHost);
    meta.appendChild(status);
    meta.appendChild(elapsed);

    const summary = document.createElement("span");
    summary.className = "wasla-live-focus-entry__summary";
    summary.setAttribute("data-product-summary", "");
    summary.textContent = productSummary(order.items);

    button.appendChild(code);
    button.appendChild(meta);
    button.appendChild(summary);
    button.setAttribute("data-live-signature", queueSignature(order));
    button.setAttribute("data-platform", order.platform || "");
    return button;
  }

  function updateFocusEntry(entry, order, serverTimeUtc) {
    const signature = queueSignature(order);
    const elapsed = formatElapsed(serverTimeUtc, order.receivedAtUtc);
    if (entry.getAttribute("data-live-signature") === signature) {
      const elapsedEl = entry.querySelector("[data-elapsed]");
      if (elapsedEl && elapsedEl.textContent !== elapsed) {
        elapsedEl.textContent = elapsed;
        return "time";
      }
      return "same";
    }
    entry.setAttribute("data-order-status", order.status || "");
    entry.setAttribute("data-received-at", order.receivedAtUtc || "");
    const code = entry.querySelector("[data-order-code]");
    if (code) code.textContent = order.displayNumber || "";
    const status = entry.querySelector("[data-status-badge]");
    if (status) {
      status.className = statusClass(order.status);
      status.textContent = statusLabel(order.status);
    }
    if (entry.getAttribute("data-platform") !== (order.platform || "")) {
      const platformHost = entry.querySelector("[data-platform-host]");
      if (platformHost) {
        while (platformHost.firstChild) platformHost.removeChild(platformHost.firstChild);
        platformHost.appendChild(createPlatformBadge(order.platform, order));
      }
      entry.setAttribute("data-platform", order.platform || "");
    }
    const summary = entry.querySelector("[data-product-summary]");
    const summaryText = productSummary(order.items);
    if (summary && summary.textContent !== summaryText) summary.textContent = summaryText;
    const elapsedEl = entry.querySelector("[data-elapsed]");
    if (elapsedEl) elapsedEl.textContent = elapsed;
    entry.setAttribute("data-live-signature", signature);
    return "changed";
  }

  function markPressed() {
    const host = document.getElementById(hostId);
    if (!host) return;
    const entries = host.querySelectorAll("[data-focus-select]");
    for (let i = 0; i < entries.length; i++) {
      const on = entries[i].getAttribute("data-order-id") === selectedOrderId;
      entries[i].setAttribute("aria-pressed", on ? "true" : "false");
      entries[i].classList.toggle("is-selected", on);
    }
  }

  function syncDetailElapsed(order, serverTimeUtc, root) {
    const detail = root || detailNode();
    if (!detail || typeof detail.querySelector !== "function") return;
    const panel = detail.querySelector(".wasla-live-detail");
    if (!panel || panel.getAttribute("data-order-id") !== String(order.id)) return;
    const elapsed = formatElapsed(serverTimeUtc, order.receivedAtUtc);
    const elapsedEl = panel.querySelector("[data-elapsed]");
    if (elapsedEl && elapsedEl.textContent !== elapsed) elapsedEl.textContent = elapsed;
    if (typeof order.totalAmount === "number" && isFinite(order.totalAmount)) {
      const totalEl = panel.querySelector(".wasla-live-detail__grand dd");
      const total = formatMoney(order.totalAmount);
      if (totalEl && totalEl.textContent !== total) totalEl.textContent = total;
    }
  }

  function syncFocusOrderNote(root, order) {
    if (!root || !order || typeof root.querySelector !== "function") return;
    const panel = root.classList && root.classList.contains("wasla-live-detail")
      ? root
      : root.querySelector(".wasla-live-detail");
    if (!panel) return;
    const id = String(order.id);
    const panelId = panel.getAttribute("data-order-id");
    if (panelId && panelId !== id) return;
    if (!panelId) panel.setAttribute("data-order-id", id);
    const note = orderNoteText(order);
    let block = panel.querySelector("[data-order-note]");
    if (!note) {
      if (block) block.remove();
      return;
    }
    if (!block) {
      block = document.createElement("div");
      block.className = "wasla-live-focus-order-note";
      block.setAttribute("data-order-note", "");
      const label = document.createElement("span");
      label.className = "wasla-live-focus-order-note__label";
      label.textContent = message("orderNote");
      const text = document.createElement("p");
      text.className = "wasla-live-focus-order-note__text";
      text.setAttribute("data-order-note-text", "");
      block.appendChild(label);
      block.appendChild(text);
      placeFocusOrderNote(panel, block);
    } else {
      placeFocusOrderNote(panel, block);
    }
    const textEl = block.querySelector("[data-order-note-text]");
    if (textEl && textEl.textContent !== note) textEl.textContent = note;
    if (textEl) textEl.title = note;
  }

  function placeFocusOrderNote(panel, block) {
    const facts = panel.querySelector(".wasla-live-detail__facts");
    const list = facts && facts.querySelector(".wasla-live-detail__fact-list");
    if (facts && list && list.parentElement === facts) {
      if (list.nextElementSibling !== block) facts.insertBefore(block, list.nextSibling);
      return;
    }
    if (facts) {
      if (block.parentElement !== facts) facts.appendChild(block);
      return;
    }
    const body = panel.querySelector(".wasla-live-detail__body");
    const actions = panel.querySelector(".wasla-live-detail__actions");
    if (body) body.appendChild(block);
    else if (actions && actions.parentElement === panel) panel.insertBefore(block, actions);
    else panel.appendChild(block);
  }

  function syncFocusOrderNoteFromSnapshot() {
    if (currentLayout() !== "focus" || !selectedOrderId || !renderedSnapshot) return;
    const order = findOrder(selectedOrderId);
    if (!order) return;
    syncFocusOrderNote(detailNode(), order);
  }

  function syncFocusedDetailPresentation(order, serverTimeUtc, root) {
    syncDetailElapsed(order, serverTimeUtc, root);
    syncFocusOrderNote(root || detailNode(), order);
    try {
      if (O.table && typeof O.table.applyNewOrderVisualState === "function") {
        O.table.applyNewOrderVisualState();
      }
    } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("applyNewOrderVisualState", error);
    }
    try { syncOrderNoteAttention(); } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("orderNoteAttention", error);
    }
  }

  function showDetailMessage(key) {
    const detail = detailNode();
    if (!detail) return;
    const existing = detail.querySelector(".wasla-live-empty");
    if (existing && existing.getAttribute("data-live-empty") === key && !detail.querySelector("[data-order-action]")) return;
    while (detail.firstChild) detail.removeChild(detail.firstChild);
    detail.appendChild(createBrandedEmpty(key));
    detail.setAttribute("aria-busy", "false");
  }

  function prepareDetail(detail, replaceAll) {
    detail.setAttribute("aria-busy", "true");
    if (replaceAll) {
      while (detail.firstChild) detail.removeChild(detail.firstChild);
      const paragraph = document.createElement("p");
      paragraph.className = "text-muted small mb-0";
      paragraph.setAttribute("role", "status");
      paragraph.textContent = message("commonLoading");
      detail.appendChild(paragraph);
      return;
    }
    const actions = detail.querySelector(".wasla-live-detail__actions");
    if (actions) while (actions.firstChild) actions.removeChild(actions.firstChild);
  }

  function abandonDetail() {
    detailGeneration += 1;
    loadingOrderId = null;
    loadingSignature = null;
    loadedOrderId = null;
    loadedSignature = null;
    failedOrderId = null;
    failedSignature = null;
    detailStale = false;
    if (O.liveDetailModal && O.liveDetailModal.cancelPanel) O.liveDetailModal.cancelPanel("focus");
  }

  function ensureDetail(order, force) {
    const id = String(order.id);
    const signature = api.orderContentSignature(order);
    if (!force && !detailStale && loadedOrderId === id && loadedSignature === signature) return;
    if (!force && !detailStale && failedOrderId === id && failedSignature === signature) return;
    if (!force && !detailStale && loadingOrderId === id && loadingSignature === signature) return;
    const detail = detailNode();
    if (detail && order.isDemo) {
      abandonDetail();
      while (detail.firstChild) detail.removeChild(detail.firstChild);
      const card = createCard(order, renderedSnapshot.serverTimeUtc);
      card.classList.add("wasla-demo-focus-card");
      card.setAttribute("role", "group");
      detail.appendChild(card);
      detail.setAttribute("aria-busy", "false");
      loadedOrderId = id;
      loadedSignature = signature;
      return;
    }
    const loader = O.liveDetailModal && O.liveDetailModal.loadPanel;
    if (!detail || !loader) return;
    const replaceAll = loadedOrderId !== id || !detail.querySelector(".wasla-live-detail");
    prepareDetail(detail, replaceAll);
    const generation = ++detailGeneration;
    loadingOrderId = id;
    loadingSignature = signature;
    detailStale = false;
    Promise.resolve(loader(detail, id, "focus")).then(function (ok) {
      if (generation !== detailGeneration) return;
      if (loadingOrderId === id) {
        loadingOrderId = null;
        loadingSignature = null;
      }
      if (selectedOrderId === id) {
        if (ok) {
          loadedOrderId = id;
          loadedSignature = signature;
          failedOrderId = null;
          failedSignature = null;
          const refreshed = findOrder(id);
          if (refreshed && renderedSnapshot) syncFocusedDetailPresentation(refreshed, renderedSnapshot.serverTimeUtc, detail);
        } else {
          loadedOrderId = null;
          loadedSignature = null;
          failedOrderId = id;
          failedSignature = signature;
        }
        detail.setAttribute("aria-busy", "false");
      }
    }, function () {
      if (generation !== detailGeneration) return;
      if (loadingOrderId === id) {
        loadingOrderId = null;
        loadingSignature = null;
      }
    });
  }

  function selectedEntry(host) {
    if (!host || !selectedOrderId) return null;
    const entries = host.querySelectorAll("[data-focus-select]");
    for (let i = 0; i < entries.length; i++) {
      if (entries[i].getAttribute("data-order-id") === selectedOrderId) return entries[i];
    }
    return null;
  }

  function selectOrder(id, fromUser) {
    if (!id || !findOrder(id)) return;
    const next = String(id);
    const host = document.getElementById(hostId);
    const changed = selectedOrderId !== next;
    selectionDismissed = false;
    selectedOrderId = next;
    if (fromUser && host) {
      focusOpener = selectedEntry(host);
      const queue = host.querySelector(".wasla-live-focus__queue");
      if (queue) queueScrollTop = queue.scrollTop || 0;
      if (isNarrow()) narrowDetail = true;
    }
    const root = host && host.querySelector(".wasla-live-focus");
    if (root) root.classList.toggle("wasla-live-focus--show-detail", !!(narrowDetail && isNarrow()));
    markPressed();
    const order = findOrder(next);
    if (order && (changed || loadedOrderId !== next || detailStale)) {
      ensureDetail(order, changed);
      if (changed && O.table && typeof O.table.applyNewOrderVisualState === "function") {
        try { O.table.applyNewOrderVisualState(); } catch (error) {
          if (typeof O.debugWarn === "function") O.debugWarn("applyNewOrderVisualState", error);
        }
      }
    }
  }

  function showQueue() {
    narrowDetail = false;
    const host = document.getElementById(hostId);
    if (!host) return;
    const root = host.querySelector(".wasla-live-focus");
    if (root) root.classList.toggle("wasla-live-focus--show-detail", false);
    const queue = host.querySelector(".wasla-live-focus__queue");
    if (queue) queue.scrollTop = queueScrollTop;
    const target = (focusOpener && focusOpener.parentElement && focusOpener) || selectedEntry(host) || host.querySelector("[data-focus-select]") || host;
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  function retrySelectedDetail() {
    const order = selectedOrderId ? findOrder(selectedOrderId) : null;
    if (!order) return;
    detailStale = true;
    loadedSignature = null;
    ensureDetail(order, true);
  }

  function onOrderAction(ev) {
    if (!focusActive || !selectedOrderId) return;
    const changedId = ev && ev.detail ? ev.detail.orderId : null;
    if (changedId && String(changedId) !== String(selectedOrderId)) return;
    const order = findOrder(selectedOrderId);
    if (!order) return;
    detailStale = true;
    loadedSignature = null;
    ensureDetail(order, true);
  }

  function moveFocusOutOfHidden(root) {
    const active = document.activeElement;
    if (!active || active === document.body || !root.contains(active)) return;
    const panel = root.querySelector(".wasla-live-focus__panel");
    const back = root.querySelector("[data-focus-back]");
    const panelHidden = isNarrow() && !narrowDetail;
    const backHidden = !isNarrow();
    const stuck = (panelHidden && panel && panel.contains(active)) || (backHidden && active === back);
    if (!stuck) return;
    const host = document.getElementById(hostId);
    const target = selectedEntry(host) || root.querySelector("[data-focus-select]") || host;
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  function reconcileNarrowFocus() {
    if (!focusActive) return;
    const host = document.getElementById(hostId);
    const root = host && host.querySelector(".wasla-live-focus");
    if (!root) return;
    root.classList.toggle("wasla-live-focus--show-detail", !!(narrowDetail && isNarrow()));
    moveFocusOutOfHidden(root);
  }

  function bindNarrowWatcher() {
    if (narrowMedia || !global.matchMedia) return;
    try {
      narrowMedia = global.matchMedia("(max-width: 767.98px)");
    } catch (e) {
      narrowMedia = null;
      return;
    }
    const onChange = function () { reconcileNarrowFocus(); };
    if (typeof narrowMedia.addEventListener === "function") narrowMedia.addEventListener("change", onChange);
    else if (typeof narrowMedia.addListener === "function") narrowMedia.addListener(onChange);
  }

  function renderFocusSnapshot(snapshot) {
    const host = document.getElementById(hostId);
    if (!host) return { added: 0, removed: 0, changed: 0, unchanged: 0, timeUpdates: 0, moved: 0 };
    const orders = presentationOrders(snapshot.orders || []);
    const serverTimeUtc = snapshot.serverTimeUtc;
    const entering = !focusActive;
    focusActive = true;
    const focused = document.activeElement === document.body && actionFocus ? actionFocus : document.activeElement;
    actionFocus = null;
    const focusedEntry = focused && focused.closest ? focused.closest("[data-focus-select]") : null;
    const focusedId = focusedEntry && focusedEntry.getAttribute("data-order-id");
    const loading = host.querySelector("[data-live-loading]");
    if (loading) loading.remove();
    releaseBoardOverflow(host);
    Array.from(host.querySelectorAll(".wasla-live-board")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".wasla-live-groups")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".orders-card-grid")).forEach(function (el) { el.remove(); });

    if (!orders.length) {
      focusHadOrders = false;
      abandonDetail();
      selectedOrderId = null;
      selectionDismissed = false;
      showAcceptedEmpty(host);
      if (focusedId && document.activeElement !== focused) host.focus({ preventScroll: true });
      return finishRender({ added: 0, removed: 0, changed: 0, unchanged: 0, timeUpdates: 0, moved: 0 });
    }

    const hadOrders = focusHadOrders;
    focusHadOrders = true;
    const hostLevelEmpty = hostEmpty(host);
    if (hostLevelEmpty) hostLevelEmpty.remove();
    const legacyEmpty = host.querySelector(".wasla-orders-empty");
    if (legacyEmpty) legacyEmpty.remove();
    let root = host.querySelector(".wasla-live-focus");
    if (!root) {
      root = createFocusShell();
      host.appendChild(root);
    }
    if (!selectedOrderId && !selectionDismissed && !isNarrow() && (entering || !hadOrders)) {
      selectedOrderId = String(orders[0].id);
    }
    root.classList.toggle("wasla-live-focus--show-detail", !!(narrowDetail && isNarrow()));

    const lists = {};
    const counts = {};
    const anchors = {};
    boardColumns.forEach(function (spec) {
      lists[spec.key] = root.querySelector('[data-board-list="' + spec.key + '"]');
      counts[spec.key] = 0;
    });
    const byId = new Map();
    Array.from(root.querySelectorAll("[data-focus-select]")).forEach(function (entry) {
      byId.set(entry.getAttribute("data-order-id"), entry);
    });
    const seen = new Set();
    let added = 0;
    let changed = 0;
    let unchanged = 0;
    let timeUpdates = 0;
    let moved = 0;
    orders.forEach(function (order) {
      const id = String(order.id);
      seen.add(id);
      let entry = byId.get(id);
      if (!entry) {
        entry = createFocusEntry(order, serverTimeUtc);
        added += 1;
      } else {
        const result = updateFocusEntry(entry, order, serverTimeUtc);
        if (result === "changed") changed += 1;
        else if (result === "time") timeUpdates += 1;
        else unchanged += 1;
      }
      const key = columnKey(order.status);
      const list = lists[key];
      counts[key] += order.isDemo ? 0 : 1;
      const anchor = anchors[key];
      const expected = anchor ? anchor.nextElementSibling : list.firstElementChild;
      if (entry !== expected) {
        list.insertBefore(entry, expected);
        moved += 1;
      }
      anchors[key] = entry;
      byId.set(id, entry);
    });
    let removed = 0;
    byId.forEach(function (entry, id) {
      if (!seen.has(id)) {
        entry.remove();
        removed += 1;
      }
    });
    boardColumns.forEach(function (spec) {
      const column = root.querySelector('[data-board-column="' + spec.key + '"]');
      const count = column.querySelector("[data-board-count]");
      const text = String(counts[spec.key]);
      if (count.textContent !== text) count.textContent = text;
      var hasCard = !!column.querySelector("[data-order-id]");
      column.querySelector("[data-board-empty]").hidden = counts[spec.key] !== 0 || hasCard;
    });
    markPressed();

    if (selectedOrderId && !seen.has(selectedOrderId)) {
      selectedOrderId = null;
      selectionDismissed = true;
      focusOpener = null;
      abandonDetail();
      showDetailMessage("selectOrder");
    } else if (!selectedOrderId) {
      showDetailMessage("selectOrder");
    } else {
      const order = findOrder(selectedOrderId);
      if (order) {
        ensureDetail(order, false);
        syncDetailElapsed(order, serverTimeUtc);
      }
    }

    if (focusedId && document.activeElement !== focused) {
      const panel = root.querySelector(".wasla-live-focus__panel");
      const focusInPanel = panel && panel.contains(document.activeElement) && document.activeElement !== document.body;
      if (!focusInPanel) {
        const entry = seen.has(focusedId) ? byId.get(focusedId) : null;
        const target = entry || root.querySelector("[data-focus-select]") || host;
        if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
      }
    }
    return finishRender({ added: added, removed: removed, changed: changed, unchanged: unchanged, timeUpdates: timeUpdates, moved: moved });
  }

  function boardItemsOverflow(viewport) {
    const scroll = viewport.scrollHeight;
    const client = viewport.clientHeight;
    if (typeof scroll !== "number" || typeof client !== "number" || client <= 0) return null;
    return scroll - client > 1;
  }

  function syncBoardItemOverflow(card) {
    if (!card || card.getAttribute("data-live-layout") !== "board") return;
    const shell = card.querySelector(".wasla-live-board-items");
    const viewport = shell && shell.querySelector(".wasla-live-board-items__viewport");
    if (!shell || !viewport) return;
    const overflow = boardItemsOverflow(viewport);
    if (overflow === null) return;
    shell.classList.toggle("has-item-overflow", overflow);
    const details = card.querySelector(".wasla-live-screen-card__details");
    if (details) details.classList.toggle("has-item-overflow", overflow);
  }

  function watchBoardCardOverflow(card) {
    if (!card) return;
    if (global.IntersectionObserver) {
      if (!boardOverflowObserver) {
        boardOverflowObserver = new global.IntersectionObserver(function (entries) {
          for (let i = 0; i < entries.length; i++) {
            if (entries[i].isIntersecting) syncBoardItemOverflow(entries[i].target);
          }
        });
      }
      boardOverflowObserver.observe(card);
    }
    // A Board card keeps one height, so its product preview gets whatever room is left: a note, wrapped actions or the
    // practice-order countdown change that room without changing the card. The "more in details" line follows the
    // preview's own size, re-checked on the next frame.
    const viewport = card.querySelector(".wasla-live-board-items__viewport");
    if (!global.ResizeObserver || !viewport) return;
    if (!boardItemsResizeObserver) {
      boardItemsResizeObserver = new global.ResizeObserver(function (entries) {
        const first = !boardItemsResizePending;
        if (first) boardItemsResizePending = new Set();
        for (let i = 0; i < entries.length; i++) {
          const owner = entries[i].target && entries[i].target.closest ? entries[i].target.closest(".wasla-live-screen-card") : null;
          if (owner) boardItemsResizePending.add(owner);
        }
        if (!first) return;
        const nextFrame = global.requestAnimationFrame || function (fn) { return global.setTimeout(fn, 16); };
        nextFrame(function () {
          const cards = boardItemsResizePending;
          boardItemsResizePending = null;
          if (cards) cards.forEach(function (owner) { if (owner.isConnected !== false) syncBoardItemOverflow(owner); });
        });
      });
    }
    boardItemsResizeObserver.observe(viewport);
  }

  function unwatchBoardCard(card) {
    if (!card) return;
    if (boardOverflowObserver) boardOverflowObserver.unobserve(card);
    const viewport = boardItemsResizeObserver && card.querySelector ? card.querySelector(".wasla-live-board-items__viewport") : null;
    if (viewport) boardItemsResizeObserver.unobserve(viewport);
  }

  function releaseBoardOverflow(root) {
    if ((!boardOverflowObserver && !boardItemsResizeObserver) || !root || typeof root.querySelectorAll !== "function") return;
    const cards = root.querySelectorAll(".wasla-live-screen-card");
    for (let i = 0; i < cards.length; i++) unwatchBoardCard(cards[i]);
  }

  function syncBoardOverflow(host) {
    if (!host) return;
    const cards = host.querySelectorAll('.wasla-live-screen-card[data-live-layout="board"]');
    for (let i = 0; i < cards.length; i++) {
      syncBoardItemOverflow(cards[i]);
      watchBoardCardOverflow(cards[i]);
    }
  }

  function bindBoardOverflowResize() {
    if (boardOverflowResizeBound || typeof global.addEventListener !== "function") return;
    boardOverflowResizeBound = true;
    let timer = 0;
    global.addEventListener("resize", function () {
      if (currentLayout() !== "board") return;
      if (timer) global.clearTimeout(timer);
      timer = global.setTimeout(function () {
        timer = 0;
        syncBoardOverflow(document.getElementById(hostId));
      }, 80);
    });
  }

  function renderSnapshot(snapshot) {
    const host = document.getElementById(hostId);
    if (!host) return { added: 0, removed: 0, changed: 0, unchanged: 0 };
    renderedSnapshot = snapshot;
    const layout = currentLayout();
    if (layout === "focus") return renderFocusSnapshot(snapshot);
    if (focusActive) {
      abandonDetail();
      focusActive = false;
      narrowDetail = false;
    }
    const focusShell = host.querySelector(".wasla-live-focus");
    if (focusShell) focusShell.remove();
    const orders = presentationOrders(snapshot.orders || []);
    const serverTimeUtc = snapshot.serverTimeUtc;
    const focused = document.activeElement === document.body && actionFocus ? actionFocus : document.activeElement;
    actionFocus = null;
    const focusedCard = focused && focused.closest ? focused.closest(".wasla-live-screen-card") : null;
    const focusedId = focusedCard && focusedCard.getAttribute("data-order-id");
    if (!orders.length) {
      showAcceptedEmpty(host);
      if (focusedId && document.activeElement !== focused) host.focus({ preventScroll: true });
      return finishRender({ added: 0, removed: 0, changed: 0, unchanged: 0, timeUpdates: 0, moved: 0 });
    }
    const loading = host.querySelector("[data-live-loading]");
    if (loading) loading.remove();
    const hostLevelEmpty = hostEmpty(host);
    if (hostLevelEmpty) hostLevelEmpty.remove();
    const legacyEmpty = host.querySelector(".wasla-orders-empty");
    if (legacyEmpty) legacyEmpty.remove();
    let grid = host.querySelector(layout === "board" ? ".wasla-live-board" : ".wasla-live-groups");
    if (!grid) {
      grid = createGroupedLayout(layout);
      host.appendChild(grid);
    }
    const cards = Array.from(host.querySelectorAll(".wasla-live-screen-card"));
    const lists = {};
    const counts = {};
    const anchors = {};
    boardColumns.forEach(function (spec) {
      lists[spec.key] = grid.querySelector('[data-board-list="' + spec.key + '"]');
      counts[spec.key] = 0;
    });
    const byId = new Map();
    cards.forEach(function (card) { byId.set(card.getAttribute("data-order-id"), card); });
    const seen = new Set();
    let added = 0;
    let changed = 0;
    let unchanged = 0;
    let timeUpdates = 0;
    let moved = 0;
    function createEntry(order) {
      return layout === "board" ? createCard(order, serverTimeUtc) : createListRow(order, serverTimeUtc);
    }
    orders.forEach(function (order) {
      const id = String(order.id);
      seen.add(id);
      let card = byId.get(id);
      if (!card) {
        card = createEntry(order);
        added += 1;
      } else if (card.getAttribute("data-live-layout") !== layout || !card.getAttribute("data-live-signature")) {
        unwatchBoardCard(card);
        const replacement = createEntry(order);
        card.replaceWith(replacement);
        card = replacement;
        changed += 1;
      } else if (card.getAttribute("data-live-signature") === api.orderContentSignature(order)) {
        if (syncTimeLabels(card, order, serverTimeUtc)) timeUpdates += 1;
        unchanged += 1;
      } else if (updateCard(card, order, serverTimeUtc)) {
        changed += 1;
      } else {
        unchanged += 1;
      }
      const key = columnKey(order.status);
      const list = lists[key];
      counts[key] += order.isDemo ? 0 : 1;
      const anchor = anchors[key];
      const expected = anchor ? anchor.nextElementSibling : list.firstElementChild;
      if (card !== expected) {
        list.insertBefore(card, expected);
        moved += 1;
      }
      anchors[key] = card;
      byId.set(id, card);
    });
    let removed = 0;
    byId.forEach(function (card, id) {
      if (!seen.has(id)) {
        unwatchBoardCard(card);
        card.remove();
        removed += 1;
      }
    });
    Array.from(host.querySelectorAll(layout === "board" ? ".wasla-live-groups" : ".wasla-live-board")).forEach(function (el) { el.remove(); });
    Array.from(host.querySelectorAll(".orders-card-grid")).forEach(function (el) { el.remove(); });
    boardColumns.forEach(function (spec) {
      const column = grid.querySelector('[data-board-column="' + spec.key + '"]');
      const count = column.querySelector("[data-board-count]");
      const text = String(counts[spec.key]);
      if (count.textContent !== text) count.textContent = text;
      var hasCard = !!column.querySelector("[data-order-id]");
      column.querySelector("[data-board-empty]").hidden = counts[spec.key] !== 0 || hasCard;
    });
    if (layout === "board") syncBoardOverflow(host);
    // Reparenting or replacing an action button may blur it. Never steal focus
    // from a modal or a header control that was active when reconciliation began.
    if (focusedId && document.activeElement !== focused) {
      const card = seen.has(focusedId) ? byId.get(focusedId) : null;
      const target = card && (card.contains(focused) ? focused :
        card.querySelector("[data-order-action]") || card.querySelector("[data-order-detail]"));
      (target || host).focus({ preventScroll: true });
    }
    return finishRender({ added: added, removed: removed, changed: changed, unchanged: unchanged, timeUpdates: timeUpdates, moved: moved });
  }

  async function fetchSnapshot(signal) {
    const started = (global.performance && performance.now) ? performance.now() : Date.now();
    const response = await fetch(O.opts.liveDataUrl || "/orders/live-data", {
      headers: { "Accept": "application/json", "X-Requested-With": "XMLHttpRequest" },
      signal: signal,
      cache: "no-store"
    });
    const text = await response.text();
    const parseStarted = (global.performance && performance.now) ? performance.now() : Date.now();
    const classified = api.classifyResponse(response, text, JSON.parse);
    const parseMs = ((global.performance && performance.now) ? performance.now() : Date.now()) - parseStarted;
    if (classified.kind === "ok") {
      classified.payloadBytes = text.length;
      classified.parseMs = parseMs;
      classified.fetchMs = parseStarted - started;
    }
    return classified;
  }

  async function onAccepted(snapshot, meta) {
    const renderStarted = (global.performance && performance.now) ? performance.now() : Date.now();
    let diff;
    try {
      diff = renderSnapshot(snapshot);
    } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("renderSnapshot", error);
      return { accepted: false };
    }
    const renderMs = ((global.performance && performance.now) ? performance.now() : Date.now()) - renderStarted;
    lastTimings.payloadBytes = meta.payloadBytes || 0;
    lastTimings.parseMs = meta.parseMs || 0;
    lastTimings.renderMs = renderMs;
    lastTimings.orderCount = snapshot.orders.length;
    lastTimings.changedCards = diff.changed;
    lastTimings.addedCards = diff.added;
    lastTimings.removedCards = diff.removed;
    lastTimings.unchangedCards = diff.unchanged;
    lastTimings.timeUpdates = diff.timeUpdates || 0;
    setSummary(snapshot);
    touchUpdated();
    // Listeners such as order training, the automation status and the practice-order countdown read the accepted
    // snapshot instead of scraping one view's DOM. Neither the automation section nor the practice order's countdown
    // deadline takes part in new-order detection; serverTimeUtc lets the countdown measure the server's deadline.
    document.dispatchEvent(new CustomEvent("wasla:live-rendered", {
      detail: {
        orders: snapshot.orders,
        training: snapshot.training || null,
        automation: snapshot.automation || null,
        serverTimeUtc: snapshot.serverTimeUtc
      }
    }));
  }

  async function onNotify(snapshot, meta) {
    if (meta.isBaseline || !meta.newIds || !meta.newIds.length) return;

    try {
      if (O.table && typeof O.table.markOrdersAsRecentlyNew === "function") {
        O.table.markOrdersAsRecentlyNew(meta.newIds);
        O.table.applyNewOrderVisualState();
        O.table.scheduleNewOrderHighlightCleanup();
      }
      document.dispatchEvent(new CustomEvent("wasla:real-order-arrived", {
        detail: { orderId: String(meta.newIds[0]) }
      }));

      const settings = O.state && O.state.notificationSettings;
      if (settings && settings.newOrderSoundEnabled && O.audio && typeof O.audio.playSoundNow === "function") {
        try {
          await O.audio.playSoundNow({
            newOrderSoundEnabled: true,
            newOrderSoundName: settings.newOrderSoundName,
            newOrderSoundRepeatCount: settings.newOrderSoundRepeatCount,
            newOrderSoundVolumePercent: settings.newOrderSoundVolumePercent != null
              ? settings.newOrderSoundVolumePercent
              : Math.round((settings.newOrderSoundVolume || 1) * 100),
            showBrowserNotification: settings.showBrowserNotification
          });
        } catch (error) {
          if (typeof O.debugWarn === "function") O.debugWarn("playSoundNow", error);
        }
      }
      if (O.audio && typeof O.audio.showBrowserNotificationIfAllowed === "function") {
        O.audio.showBrowserNotificationIfAllowed();
      }
    } catch (error) {
      if (typeof O.debugWarn === "function") O.debugWarn("liveNotification", error);
    }
  }

  function ensureCoordinator() {
    if (coordinator) return coordinator;
    coordinator = api.createRefreshCoordinator({
      intervalMs: O.opts.pollingIntervalMs || 10000,
      timeoutMs: O.opts.liveDataTimeoutMs || 15000,
      setTimeout: function (fn, ms) { return global.setTimeout(fn, ms); },
      clearTimeout: function (id) { global.clearTimeout(id); },
      createAbortController: function () { return new AbortController(); },
      fetchSnapshot: fetchSnapshot,
      onStatus: onStatus,
      onAccepted: onAccepted,
      onNotify: onNotify
    });
    return coordinator;
  }

  if (typeof document.addEventListener === "function") {
    document.addEventListener("wasla:order-action-completed", onOrderAction);
  }
  bindNarrowWatcher();
  bindBoardOverflowResize();
  if (O.table) {
    O.table.onRowHighlightEnded = function (orderId) {
      try {
        beginOrderNoteAttention(orderId);
      } catch (error) {
        if (typeof O.debugWarn === "function") O.debugWarn("orderNoteAttention", error);
      }
    };
  }

  return {
    start: function () { return ensureCoordinator().start(); },
    beginMutation: function (origin) {
      if (origin && document.activeElement === origin) actionFocus = origin;
      return ensureCoordinator().beginMutation();
    },
    requestRefresh: function () { return ensureCoordinator().requestRefresh(); },
    refreshView: function () {
      const snapshot = coordinator && coordinator.lastSnapshot();
      if (snapshot) renderSnapshot(snapshot);
    },
    selectOrder: selectOrder,
    showQueue: showQueue,
    retrySelectedDetail: retrySelectedDetail,
    getSelectedOrderId: function () { return selectedOrderId; },
    lastTimings: lastTimings,
    renderSnapshot: renderSnapshot,
    orderNoteAttentionState: orderNoteAttentionState
  };
}
