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
      order.totalAmount,
      order.receivedAtUtc || "",
      order.deliveredAtUtc || "",
      itemPart
    ].join("\u001d");
  }

  function formatAmount(amount, culture) {
    const value = typeof amount === "number" ? amount : Number(amount);
    if (!isFinite(value)) return "";
    return new Intl.NumberFormat(culture || "tr-TR", {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(value);
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
      if (!known.has(id)) fresh.push(id);
    }
    return fresh;
  }

  function rememberOrderIds(known, orders) {
    for (let i = 0; i < orders.length; i++) known.add(String(orders[i].id));
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

          const isBaseline = !baselineReady;
          const newIds = collectNewIds(known, result.snapshot.orders, baselineReady);
          const meta = {
            newIds: newIds,
            isBaseline: isBaseline,
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
    elapsedMinutes: elapsedMinutes,
    collectNewIds: collectNewIds,
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

  function formatElapsed(serverTimeUtc, receivedAtUtc) {
    const template = message("elapsedMinutes");
    const minutes = api.elapsedMinutes(serverTimeUtc, receivedAtUtc);
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
    if (status === "Preparing" || status === "ReadyForPickup" || status === "OnTheWay") return "wasla-dash-status wasla-dash-status--progress";
    if (status === "Delivered") return "wasla-dash-status wasla-dash-status--delivered";
    if (status === "Cancelled") return "wasla-dash-status wasla-dash-status--cancelled";
    if (status === "Failed") return "wasla-dash-status wasla-dash-status--failed";
    return "wasla-dash-status";
  }

  function statusLabel(status) {
    return message("status" + status) || status;
  }

  function actionButtons(status) {
    if (!O.opts.canManageOrders) return [];
    if (status === "New") {
      return [
        { action: "approve", className: "btn btn-success btn-lg", label: message("ordersApprove") },
        { action: "reject", className: "btn btn-outline-danger btn-lg", label: message("ordersReject") }
      ];
    }
    if (status === "Accepted") return [{ action: "start-preparing", className: "btn btn-primary btn-lg", label: message("ordersStartPreparing") }];
    if (status === "Preparing") return [{ action: "mark-ready", className: "btn btn-primary btn-lg", label: message("ordersMarkReady") }];
    if (status === "ReadyForPickup") return [{ action: "hand-to-courier", className: "btn btn-primary btn-lg", label: message("ordersHandToCourier") }];
    if (status === "OnTheWay") return [{ action: "mark-delivered", className: "btn btn-primary btn-lg", label: message("ordersMarkDelivered") }];
    return [];
  }

  function fillActions(container, order) {
    while (container.firstChild) container.removeChild(container.firstChild);
    const buttons = actionButtons(order.status);
    if (!buttons.length) {
      container.hidden = true;
      return;
    }
    container.hidden = false;
    buttons.forEach(function (spec) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = spec.className;
      button.setAttribute("data-order-action", spec.action);
      button.setAttribute("data-order-id", String(order.id));
      button.textContent = spec.label;
      container.appendChild(button);
    });
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

    const imageWrap = document.createElement("div");
    imageWrap.className = "orders-card-image orders-card-image--kitchen";
    const image = document.createElement("img");
    image.src = api.demoImageUrl(order);
    image.alt = (order.items && order.items[0] && order.items[0].productName) || order.customerName || "";
    image.loading = "lazy";
    imageWrap.appendChild(image);

    const body = document.createElement("div");
    body.className = "orders-card-body";

    const top = document.createElement("div");
    top.className = "orders-card-top";
    const badges = document.createElement("div");
    badges.className = "orders-card-badges";
    const platformHost = document.createElement("div");
    platformHost.className = "orders-card-platform";
    platformHost.appendChild(createPlatformBadge(order.platform));
    badges.appendChild(platformHost);
    const status = document.createElement("span");
    status.className = statusClass(order.status);
    status.setAttribute("data-status-badge", "");
    status.textContent = statusLabel(order.status);
    badges.appendChild(status);
    const fresh = document.createElement("span");
    fresh.className = "badge text-bg-primary d-none wasla-orders-new-badge";
    fresh.setAttribute("data-new-badge", "");
    fresh.textContent = message("statusNewBadge");
    badges.appendChild(fresh);
    top.appendChild(badges);

    const code = document.createElement("div");
    code.className = "orders-card-code wasla-live-screen-card__code";
    const link = document.createElement("a");
    link.href = "/orders/details/" + encodeURIComponent(String(order.id));
    link.textContent = order.displayNumber || "";
    code.appendChild(link);

    const customer = document.createElement("div");
    customer.className = "orders-card-customer";
    const customerLabel = document.createElement("span");
    customerLabel.className = "orders-card-meta__label";
    customerLabel.textContent = message("ordersFullscreenCustomer") + ":";
    customer.appendChild(customerLabel);
    customer.appendChild(document.createTextNode(" "));
    const customerValue = document.createElement("span");
    customerValue.setAttribute("data-customer-name", "");
    customerValue.textContent = order.customerName || "";
    customer.appendChild(customerValue);

    const items = document.createElement("ul");
    items.className = "wasla-live-screen-card__items list-unstyled mb-2";
    fillItems(items, order.items || []);

    const meta = document.createElement("div");
    meta.className = "orders-card-meta orders-card-meta--kitchen";
    meta.appendChild(metaRow(message("ordersFullscreenTotal"), formatMoney(order.totalAmount), "data-total"));
    const receivedRow = metaRow(message("ordersFullscreenReceived"), formatReceived(order.receivedAtUtc, serverTimeUtc), "data-received");
    const elapsed = document.createElement("span");
    elapsed.className = "wasla-live-screen-card__elapsed text-muted";
    elapsed.setAttribute("data-elapsed", "");
    elapsed.textContent = " · " + formatElapsed(serverTimeUtc, order.receivedAtUtc);
    receivedRow.appendChild(elapsed);
    meta.appendChild(receivedRow);

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
    actions.appendChild(actionGroup);
    actions.appendChild(details);

    body.appendChild(top);
    body.appendChild(code);
    body.appendChild(customer);
    body.appendChild(items);
    body.appendChild(meta);
    body.appendChild(actions);
    article.appendChild(imageWrap);
    article.appendChild(body);
    article.setAttribute("data-live-signature", api.orderContentSignature(order));
    return article;
  }

  function createPlatformBadge(platform) {
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
      if (item.notes && String(item.notes).trim()) {
        const note = document.createElement("div");
        note.className = "wasla-live-screen-card__note text-muted";
        note.textContent = item.notes;
        li.appendChild(note);
      }
      list.appendChild(li);
    });
    list.hidden = items.length === 0;
  }

  function syncTimeLabels(card, order, serverTimeUtc) {
    const elapsed = " · " + formatElapsed(serverTimeUtc, order.receivedAtUtc);
    const received = " " + formatReceived(order.receivedAtUtc, serverTimeUtc);
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
      image.alt = (order.items && order.items[0] && order.items[0].productName) || order.customerName || "";
    }
    const status = card.querySelector("[data-status-badge]");
    if (status) {
      status.className = statusClass(order.status);
      status.textContent = statusLabel(order.status);
    }
    const link = card.querySelector(".wasla-live-screen-card__code a");
    if (link) link.textContent = order.displayNumber || "";
    setText(card.querySelector("[data-customer-name]"), order.customerName || "");
    const items = card.querySelector(".wasla-live-screen-card__items");
    if (items) fillItems(items, order.items || []);
    setText(card.querySelector("[data-total]"), " " + formatMoney(order.totalAmount));
    setText(card.querySelector("[data-received]"), " " + formatReceived(order.receivedAtUtc, serverTimeUtc));
    setText(card.querySelector("[data-elapsed]"), " · " + formatElapsed(serverTimeUtc, order.receivedAtUtc));
    if (statusChanged) {
      const actions = card.querySelector("[data-order-actions]");
      if (actions) fillActions(actions, order);
    }
    card.setAttribute("data-live-signature", signature);
    return true;
  }

  function ensureEmpty(host) {
    let empty = host.querySelector(".wasla-orders-empty");
    if (!empty) {
      empty = document.createElement("div");
      empty.className = "wasla-orders-empty text-center py-5";
      empty.setAttribute("role", "status");
      const title = document.createElement("div");
      title.className = "wasla-orders-empty__title fw-semibold mb-1";
      title.textContent = message("noLiveOrders");
      const desc = document.createElement("div");
      desc.className = "wasla-orders-empty__desc text-muted";
      desc.textContent = message("noLiveOrdersDescription");
      empty.appendChild(title);
      empty.appendChild(desc);
      host.appendChild(empty);
    }
    return empty;
  }

  function renderSnapshot(snapshot) {
    const host = document.getElementById(hostId);
    if (!host) return { added: 0, removed: 0, changed: 0, unchanged: 0 };
    const orders = snapshot.orders || [];
    const serverTimeUtc = snapshot.serverTimeUtc;
    let grid = host.querySelector(".orders-card-grid");
    if (!orders.length) {
      if (grid) grid.remove();
      ensureEmpty(host);
      return { added: 0, removed: 0, changed: 0, unchanged: 0 };
    }
    const empty = host.querySelector(".wasla-orders-empty");
    if (empty) empty.remove();
    if (!grid) {
      grid = document.createElement("div");
      grid.className = "orders-card-grid wasla-live-screen-grid";
      grid.setAttribute("role", "list");
      host.appendChild(grid);
    }

    const cards = [];
    for (let i = 0; i < grid.children.length; i++) {
      const child = grid.children[i];
      if (child.getAttribute && child.getAttribute("data-order-id")) cards.push(child);
    }

    if (cards.length === orders.length) {
      let same = true;
      for (let i = 0; i < orders.length; i++) {
        if (cards[i].getAttribute("data-order-id") !== String(orders[i].id)
          || cards[i].getAttribute("data-live-signature") !== api.orderContentSignature(orders[i])) {
          same = false;
          break;
        }
      }
      if (same) {
        let timeUpdates = 0;
        for (let i = 0; i < orders.length; i++) {
          if (syncTimeLabels(cards[i], orders[i], serverTimeUtc)) timeUpdates += 1;
        }
        return { added: 0, removed: 0, changed: 0, unchanged: orders.length, timeUpdates: timeUpdates };
      }
    }

    const byId = new Map();
    cards.forEach(function (card) { byId.set(card.getAttribute("data-order-id"), card); });
    const seen = new Set();
    let added = 0;
    let changed = 0;
    let unchanged = 0;
    let anchor = null;
    orders.forEach(function (order) {
      const id = String(order.id);
      seen.add(id);
      let card = byId.get(id);
      if (!card) {
        card = createCard(order, serverTimeUtc);
        added += 1;
      } else if (!card.getAttribute("data-live-signature")) {
        const replacement = createCard(order, serverTimeUtc);
        card.replaceWith(replacement);
        card = replacement;
        changed += 1;
      } else if (card.getAttribute("data-live-signature") === api.orderContentSignature(order)) {
        syncTimeLabels(card, order, serverTimeUtc);
        unchanged += 1;
      } else if (updateCard(card, order, serverTimeUtc)) {
        changed += 1;
      } else {
        unchanged += 1;
      }
      const expected = anchor ? anchor.nextElementSibling : grid.firstElementChild;
      if (card !== expected) grid.insertBefore(card, expected);
      anchor = card;
    });
    let removed = 0;
    byId.forEach(function (card, id) {
      if (!seen.has(id)) {
        card.remove();
        removed += 1;
      }
    });
    return { added: added, removed: removed, changed: changed, unchanged: unchanged };
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
  }

  async function onNotify(snapshot, meta) {
    if (meta.isBaseline || !meta.newIds || !meta.newIds.length) return;

    try {
      if (O.table && typeof O.table.markOrdersAsRecentlyNew === "function") {
        O.table.markOrdersAsRecentlyNew(meta.newIds);
        O.table.applyNewOrderVisualState();
        O.table.scheduleNewOrderHighlightCleanup();
      }

      const settings = O.state && O.state.notificationSettings;
      if (settings && settings.newOrderSoundEnabled && O.audio && typeof O.audio.playSoundNow === "function") {
        if (!O.audio.isSoundUnlocked()) {
          if (O.table && !O.table.hintShownForUnlock) {
            O.table.hintShownForUnlock = true;
            O.showMessage(message("soundUnlockHint"), "info");
          }
        } else {
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

  return {
    start: function () { return ensureCoordinator().start(); },
    beginMutation: function () { return ensureCoordinator().beginMutation(); },
    requestRefresh: function () { return ensureCoordinator().requestRefresh(); },
    lastTimings: lastTimings,
    renderSnapshot: renderSnapshot
  };
}
