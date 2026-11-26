(function (root, factory) {
  "use strict";

  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document) api.bind(root.document, root);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  // The Create user form is sent once (WAS-249): a second click or Enter while the first submission is on its way is
  // not sent, so a double click lands on the new user instead of on the answer to a second post ("email already in
  // use"). The server stays the protection: simultaneous posts still create one user (WAS-116).
  var FORM_SELECTOR = "form[data-users-create]";
  var SUBMITTING = "data-users-submitting";

  function isCreateForm(form) {
    return !!(form && typeof form.matches === "function" && form.matches(FORM_SELECTOR));
  }

  // The submit button keeps its focus and its accessible name: aria-disabled says it does nothing now, and aria-busy on
  // the form says the form is being sent.
  function setBusy(form, busy) {
    if (busy) {
      form.setAttribute(SUBMITTING, "true");
      form.setAttribute("aria-busy", "true");
    } else {
      form.removeAttribute(SUBMITTING);
      form.removeAttribute("aria-busy");
    }
    var buttons = form.querySelectorAll("button[type='submit'], button:not([type])");
    for (var i = 0; i < buttons.length; i++) {
      if (busy) buttons[i].setAttribute("aria-disabled", "true");
      else buttons[i].removeAttribute("aria-disabled");
    }
  }

  /** Returns whether the submission goes ahead: the first one is marked, a later one is cancelled. */
  function handleSubmit(event) {
    var form = event && event.target;
    if (!isCreateForm(form)) return true;
    // A submission another script stopped was not sent, so the form stays usable.
    if (event.defaultPrevented) return false;
    if (form.getAttribute(SUBMITTING) === "true") {
      event.preventDefault();
      return false;
    }
    setBusy(form, true);
    return true;
  }

  /** A page restored from the back/forward cache can be sent again. */
  function resetAll(doc) {
    var forms = doc.querySelectorAll(FORM_SELECTOR);
    for (var i = 0; i < forms.length; i++) setBusy(forms[i], false);
  }

  function bind(doc, win) {
    if (!doc || typeof doc.addEventListener !== "function") return;
    // On the document, so it sees the submission after every handler on the form itself.
    doc.addEventListener("submit", handleSubmit);
    if (win && typeof win.addEventListener === "function") {
      win.addEventListener("pageshow", function (event) {
        if (event && event.persisted) resetAll(doc);
      });
    }
  }

  return { FORM_SELECTOR: FORM_SELECTOR, handleSubmit: handleSubmit, resetAll: resetAll, bind: bind };
});
