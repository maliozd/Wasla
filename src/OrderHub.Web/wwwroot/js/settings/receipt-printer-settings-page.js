(function (global) {
  "use strict";

  const opts = global.orderHubReceiptTemplateOptions || {};
  const O = global.OrderHubOrders;

  const SUPPORTED_LANGUAGES = ["tr", "en", "ar", "ru"];

  function normalizeLanguage(value) {
    const code = String(value || "").trim().toLowerCase();
    if (SUPPORTED_LANGUAGES.indexOf(code) >= 0) return code;
    return opts.defaultReceiptLanguage || "tr";
  }

  function getDefaultFooter(language) {
    const footers = opts.defaultFooters || {};
    const lang = normalizeLanguage(language);
    return footers[lang] || footers.tr || "Thank you for your order.";
  }

  function isKnownDefaultFooter(text) {
    const value = String(text || "").trim();
    if (!value) return true;
    const footers = opts.defaultFooters || {};
    return Object.keys(footers).some(function (key) {
      return String(footers[key] || "").trim() === value;
    });
  }

  function receiptLabel(language, key, fallback) {
    const lang = normalizeLanguage(language);
    const labels = opts.receiptLabels || {};
    const map = labels[lang] || labels.tr || {};
    return map[key] || (labels.tr && labels.tr[key]) || fallback || key;
  }

  function defaultTemplate() {
    const lang = normalizeLanguage(opts.defaultReceiptLanguage);
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
      receiptLanguage: lang,
      receiptHeaderText: "",
      receiptFooterText: getDefaultFooter(lang)
    };
  }

  function resolvePreviewHeader(template) {
    const custom = (template.receiptHeaderText || "").trim();
    if (custom) return custom;
    if (template.showRestaurantName) return (opts.customerDisplayName || "").trim();
    return "";
  }

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

  function showSaveToast(text, type) {
    const message = String(text || "").trim();
    if (!message) return;

    if (global.OrderHubToast) {
      const options = { key: "settings-save", durationMs: 3000 };
      if (type === "success" && global.OrderHubToast.success) {
        global.OrderHubToast.success(message, options);
        return;
      }
      if (global.OrderHubToast.error) {
        global.OrderHubToast.error(message, options);
        return;
      }
    }

    showTemplateMessage(message, type === "success" ? "success" : "danger");
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

  function formatEnabledCount(enabled, total) {
    const pattern = msg("enabledCount", "{0} of {1} enabled");
    return pattern.replace(/\{0\}/g, String(enabled)).replace(/\{1\}/g, String(total));
  }

  function updateAccordionEnabledCounts(template) {
    document.querySelectorAll(".oh-receipt-accordion-item[data-receipt-settings]").forEach(function (item) {
      const raw = item.getAttribute("data-receipt-settings") || "";
      const keys = raw.split(",").map(function (s) { return s.trim(); }).filter(Boolean);
      const enabled = keys.filter(function (key) { return !!template[key]; }).length;
      const countEl = item.querySelector("[data-receipt-enabled-count]");
      if (countEl) {
        countEl.textContent = formatEnabledCount(enabled, keys.length);
      }
    });
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
    const languageEl = document.getElementById("receiptLanguageSelect");
    next.receiptLanguage = languageEl ? normalizeLanguage(languageEl.value) : normalizeLanguage(currentTemplate.receiptLanguage);
    return next;
  }

  function syncReceiptLanguageUi(template) {
    const languageEl = document.getElementById("receiptLanguageSelect");
    if (languageEl) languageEl.value = normalizeLanguage(template.receiptLanguage);

    const noteEl = document.getElementById("receiptArabicPrinterNote");
    if (noteEl) {
      noteEl.classList.toggle("d-none", normalizeLanguage(template.receiptLanguage) !== "ar");
    }
  }

  function onReceiptLanguageChange(previousLang) {
    const languageEl = document.getElementById("receiptLanguageSelect");
    const footerEl = document.getElementById("receiptFooterText");
    if (!languageEl || !footerEl) return;

    const newLang = normalizeLanguage(languageEl.value);
    const currentFooter = String(footerEl.value || "").trim();
    const prevDefault = getDefaultFooter(previousLang);
    const wasDefault = !currentFooter
      || currentFooter === String(prevDefault).trim()
      || isKnownDefaultFooter(currentFooter);

    if (wasDefault) {
      footerEl.value = getDefaultFooter(newLang);
    }
  }

  function syncMessageFieldsState(template) {
    const footerEl = document.getElementById("receiptFooterText");
    const footerWrap = document.getElementById("receiptFooterFieldWrap");
    const enabled = !!template.showFooterMessage;
    if (footerEl) footerEl.disabled = !enabled;
    if (footerWrap) footerWrap.classList.toggle("oh-receipt-message-field--muted", !enabled);
  }

  function applyTemplateToUi(template) {
    const merged = Object.assign(defaultTemplate(), template || {});
    if (template && typeof template.showRestaurantName === "boolean") {
      merged.showRestaurantName = template.showRestaurantName;
    }
    if (template && template.receiptLanguage) {
      merged.receiptLanguage = normalizeLanguage(template.receiptLanguage);
    }
    currentTemplate = merged;
    document.querySelectorAll(".oh-receipt-template-toggle").forEach(function (el) {
      const key = el.getAttribute("data-setting");
      if (!key) return;
      el.checked = !!currentTemplate[key];
    });

    const headerEl = document.getElementById("receiptHeaderText");
    const footerEl = document.getElementById("receiptFooterText");
    if (headerEl) headerEl.value = currentTemplate.receiptHeaderText || "";
    if (footerEl) footerEl.value = currentTemplate.receiptFooterText || "";
    syncReceiptLanguageUi(currentTemplate);
    syncMessageFieldsState(currentTemplate);
    updateAccordionEnabledCounts(currentTemplate);
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

  function setSaveButtonBusy(busy) {
    const saveBtn = document.getElementById("saveReceiptTemplateBtn");
    if (!saveBtn) return;
    if (busy) {
      if (!saveBtn.dataset.originalHtml) {
        saveBtn.dataset.originalHtml = saveBtn.innerHTML;
      }
      saveBtn.disabled = true;
      saveBtn.innerHTML = "<span class=\"spinner-border spinner-border-sm me-1\" role=\"status\" aria-hidden=\"true\"></span>"
        + escapeHtml(msg("saving", "Saving..."));
      return;
    }
    saveBtn.disabled = false;
    if (saveBtn.dataset.originalHtml) {
      saveBtn.innerHTML = saveBtn.dataset.originalHtml;
    }
  }

  async function saveTemplate() {
    const next = readTemplateFromUi();
    const validationError = validateTemplate(next);
    if (validationError) {
      showSaveToast(validationError, "error");
      return;
    }

    const url = opts.templateSettingsUrl || "/settings/receipt-printer/template-settings";
    const token = getToken();
    const headers = {
      "Content-Type": "application/json",
      "X-Requested-With": "fetch"
    };
    if (token) headers["RequestVerificationToken"] = token;

    setSaveButtonBusy(true);
    try {
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
          receiptLanguage: next.receiptLanguage,
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
      showSaveToast(msg("saved", "Saved."), "success");
    } finally {
      setSaveButtonBusy(false);
    }
  }

  function previewLine(label, value) {
    if (!value) return "";
    return "<div class=\"oh-receipt-preview-line\"><span class=\"oh-receipt-preview-label\">" + escapeHtml(label) + "</span><span>" + escapeHtml(value) + "</span></div>";
  }

  function renderPreview(template) {
    const panel = document.getElementById("receiptPreviewPanel");
    if (!panel) return;

    const sample = opts.sample || {};
    const header = resolvePreviewHeader(template);
    const lang = normalizeLanguage(template.receiptLanguage);
    const L = function (key, fallback) { return receiptLabel(lang, key, fallback); };
    const rtlClass = lang === "ar" ? " oh-receipt-preview-paper--rtl" : "";

    let html = "<div class=\"oh-receipt-preview-paper" + rtlClass + "\"" + (lang === "ar" ? " dir=\"rtl\"" : "") + ">";

    if (header) {
      html += "<div class=\"oh-receipt-preview-header\">" + escapeHtml(header) + "</div>";
      html += "<div class=\"oh-receipt-preview-sep\"></div>";
    }

    if (template.showPlatformName) html += previewLine(L("platform", "Platform"), sample.platform);
    html += previewLine(L("order", "Order"), sample.orderCode);
    if (template.showReceivedTime) html += previewLine(L("received", "Received"), sample.receivedAt);

    const hasCustomer = template.showCustomerName || template.showCustomerPhone || template.showDeliveryAddress;
    if (hasCustomer) html += "<div class=\"oh-receipt-preview-sep\"></div>";
    if (template.showCustomerName) html += previewLine(L("customer", "Customer"), sample.customerName);
    if (template.showCustomerPhone) html += previewLine(L("phone", "Phone"), sample.customerPhone);
    if (template.showDeliveryAddress) html += previewLine(L("address", "Address"), sample.address);

    html += "<div class=\"oh-receipt-preview-sep\"></div>";
    html += "<div class=\"oh-receipt-preview-item\">2x " + escapeHtml(sample.itemName || "Sample item") + "</div>";
    html += previewLine("  " + L("line", "Line"), "120.00");
    if (template.showProductNotes) html += previewLine(L("note", "Note"), sample.itemNote);
    if (template.showProductOptions) html += previewLine("  +", sample.itemOption);

    html += "<div class=\"oh-receipt-preview-sep\"></div>";
    if (template.showSubtotal) html += previewLine(L("subtotal", "Subtotal"), sample.subtotal);
    if (template.showDeliveryFee) html += previewLine(L("delivery", "Delivery"), sample.deliveryFee);
    html += previewLine(L("total", "TOTAL"), sample.total);
    if (template.showPaymentMethod) html += previewLine(L("payment", "Payment"), sample.paymentMethod);

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
        const next = readTemplateFromUi();
        syncMessageFieldsState(next);
        updateAccordionEnabledCounts(next);
        renderPreview(next);
      });
      el.addEventListener("change", function () {
        const next = readTemplateFromUi();
        syncMessageFieldsState(next);
        updateAccordionEnabledCounts(next);
        renderPreview(next);
      });
    });

    const languageEl = document.getElementById("receiptLanguageSelect");
    if (languageEl) {
      languageEl.addEventListener("change", function () {
        const previousLang = normalizeLanguage(currentTemplate.receiptLanguage);
        onReceiptLanguageChange(previousLang);
        const next = readTemplateFromUi();
        currentTemplate = next;
        syncReceiptLanguageUi(next);
        syncMessageFieldsState(next);
        updateAccordionEnabledCounts(next);
        renderPreview(next);
      });
    }

    const saveBtn = document.getElementById("saveReceiptTemplateBtn");
    if (saveBtn) {
      saveBtn.addEventListener("click", function () {
        saveTemplate().catch(function (e) {
          showSaveToast(e && e.message ? e.message : msg("saveFailed", "Save failed."), "error");
        });
      });
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadTemplate().catch(function () {
      applyTemplateToUi(defaultTemplate());
      showSaveToast(msg("saveFailed", "Could not load settings."), "error");
    });
  });
})(window);
