// The signed-in employee's choice to confirm Approve and Mark ready (WAS-119). The server holds it, per employee and
// tenant; a missing record means confirmations stay on. Until the first read answers, and after a failed read, the page
// keeps asking, which is the safe side. Reads happen on page load and whenever the tab becomes visible again, so another
// open tab catches up without a new realtime channel.
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O) return;

  const baseUrl = O.opts && O.opts.orderActionPreferenceUrl;
  let confirmRoutine = true;
  let readSeq = 0;
  let saving = false;
  let lastAttempt = null;

  function getCsrfToken() {
    const input = document.querySelector("input[name=\"__RequestVerificationToken\"]");
    return input ? input.value : null;
  }

  function parts() {
    const group = document.querySelector("[data-order-action-preference]");
    if (!group) return null;
    return {
      group: group,
      input: group.querySelector("[data-order-action-preference-input]"),
      status: group.querySelector("[data-order-action-preference-status]"),
      error: group.querySelector("[data-order-action-preference-error]")
    };
  }

  function showValue() {
    const p = parts();
    if (p && p.input) p.input.checked = confirmRoutine;
  }

  function setState(state) {
    const p = parts();
    if (!p) return;
    p.group.classList.toggle("is-saving", state === "saving");
    p.group.setAttribute("aria-busy", state === "saving" ? "true" : "false");
    if (p.status) {
      const text = state === "saving" ? p.status.getAttribute("data-label-saving")
        : state === "saved" ? p.status.getAttribute("data-label-saved")
        : "";
      p.status.textContent = text || "";
      p.status.hidden = !text;
    }
    if (p.error) p.error.hidden = state !== "failed";
  }

  async function read() {
    if (!baseUrl) return;
    const seq = ++readSeq;
    try {
      const resp = await fetch(baseUrl + "/current", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });
      if (!resp.ok) return;
      const body = await resp.json();
      // A save in progress, or a newer read, decides instead.
      if (seq !== readSeq || saving || typeof body.confirmRoutineActions !== "boolean") return;
      confirmRoutine = body.confirmRoutineActions;
      showValue();
    } catch (_) { /* keeps the current value */ }
  }

  async function save(next) {
    if (!baseUrl || saving) return;
    saving = true;
    lastAttempt = next;
    readSeq++;
    setState("saving");
    let saved = false;
    try {
      const token = getCsrfToken();
      const headers = { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/x-www-form-urlencoded" };
      if (token) headers["RequestVerificationToken"] = token;
      const resp = await fetch(baseUrl, {
        method: "POST",
        credentials: "same-origin",
        headers: headers,
        body: "confirmRoutineActions=" + (next ? "true" : "false")
      });
      if (resp.ok) {
        const body = await resp.json();
        if (typeof body.confirmRoutineActions === "boolean") {
          confirmRoutine = body.confirmRoutineActions;
          saved = true;
        }
      }
    } catch (_) { /* reported below */ }
    // A read that started during this save may have seen the old value.
    readSeq++;
    saving = false;
    // The switch only ever shows a stored value.
    showValue();
    setState(saved ? "saved" : "failed");
  }

  function bindSwitch() {
    const p = parts();
    if (!p || !p.input) return;
    p.input.checked = confirmRoutine;
    p.input.addEventListener("click", function (ev) {
      // The switch does not move until the server confirms: the click only starts the save.
      ev.preventDefault();
      if (!saving) save(!confirmRoutine);
    });
    p.group.addEventListener("click", function (ev) {
      // A click inside this group keeps the settings menu open so the result stays visible.
      ev.stopPropagation();
      if (ev.target.closest && ev.target.closest("[data-order-action-preference-retry]") && lastAttempt !== null && !saving) {
        save(lastAttempt);
      }
    });
  }

  O.actionPreference = {
    confirmsRoutineActions: function () { return confirmRoutine; },
    refresh: read
  };

  document.addEventListener("DOMContentLoaded", function () {
    bindSwitch();
    read();
  });
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "visible") read();
  });
})(window);
