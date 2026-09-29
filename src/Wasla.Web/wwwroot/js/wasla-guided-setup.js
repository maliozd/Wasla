(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && !root.WaslaGuidedSetup) {
    root.WaslaGuidedSetup = api;
    api.bind(root.document, root);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  // Guided-setup forms post once. State rules live on the server; this only prevents double clicks.
  var FORM_SELECTOR = "form[data-guided-setup-form]";

  function isGuidedForm(form) {
    return !!(form && typeof form.matches === "function" && form.matches(FORM_SELECTOR));
  }

  function setBusy(form, busy) {
    if (busy) form.setAttribute("data-guided-setup-submitting", "true");
    else form.removeAttribute("data-guided-setup-submitting");
    form.setAttribute("aria-busy", busy ? "true" : "false");
    var buttons = form.querySelectorAll("button[type='submit'], button:not([type])");
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = busy;
  }

  /** Returns false (and cancels the event) when the form is already submitting. */
  function handleSubmit(event) {
    var form = event.target;
    if (!isGuidedForm(form)) return true;
    if (form.getAttribute("data-guided-setup-submitting") === "true") {
      event.preventDefault();
      return false;
    }
    setBusy(form, true);
    return true;
  }

  /** A page restored from the back/forward cache must be usable again. */
  function resetAll(doc) {
    var forms = doc.querySelectorAll(FORM_SELECTOR);
    for (var i = 0; i < forms.length; i++) setBusy(forms[i], false);
  }

  /** The End dialog opens on its safe choice; Bootstrap returns focus to the trigger on close. */
  function focusSafeChoice(event) {
    var dialog = event.target;
    if (!dialog || typeof dialog.hasAttribute !== "function" || !dialog.hasAttribute("data-guided-setup-dialog")) return;
    var cancel = dialog.querySelector("[data-guided-setup-cancel]");
    if (cancel && typeof cancel.focus === "function") cancel.focus();
  }

  var bound = false;

  function bind(doc, win) {
    if (bound || !doc || typeof doc.addEventListener !== "function") return;
    bound = true;
    doc.addEventListener("submit", handleSubmit);
    doc.addEventListener("shown.bs.modal", focusSafeChoice);
    if (win && typeof win.addEventListener === "function") {
      win.addEventListener("pageshow", function (event) {
        if (event && event.persisted) resetAll(doc);
      });
    }
  }

  return {
    bind: bind,
    handleSubmit: handleSubmit,
    resetAll: resetAll,
    focusSafeChoice: focusSafeChoice
  };
});
