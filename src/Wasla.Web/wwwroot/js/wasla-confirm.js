// The application's confirmation dialog (WAS-119): one accessible Bootstrap modal per page, _WaslaConfirmDialog.cshtml.
// It replaces the browser's native confirm() on Orders, the Live Screen and Print Bridge.
//   WaslaConfirm.ask(options): the dialog closes on confirmation and the caller submits. Resolves true or false.
//   WaslaConfirm.run(options): the dialog stays open while options.action runs (security actions). Closing is impossible
//   meanwhile, because closing a screen cannot cancel a request already sent.
// Opening the dialog sends nothing. Cancel, Escape, the close button and the backdrop send nothing either.
(function (global) {
  "use strict";

  // The click or key press that opened the dialog must not also answer it. Time-based: under reduced motion there is
  // no transition to wait for.
  const ARM_MS = 250;

  // Bootstrap ignores hide() (and show()) while a modal is still fading in or out, so these flows follow each modal's
  // own events instead of assuming a hide() takes effect. Listening on document, registered before any waiter below,
  // records the state before a waiter reacts to the same event, and sees show/hide only once they were allowed.
  const modalStates = new WeakMap();
  ["show", "shown", "hide", "hidden"].forEach(function (name) {
    document.addEventListener(name + ".bs.modal", function (ev) {
      if (!ev.defaultPrevented) modalStates.set(ev.target, name);
    });
  });

  function onceModalEvent(modalEl, name, callback) {
    function listener(ev) {
      if (ev.target !== modalEl) return;
      document.removeEventListener(name + ".bs.modal", listener);
      callback();
    }
    document.addEventListener(name + ".bs.modal", listener);
  }

  // Resolves true once the modal is hidden: one still opening finishes opening and is then hidden, one already closing
  // finishes closing. Resolves false when something kept it open.
  function whenHidden(modalEl) {
    return new Promise(function (resolve) {
      function hide() {
        global.bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        const state = modalStates.get(modalEl);
        if (state === "hide") onceModalEvent(modalEl, "hidden", function () { resolve(true); });
        else resolve(state === "hidden");
      }
      const state = modalEl ? modalStates.get(modalEl) : null;
      if (state === "show") onceModalEvent(modalEl, "shown", hide);
      else if (state === "shown") hide();
      else if (state === "hide") onceModalEvent(modalEl, "hidden", function () { resolve(true); });
      else resolve(true);
    });
  }

  // A polite announcement that never moves focus.
  function announce(text) {
    const region = document.querySelector("[data-wasla-confirm-announcer]");
    if (region) region.textContent = text || "";
  }

  // Who or what the dialog is about: { platform: element to copy, primary: order code or device name, detail: total or
  // machine name }. Text only; the platform badge is a copy of the one already on the page.
  function renderIdentity(container, identity) {
    if (!container) return;
    while (container.firstChild) container.removeChild(container.firstChild);
    const empty = !identity || (!identity.primary && !identity.platform);
    container.hidden = empty;
    if (empty) return;

    const lead = document.createElement("span");
    lead.className = "wasla-confirm-identity__lead";
    if (identity.platform) lead.appendChild(identity.platform.cloneNode(true));
    if (identity.primary) {
      const primary = document.createElement("span");
      primary.className = "wasla-confirm-identity__primary";
      primary.setAttribute("dir", "auto");
      primary.textContent = identity.primary;
      lead.appendChild(primary);
    }
    container.appendChild(lead);

    if (identity.detail) {
      const detail = document.createElement("span");
      detail.className = "wasla-confirm-identity__detail";
      detail.setAttribute("dir", "auto");
      detail.textContent = identity.detail;
      container.appendChild(detail);
    }
  }

  function setText(el, text) {
    if (!el) return;
    el.textContent = text || "";
    el.hidden = !text;
  }

  function focusBack(options) {
    const trigger = options.trigger;
    let target = trigger && trigger.isConnected && !trigger.closest(".modal") ? trigger : null;
    if (!target && typeof options.focusFallback === "function") target = options.focusFallback();
    if (target && typeof target.focus === "function") target.focus({ preventScroll: true });
  }

  let busy = false;

  function present(dialog, options) {
    const part = function (name) { return dialog.querySelector("[data-wasla-confirm-" + name + "]"); };
    const title = part("title"), body = part("body"), hint = part("hint"), notice = part("notice");
    const accept = part("accept"), cancel = part("cancel"), close = part("close");
    const cancelLabel = cancel.getAttribute("data-label-cancel");
    const closeLabel = cancel.getAttribute("data-label-close");

    function showNotice(text, tone) {
      notice.textContent = text || "";
      notice.hidden = !text;
      notice.className = "alert small mb-0 py-2 " + (tone === "danger" ? "alert-danger" : "alert-warning");
    }

    title.textContent = options.title || "";
    setText(body, options.body);
    setText(hint, options.hint);
    showNotice("", "warning");
    renderIdentity(part("identity"), options.identity);
    accept.hidden = false;
    accept.disabled = false;
    accept.className = "btn " + (options.confirmClass || "btn-primary");
    accept.textContent = options.confirmLabel || "";
    cancel.disabled = false;
    cancel.textContent = options.cancelLabel || cancelLabel;
    close.disabled = false;

    return new Promise(function (resolve) {
      const modal = global.bootstrap.Modal.getOrCreateInstance(dialog);
      const armedAt = Date.now() + ARM_MS;
      let outcome = false;
      // Set by the first of Confirm, Cancel or Escape: later clicks while the dialog fades in or out change nothing.
      let closing = false;
      let sending = false;
      let answered = false;
      let unwatch = null;

      function finish(value) {
        if (closing) return;
        closing = true;
        outcome = value;
        // While the dialog is still opening, it closes as soon as it has opened.
        whenHidden(dialog);
      }

      // The answer is no longer the user's to give: only Close remains, on the same subject.
      function settleOnClose(result) {
        answered = true;
        if (result.title) title.textContent = result.title;
        if (result.hideBody) setText(body, "");
        setText(hint, "");
        showNotice(result.notice, result.tone);
        accept.hidden = true;
        cancel.disabled = false;
        close.disabled = false;
        cancel.textContent = closeLabel;
        cancel.focus({ preventScroll: true });
      }

      function setSending(pending) {
        sending = pending;
        cancel.disabled = pending;
        close.disabled = pending;
        accept.disabled = pending;
        if (pending) {
          const spinner = document.createElement("span");
          spinner.className = "spinner-border spinner-border-sm wasla-action-spinner me-2";
          spinner.setAttribute("aria-hidden", "true");
          accept.textContent = "";
          accept.appendChild(spinner);
          accept.appendChild(document.createTextNode(options.pendingLabel || ""));
          // The focused button is now disabled; the dialog keeps focus inside it.
          dialog.focus({ preventScroll: true });
          announce(options.pendingLabel || "");
        } else {
          accept.textContent = options.confirmLabel || "";
        }
      }

      async function runAction() {
        setSending(true);
        let result = null;
        try {
          result = await options.action();
        } catch (_) {
          result = { notice: options.failureText || "", tone: "danger" };
        }
        setSending(false);
        if (!result || result.close) finish(true);
        else settleOnClose(result);
      }

      function onAccept() {
        if (closing || sending || answered || Date.now() < armedAt) return;
        if (typeof options.action === "function") runAction();
        else finish(true);
      }

      function onClick(ev) {
        if (!ev.target.closest) return;
        if (ev.target.closest("[data-wasla-confirm-accept]")) { onAccept(); return; }
        // Bootstrap's own dismiss handling ignores a click while the dialog is still opening.
        if (ev.target.closest("[data-bs-dismiss=\"modal\"]") && !sending) finish(false);
      }

      // Escape while the dialog is still opening never reaches Bootstrap (its focus trap and key handling start once
      // the dialog has opened), so it cancels here. Once open, Bootstrap's own Escape runs first.
      function onKeydown(ev) {
        if (ev.repeat && (ev.key === "Enter" || ev.key === " ") && dialog.contains(ev.target)) {
          ev.preventDefault();
          return;
        }
        if (ev.key !== "Escape") return;
        ev.preventDefault();
        if (!sending) finish(false);
      }

      // While a request is out, Escape, the close button and the backdrop do nothing.
      function onHide(ev) {
        if (sending) { ev.preventDefault(); return; }
        closing = true;
      }

      function onShown() {
        if (closing) return;
        dialog.setAttribute("role", "alertdialog");
        (options.initialFocus === "confirm" && !accept.hidden ? accept : cancel).focus({ preventScroll: true });
      }

      function onHidden() {
        dialog.removeEventListener("click", onClick);
        document.removeEventListener("keydown", onKeydown);
        dialog.removeEventListener("hide.bs.modal", onHide);
        dialog.removeEventListener("shown.bs.modal", onShown);
        if (typeof unwatch === "function") unwatch();
        focusBack(options);
        resolve(outcome);
      }

      dialog.addEventListener("click", onClick);
      document.addEventListener("keydown", onKeydown);
      dialog.addEventListener("hide.bs.modal", onHide);
      dialog.addEventListener("shown.bs.modal", onShown);
      dialog.addEventListener("hidden.bs.modal", onHidden, { once: true });

      // A refresh never changes what the dialog is about: when the action stops being possible, the dialog says so.
      if (typeof options.watch === "function") {
        unwatch = options.watch({
          invalidate: function (text) {
            if (closing || sending || answered) return;
            settleOnClose({ notice: text, tone: "warning" });
          }
        });
      }

      modal.show();
    });
  }

  function open(options) {
    if (busy) return Promise.resolve(false);
    const dialog = document.querySelector("[data-wasla-confirm-dialog]");
    if (!dialog || !global.bootstrap || !global.bootstrap.Modal) {
      // Fail closed: nothing is sent without a confirmation the user could see.
      const message = dialog ? dialog.getAttribute("data-unavailable-message") : "";
      if (message && global.WaslaToast && typeof global.WaslaToast.error === "function") {
        global.WaslaToast.error(message, { key: "wasla-confirm-unavailable" });
      }
      return Promise.resolve(false);
    }

    busy = true;
    return Promise.resolve()
      // Bootstrap does not stack modals: a confirmation asked from inside another modal closes that one first.
      .then(function () { return options.closeFirst ? whenHidden(options.closeFirst) : true; })
      .then(function (hidden) { return hidden ? present(dialog, options) : false; })
      .finally(function () { busy = false; });
  }

  global.WaslaConfirm = {
    ask: open,
    run: open,
    isOpen: function () { return busy; },
    whenHidden: whenHidden,
    announce: announce,
    renderIdentity: renderIdentity
  };
})(window);
