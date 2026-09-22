// Orders sync settings toggle: tenant-managed enable/disable (does not control Worker process).
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) return;

  function getToken() {
    const f = document.getElementById("orderSyncSettingsForm");
    if (!f) return null;
    const el = f.querySelector("input[name=\"__RequestVerificationToken\"]");
    return el ? el.value : null;
  }

  function setUi(enabled) {
    const toggle = document.getElementById("orderSyncToggle");
    const badge = document.getElementById("orderSyncStatusBadge");
    if (toggle) toggle.checked = !!enabled;

    if (badge) {
      badge.textContent = enabled ? O.getMessage("ordersActive") : O.getMessage("ordersPassive");
      badge.classList.remove("text-bg-secondary", "text-bg-success");
      badge.classList.add(enabled ? "text-bg-success" : "text-bg-secondary");
    }
  }

  async function loadCurrent() {
    try {
      const url = O.opts.orderSyncSettingsUrl || "/orders/sync-settings";
      const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (!resp.ok) throw new Error("HTTP " + resp.status);
      const data = await resp.json();
      setUi(!!data.orderSyncEnabled);
    } catch (e) {
      // Non-fatal: keep default UI and avoid breaking the Orders page.
      if (O.isDebugEnabled()) O.debugWarn("OrderSync loadCurrent failed", e);
      O.showOrdersWarning("order-sync-load-failed", O.getMessage("orderSyncUpdateFailed"));
    }
  }

  async function update(enabled) {
    const url = O.opts.orderSyncSettingsUrl || "/orders/sync-settings";
    const token = getToken();

    const headers = { "X-Requested-With": "fetch" };
    if (token) headers["RequestVerificationToken"] = token;

    const body = new URLSearchParams();
    body.set("enabled", enabled ? "true" : "false");

    const resp = await fetch(url, { method: "POST", headers: headers, body: body });
    if (!resp.ok) throw new Error("HTTP " + resp.status);
    const data = await resp.json();
    setUi(!!data.orderSyncEnabled);
    O.showSettingsSaveSuccess(
      data.orderSyncEnabled ? O.getMessage("orderSyncEnabledMessage") : O.getMessage("orderSyncDisabledMessage")
    );
  }

  function bind() {
    const toggle = document.getElementById("orderSyncToggle");
    if (!toggle) return;

    toggle.addEventListener("change", function () {
      const next = !!toggle.checked;
      const previous = !next;
      toggle.disabled = true;
      update(next).catch(function (e) {
        if (O.isDebugEnabled()) O.debugWarn("OrderSync update failed", e);
        toggle.checked = previous;
        setUi(previous);
        O.showSettingsSaveError(O.getMessage("orderSyncUpdateFailed"));
      }).finally(function () {
        toggle.disabled = false;
      });
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    bind();
    loadCurrent();
  });
})(window);

