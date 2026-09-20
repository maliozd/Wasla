// Live Screen order detail: one reusable modal, markup fetched only when opened.
// Lifecycle buttons inside the panel are server-rendered and posted through orders-actions.js,
// so transition rules and authorization stay on the server.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O) return;

  let inFlight = null;
  let openOrderId = null;

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
    if (!el || !global.bootstrap || !global.bootstrap.Modal) return null;
    return global.bootstrap.Modal.getOrCreateInstance(el);
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

    if (inFlight) {
      inFlight.abort();
      inFlight = null;
    }

    renderMessage(O.getMessage("commonLoading"));

    const controller = new AbortController();
    inFlight = controller;

    try {
      const resp = await fetch(buildDetailUrl(orderId), {
        headers: { "X-Requested-With": "XMLHttpRequest" },
        signal: controller.signal
      });

      if (!resp.ok) {
        renderMessage(O.getMessage("liveScreenDetailLoadFailed"));
        if (O.isDebugEnabled()) O.debugWarn("order detail panel failed", resp.status);
        return;
      }

      const html = await resp.text();
      if (controller.signal.aborted) return;
      body.innerHTML = html;
    } catch (error) {
      if (error && error.name === "AbortError") return;
      renderMessage(O.getMessage("liveScreenDetailLoadFailed"));
      if (O.isDebugEnabled()) O.debugWarn("order detail panel exception", error);
    } finally {
      if (inFlight === controller) inFlight = null;
    }
  }

  function openDetail(orderId) {
    const instance = modalInstance();
    if (!instance) return;
    openOrderId = orderId;
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
      openDetail(orderId);
    });

    el.addEventListener("hidden.bs.modal", function () {
      openOrderId = null;
      if (inFlight) {
        inFlight.abort();
        inFlight = null;
      }
      renderMessage(O.getMessage("commonLoading"));
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
    getOpenOrderId: function () { return openOrderId; }
  };

  document.addEventListener("DOMContentLoaded", init);
})(window);
