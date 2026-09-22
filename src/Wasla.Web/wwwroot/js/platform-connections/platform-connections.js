// Platform Connections: toggle active switch (Owner/Manager only; server enforced).
(function (global) {
  "use strict";

  function getCsrfToken() {
    const f = document.getElementById("platformConnectionsAntiForgery");
    if (!f) return null;
    const el = f.querySelector("input[name=\"__RequestVerificationToken\"]");
    return el ? el.value : null;
  }

  function toastSuccess(msg) {
    if (global.WaslaToast && typeof global.WaslaToast.success === "function") {
      global.WaslaToast.success(msg, { key: "platform-connection-toggle" });
    }
  }

  function toastError(msg) {
    if (global.WaslaToast && typeof global.WaslaToast.error === "function") {
      global.WaslaToast.error(msg, { key: "platform-connection-toggle" });
    }
  }

  async function postToggle(id, isActive) {
    const token = getCsrfToken();
    const body = new URLSearchParams();
    body.set("isActive", isActive ? "true" : "false");

    const headers = { "X-Requested-With": "XMLHttpRequest" };
    if (token) headers["RequestVerificationToken"] = token;

    const resp = await fetch("/platform-connections/" + encodeURIComponent(id) + "/toggle-active", {
      method: "POST",
      headers: headers,
      body: body
    });

    let payload = null;
    try { payload = await resp.json(); } catch (_) { /* ignore */ }
    return { resp: resp, payload: payload };
  }

  function updateStatusText(container, isActive) {
    const text = container.querySelector(".wasla-connections__status-text");
    if (!text) return;
    text.textContent = isActive
      ? (global.platformConnectionsMessages && global.platformConnectionsMessages.activeText) || text.textContent
      : (global.platformConnectionsMessages && global.platformConnectionsMessages.inactiveText) || text.textContent;
    text.classList.toggle("is-active", isActive);
  }

  function initToggles() {
    document.querySelectorAll(".pc-active-toggle").forEach(function (toggle) {
      toggle.addEventListener("change", async function () {
        const id = toggle.getAttribute("data-connection-id");
        if (!id) return;

        const previous = !toggle.checked;
        toggle.disabled = true;
        try {
          const result = await postToggle(id, toggle.checked);
          if (!result.resp.ok || !result.payload || result.payload.succeeded !== true) {
            toggle.checked = previous;
            toastError((global.platformConnectionsMessages && global.platformConnectionsMessages.toggleFailed) || "Toggle failed");
            return;
          }

          const row = toggle.closest("tr");
          const statusCell = row ? row.querySelector("td:nth-child(3)") : null;
          if (statusCell) updateStatusText(statusCell, toggle.checked);
          toastSuccess((global.platformConnectionsMessages && global.platformConnectionsMessages.toggled) || "Updated");
        } catch (_) {
          toggle.checked = previous;
          toastError((global.platformConnectionsMessages && global.platformConnectionsMessages.toggleFailed) || "Toggle failed");
        } finally {
          toggle.disabled = false;
        }
      });
    });
  }

  document.addEventListener("DOMContentLoaded", initToggles);
})(window);

