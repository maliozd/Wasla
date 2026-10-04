// Live Screen order detail: one reusable modal, markup fetched only when opened.
// Focus uses the same request helper with its own container and cancellation slot.
// Lifecycle buttons inside the panel are server-rendered and posted through orders-actions.js,
// so transition rules and authorization stay on the server.
(function (root, factory) {
  "use strict";

  const api = factory();
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }

  const O = root && root.WaslaOrders;
  if (!O) return;

  let openOrderId = null;
  let returnFocus = null;
  let detailClient = null;

  function client() {
    if (!detailClient) {
      detailClient = api.createDetailClient({
        fetch: function (url, options) { return fetch(url, options); },
        getMessage: function (key) { return O.getMessage(key); },
        urlFor: buildDetailUrl,
        document: document,
        onLoaded: function (container, consumer) {
          if ((consumer !== "modal" && consumer !== "focus") || typeof O.applyDetailCurrency !== "function") return;
          const culture = (O.opts && O.opts.displayCulture)
            || (document.documentElement && document.documentElement.lang)
            || "tr-TR";
          O.applyDetailCurrency(container, culture);
        },
        failModal: function () { renderMessage(O.getMessage("liveScreenDetailLoadFailed")); },
        onError: function (status) {
          if (O.isDebugEnabled()) O.debugWarn("order detail panel failed", status);
        }
      });
    }
    return detailClient;
  }

  function isLiveDisplayPage() {
    return O.opts.pageMode === "liveDisplay";
  }

  function modalElement() {
    return document.getElementById("ordersLiveDetailModal");
  }

  function modalBody() {
    return document.getElementById("ordersLiveDetailModalBody");
  }

  /** Reuses the single Bootstrap instance bound to the one modal element. */
  function modalInstance() {
    const el = modalElement();
    if (!el || !root.bootstrap || !root.bootstrap.Modal) return null;
    return root.bootstrap.Modal.getOrCreateInstance(el);
  }

  function buildDetailUrl(orderId) {
    const template = O.opts.orderDetailPanelUrl || "/orders/{id}/detail-panel";
    return template.replace("{id}", encodeURIComponent(orderId));
  }

  function renderMessage(message) {
    const body = modalBody();
    if (!body) return;
    body.innerHTML = "<p class=\"text-muted small mb-0\">" + O.escapeHtml(message) + "</p>";
  }

  async function loadDetail(orderId) {
    const body = modalBody();
    if (!body) return;
    renderMessage(O.getMessage("commonLoading"));
    await client().load(body, orderId, "modal");
  }

  function detailButton(orderId) {
    const host = document.getElementById("ordersLiveScreenHost");
    if (!host || !orderId) return null;
    const buttons = host.querySelectorAll("[data-order-detail]");
    for (let i = 0; i < buttons.length; i++) {
      if (buttons[i].getAttribute("data-order-detail") === String(orderId)) return buttons[i];
    }
    return null;
  }

  function anotherModalIsOpen() {
    return document.querySelector(".modal.show:not(#ordersLiveDetailModal)");
  }

  function restoreDetailFocus() {
    const opener = returnFocus;
    returnFocus = null;
    const other = anotherModalIsOpen();
    if (other) {
      if (!other.contains(document.activeElement) && typeof other.focus === "function") {
        other.focus({ preventScroll: true });
      }
      return;
    }
    const host = document.getElementById("ordersLiveScreenHost");
    let target = opener && opener.isConnected ? opener : null;
    if (!target && opener && opener.getAttribute) target = detailButton(opener.getAttribute("data-order-detail"));
    if (!target) target = host;
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  function openDetail(orderId, trigger) {
    const instance = modalInstance();
    if (!instance) return;
    openOrderId = orderId;
    returnFocus = trigger || detailButton(orderId);
    instance.show();
    loadDetail(orderId);
  }

  function init() {
    if (!isLiveDisplayPage()) return;
    const el = modalElement();
    if (!el) return;

    // Delegated once on document: live cards are replaced on every poll, so per-card
    // listeners would accumulate. This single listener survives re-renders.
    document.addEventListener("click", function (ev) {
      const target = ev.target;
      const trigger = target && target.closest ? target.closest("[data-order-detail]") : null;
      if (!trigger) return;
      if (!trigger.closest("#ordersLiveScreenHost")) return;
      const orderId = trigger.getAttribute("data-order-detail");
      if (!orderId) return;
      ev.preventDefault();
      openDetail(orderId, trigger);
    });

    el.addEventListener("hidden.bs.modal", function () {
      openOrderId = null;
      client().cancel("modal");
      renderMessage(O.getMessage("commonLoading"));
      restoreDetailFocus();
    });

    // A lifecycle action completed elsewhere: refresh only the open panel so its
    // buttons match the new server status.
    document.addEventListener("wasla:order-action-completed", function (ev) {
      if (!openOrderId) return;
      const changedId = ev && ev.detail ? ev.detail.orderId : null;
      if (changedId && String(changedId) !== String(openOrderId)) return;
      loadDetail(openOrderId);
    });
  }

  O.liveDetailModal = {
    open: openDetail,
    getOpenOrderId: function () { return openOrderId; },
    loadPanel: function (container, orderId, consumer) { return client().load(container, orderId, consumer); },
    cancelPanel: function (consumer) { client().cancel(consumer); }
  };

  document.addEventListener("DOMContentLoaded", init);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  function createDetailClient(deps) {
    const requests = { modal: null, focus: null };
    let nextId = 0;
    const doc = deps.document;
    const Abort = deps.AbortController || AbortController;

    function begin(container, orderId, consumer) {
      const previous = requests[consumer];
      if (previous) previous.controller.abort();
      const request = {
        id: ++nextId,
        orderId: String(orderId),
        consumer: consumer,
        controller: new Abort(),
        container: container
      };
      requests[consumer] = request;
      return request;
    }

    function isCurrent(request) {
      return requests[request.consumer] === request
        && !!request.container
        && request.container.isConnected !== false;
    }

    function writeFocusFailure(container) {
      while (container.firstChild) container.removeChild(container.firstChild);
      const message = doc.createElement("p");
      message.className = "text-muted small mb-2";
      message.textContent = deps.getMessage("liveScreenDetailLoadFailed");
      const retry = doc.createElement("button");
      retry.type = "button";
      retry.className = "btn btn-sm btn-outline-secondary";
      retry.setAttribute("type", "button");
      retry.setAttribute("data-focus-detail-retry", "");
      retry.textContent = deps.getMessage("detailRetry");
      container.appendChild(message);
      container.appendChild(retry);
    }

    function fail(container, consumer, status) {
      if (!container) return;
      if (consumer === "focus") writeFocusFailure(container);
      else if (deps.failModal) deps.failModal(status);
      if (deps.onError) deps.onError(status);
    }

    async function load(container, orderId, consumer) {
      if (!container || !orderId || (consumer !== "modal" && consumer !== "focus")) return false;
      const request = begin(container, orderId, consumer);
      try {
        const resp = await deps.fetch(deps.urlFor(orderId), {
          headers: { "X-Requested-With": "XMLHttpRequest" },
          signal: request.controller.signal
        });
        if (!isCurrent(request)) return false;
        if (!resp.ok) {
          fail(container, consumer, resp.status);
          return false;
        }
        const html = await resp.text();
        if (!isCurrent(request)) return false;
        container.innerHTML = html;
        if (typeof deps.onLoaded === "function") deps.onLoaded(container, consumer);
        return true;
      } catch (error) {
        if (!isCurrent(request) || (error && error.name === "AbortError")) return false;
        fail(container, consumer, error);
        return false;
      } finally {
        if (requests[request.consumer] === request) requests[request.consumer] = null;
      }
    }

    function cancel(consumer) {
      const request = requests[consumer];
      if (!request) return;
      request.controller.abort();
      requests[consumer] = null;
    }

    return {
      load: load,
      cancel: cancel
    };
  }

  return { createDetailClient: createDetailClient };
});
