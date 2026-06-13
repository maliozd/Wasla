(function (global) {
  "use strict";

  const opts = global.orderHubReceiptTemplateOptions || {};
  const O = global.OrderHubOrders;

  const defaultTemplate = function () {
    return {
      showRestaurantName: true,
      showPlatformName: true,
      showReceivedTime: true,
      showCustomerName: true,
      showCustomerPhone: true,
      showDeliveryAddress: true,
      showProductNotes: true,
      showProductOptions: true,
      showSubtotal: true,
      showDiscount: false,
      showDeliveryFee: true,
      showPaymentMethod: false,
      showFooterMessage: true,
      receiptHeaderText: opts.customerDisplayName || "",
      receiptFooterText: opts.defaultFooterText || "Thank you for your order."
    };
  };

  let currentTemplate = defaultTemplate();

  function msg(key, fallback) {
    return (opts.messages && opts.messages[key]) || fallback || key;
  }

  function getToken() {
    const f = document.getElementById("receiptTemplateSettingsForm");
    if (!f) return null;
    const el = f.querySelector("input[name=\"__RequestVerificationToken\"]");
    return el ? el.value : null;
  }

  function showTemplateMessage(text, type) {
    const host = document.getElementById("receiptTemplateMessageHost");
    if (!host) return;
    const cls = type === "danger" ? "alert-danger" : (type === "success" ? "alert-success" : "alert-info");
    host.innerHTML = "<div class=\"alert " + cls + " alert-dismissible fade show py-2 small mb-0\" role=\"alert\">"
      + escapeHtml(text)
      + "<button type=\"button\" class=\"btn-close btn-close-sm\" data-bs-dismiss=\"alert\" aria-label=\"Close\"></button></div>";
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function normalizeSingleLine(value, maxLen) {
    if (!value) return "";
    const normalized = String(value).trim().replace(/\s+/g, " ");
    return normalized.length > maxLen ? normalized.slice(0, maxLen) : normalized;
  }

  function readTemplateFromUi() {
    const next = Object.assign({}, currentTemplate);
    document.querySelectorAll(".oh-receipt-template-toggle").forEach(function (el) {
      const key = el.getAttribute("data-setting");
      if (!key || el.disabled) return;
      next[key] = !!el.checked;
    });

    const headerEl = document.getElementById("receiptHeaderText");
    const footerEl = document.getElementById("receiptFooterText");
    next.receiptHeaderText = headerEl ? normalizeSingleLine(headerEl.value, opts.headerMaxLength || 60) : "";
    next.receiptFooterText = footerEl ? String(footerEl.value || "").trim() : "";
    if (next.receiptFooterText.length > (opts.footerMaxLength || 160)) {
      next.receiptFooterText = next.receiptFooterText.slice(0, opts.footerMaxLength || 160);
    }
    return next;
  }

  function applyTemplateToUi(template) {
    currentTemplate = Object.assign(defaultTemplate(), template || {});
    document.querySelectorAll(".oh-receipt-template-toggle").forEach(function (el) {
      const key = el.getAttribute("data-setting");
      if (!key) return;
      el.checked = !!currentTemplate[key];
    });

    const headerEl = document.getElementById("receiptHeaderText");
    const footerEl = document.getElementById("receiptFooterText");
    if (headerEl) headerEl.value = currentTemplate.receiptHeaderText || "";
    if (footerEl) footerEl.value = currentTemplate.receiptFooterText || "";
    renderPreview(currentTemplate);
  }

  function validateTemplate(template) {
    if ((template.receiptHeaderText || "").length > (opts.headerMaxLength || 60)) {
      return msg("headerTooLong", "Header is too long.");
    }
    if ((template.receiptFooterText || "").length > (opts.footerMaxLength || 160)) {
      return msg("footerTooLong", "Footer is too long.");
    }
    return null;
  }

  async function loadTemplate() {
    const url = opts.templateSettingsUrl || "/settings/receipt-printer/template-settings";
    const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
    if (!resp.ok) throw new Error("HTTP " + resp.status);
    const data = await resp.json();
    applyTemplateToUi(data);
  }

  async function saveTemplate() {
    const next = readTemplateFromUi();
    const validationError = validateTemplate(next);
    if (validationError) {
      showTemplateMessage(validationError, "danger");
      return;
    }

    const url = opts.templateSettingsUrl || "/settings/receipt-printer/template-settings";
    const token = getToken();
    const headers = {
      "Content-Type": "application/json",
      "X-Requested-With": "fetch"
    };
    if (token) headers["RequestVerificationToken"] = token;

    const resp = await fetch(url, {
      method: "POST",
      headers: headers,
      body: JSON.stringify({
        showRestaurantName: next.showRestaurantName,
        showPlatformName: next.showPlatformName,
        showReceivedTime: next.showReceivedTime,
        showCustomerName: next.showCustomerName,
        showCustomerPhone: next.showCustomerPhone,
        showDeliveryAddress: next.showDeliveryAddress,
        showProductNotes: next.showProductNotes,
        showProductOptions: next.showProductOptions,
        showSubtotal: next.showSubtotal,
        showDiscount: false,
        showDeliveryFee: next.showDeliveryFee,
        showPaymentMethod: next.showPaymentMethod,
        showFooterMessage: next.showFooterMessage,
        receiptHeaderText: next.receiptHeaderText || null,
        receiptFooterText: next.receiptFooterText || null
      })
    });

    if (!resp.ok) {
      let message = msg("saveFailed", "Save failed.");
      try {
        const err = await resp.json();
        if (err && err.message) message = err.message;
      } catch (_) { /* ignore */ }
      throw new Error(message);
    }

    const data = await resp.json();
    applyTemplateToUi(data);
    showTemplateMessage(msg("saved", "Saved."), "success");
  }

  function previewLine(label, value) {
    if (!value) return "";
    return "<div class=\"oh-receipt-preview-line\"><span class=\"oh-receipt-preview-label\">" + escapeHtml(label) + "</span><span>" + escapeHtml(value) + "</span></div>";
  }

  function renderPreview(template) {
    const panel = document.getElementById("receiptPreviewPanel");
    if (!panel) return;

    const sample = opts.sample || {};
    const header = template.receiptHeaderText
      || (template.showRestaurantName ? (opts.customerDisplayName || sample.platform) : "");

    let html = "<div class=\"oh-receipt-preview-paper\">";
    html += "<div class=\"oh-receipt-preview-caption small text-muted mb-2\">" + escapeHtml(msg("previewLabel", "Preview")) + "</div>";

    if (header) {
      html += "<div class=\"oh-receipt-preview-header\">" + escapeHtml(header) + "</div>";
      html += "<div class=\"oh-receipt-preview-sep\"></div>";
    }

    if (template.showPlatformName) html += previewLine(msg("platform", "Platform"), sample.platform);
    html += previewLine(msg("order", "Order"), sample.orderCode);
    if (template.showReceivedTime) html += previewLine(msg("received", "Received"), sample.receivedAt);

    const hasCustomer = template.showCustomerName || template.showCustomerPhone || template.showDeliveryAddress;
    if (hasCustomer) html += "<div class=\"oh-receipt-preview-sep\"></div>";
    if (template.showCustomerName) html += previewLine(msg("customer", "Customer"), sample.customerName);
    if (template.showCustomerPhone) html += previewLine(msg("phone", "Phone"), sample.customerPhone);
    if (template.showDeliveryAddress) html += previewLine(msg("address", "Address"), sample.address);

    html += "<div class=\"oh-receipt-preview-sep\"></div>";
    html += "<div class=\"oh-receipt-preview-item\">2x " + escapeHtml(sample.itemName || "Sample item") + "</div>";
    html += previewLine("  Line", "120.00");
    if (template.showProductNotes) html += previewLine(msg("note", "Note"), sample.itemNote);
    if (template.showProductOptions) html += previewLine("  +", sample.itemOption);

    html += "<div class=\"oh-receipt-preview-sep\"></div>";
    if (template.showSubtotal) html += previewLine(msg("subtotal", "Subtotal"), sample.subtotal);
    if (template.showDeliveryFee) html += previewLine(msg("delivery", "Delivery"), sample.deliveryFee);
    html += previewLine(msg("total", "TOTAL"), sample.total);
    if (template.showPaymentMethod) html += previewLine(msg("payment", "Payment"), sample.paymentMethod);

    if (template.showFooterMessage && template.receiptFooterText) {
      html += "<div class=\"oh-receipt-preview-sep\"></div>";
      html += "<div class=\"oh-receipt-preview-footer\">" + escapeHtml(template.receiptFooterText).replace(/\n/g, "<br>") + "</div>";
    }

    html += "</div>";
    panel.innerHTML = html;
  }

  function bind() {
    document.querySelectorAll(".oh-receipt-template-toggle, #receiptHeaderText, #receiptFooterText").forEach(function (el) {
      el.addEventListener("input", function () {
        renderPreview(readTemplateFromUi());
      });
      el.addEventListener("change", function () {
        renderPreview(readTemplateFromUi());
      });
    });

    const saveBtn = document.getElementById("saveReceiptTemplateBtn");
    if (saveBtn) {
      saveBtn.addEventListener("click", function () {
        saveTemplate().catch(function (e) {
          showTemplateMessage(e && e.message ? e.message : msg("saveFailed", "Save failed."), "danger");
        });
      });
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadTemplate().catch(function () {
      applyTemplateToUi(defaultTemplate());
      showTemplateMessage(msg("saveFailed", "Could not load settings."), "danger");
    });
  });
})(window);
