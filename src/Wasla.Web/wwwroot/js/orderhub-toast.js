// OrderHub dependency-free toasts (Tenant UI).
(function (global) {
  "use strict";

  if (global.OrderHubToast) return;

  const DEFAULT_DURATION = { success: 1400, info: 1800, warning: 2200, error: 3000 };
  const MAX_VISIBLE = 3;

  let container = null;

  function ensureContainer() {
    if (container) return container;
    const el = document.createElement("div");
    el.className = "orderhub-toast-container";
    el.setAttribute("role", "region");
    el.setAttribute("aria-label", "OrderHub toasts");
    document.body.appendChild(el);
    container = el;
    return container;
  }

  function normalizeOptions(options) {
    const o = options || {};
    return {
      key: o.key ? String(o.key) : null,
      durationMs: typeof o.durationMs === "number" ? o.durationMs : null
    };
  }

  function removeExistingWithKey(key) {
    if (!key) return;
    const el = ensureContainer().querySelector(".orderhub-toast[data-key=\"" + cssEscape(key) + "\"]");
    if (el) removeToast(el);
  }

  function cssEscape(s) {
    // Minimal escape for attribute selector usage
    return String(s).replace(/\\/g, "\\\\").replace(/"/g, "\\\"");
  }

  function trimToMaxVisible() {
    const list = ensureContainer().querySelectorAll(".orderhub-toast");
    if (list.length <= MAX_VISIBLE) return;
    for (let i = 0; i < list.length - MAX_VISIBLE; i++) {
      removeToast(list[i]);
    }
  }

  function removeToast(toastEl) {
    if (!toastEl) return;
    if (toastEl.__leaving) return;
    toastEl.__leaving = true;
    toastEl.classList.add("is-leaving");
    const cleanup = function () {
      try {
        toastEl.remove();
      } catch (e) { /* ignore */ }
    };
    toastEl.addEventListener("animationend", cleanup, { once: true });
    // Fallback in case animationend doesn't fire
    setTimeout(cleanup, 250);
  }

  function show(type, message, options) {
    const msg = String(message || "").trim();
    if (!msg) return;

    const opt = normalizeOptions(options);
    const duration = opt.durationMs != null ? opt.durationMs : (DEFAULT_DURATION[type] || 1800);

    if (opt.key) removeExistingWithKey(opt.key);

    const c = ensureContainer();

    const toast = document.createElement("div");
    toast.className = "orderhub-toast orderhub-toast--" + type;
    toast.setAttribute("role", "status");
    toast.setAttribute("aria-live", "polite");
    if (opt.key) toast.setAttribute("data-key", opt.key);

    const msgEl = document.createElement("div");
    msgEl.className = "orderhub-toast__message";
    msgEl.textContent = msg;

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "orderhub-toast__close";
    closeBtn.setAttribute("aria-label", "Close");
    closeBtn.innerHTML = "&times;";
    closeBtn.addEventListener("click", function () {
      removeToast(toast);
    });

    toast.appendChild(msgEl);
    toast.appendChild(closeBtn);

    c.appendChild(toast);
    trimToMaxVisible();

    const t = setTimeout(function () {
      removeToast(toast);
    }, Math.max(300, duration));

    // If removed early, clear timer
    toast.addEventListener("remove", function () { clearTimeout(t); });
  }

  global.OrderHubToast = {
    success: function (message, options) { show("success", message, options); },
    error: function (message, options) { show("error", message, options); },
    warning: function (message, options) { show("warning", message, options); },
    info: function (message, options) { show("info", message, options); }
  };
})(window);
