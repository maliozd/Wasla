(function (root, factory) {
  "use strict";

  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document) root.WaslaTheme = api.start(root);
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  // The one source of truth for the light/dark theme. Loaded synchronously in <head>, before any stylesheet or body
  // content, so the first frame is already painted in the resolved theme. theme-mode.js only drives the toggles.
  //
  // The preference is browser-local and per app: the page's <html data-wasla-theme-scope> names the app. Tenant hosts
  // are separate origins, so a tenant's preference never reaches another tenant. Central Admin is served under /admin
  // on every host, so it can share an origin with the tenant app: the two apps never share a storage key.
  //
  // Tenant app: an explicit choice wins; without one the operating-system preference applies, and later system changes
  // follow it until the user makes a choice. Central Admin keeps its existing behavior: its stored choice, else light.
  var SCOPES = {
    tenant: { storageKey: "Wasla.tenant.theme", followSystem: true },
    admin: { storageKey: "Wasla.theme", followSystem: false }
  };
  var SCOPE_ATTRIBUTE = "data-wasla-theme-scope";
  var THEME_ATTRIBUTE = "data-bs-theme";
  var DARK_QUERY = "(prefers-color-scheme: dark)";

  function normalize(value) {
    return value === "dark" || value === "light" ? value : null;
  }

  // systemPrefersDark is true/false, or null when the browser cannot report it.
  function resolve(explicit, systemPrefersDark, followSystem) {
    var chosen = normalize(explicit);
    if (chosen) return { theme: chosen, source: "explicit" };
    if (followSystem && systemPrefersDark !== null && systemPrefersDark !== undefined) {
      return { theme: systemPrefersDark ? "dark" : "light", source: "system" };
    }
    return { theme: "light", source: "default" };
  }

  function readStored(win, key) {
    try {
      return normalize(win.localStorage.getItem(key));
    } catch (e) {
      return null;
    }
  }

  function writeStored(win, key, theme) {
    try {
      win.localStorage.setItem(key, theme);
    } catch (e) { /* storage blocked or full: the choice still applies to this page */ }
  }

  function systemQuery(win, config) {
    if (!config.followSystem || typeof win.matchMedia !== "function") return null;
    try {
      return win.matchMedia(DARK_QUERY);
    } catch (e) {
      return null;
    }
  }

  function start(win) {
    var doc = win.document;
    var html = doc.documentElement;
    var scope = html.getAttribute(SCOPE_ATTRIBUTE);
    var config = Object.prototype.hasOwnProperty.call(SCOPES, scope) ? SCOPES[scope] : null;
    if (!config) return null;

    var media = systemQuery(win, config);
    var explicit = readStored(win, config.storageKey);
    var listeners = [];
    var state = null;

    function paint() {
      html.setAttribute(THEME_ATTRIBUTE, state.theme);
      // The layouts style both <html> and <body> by this attribute; keep them identical.
      if (doc.body) doc.body.setAttribute(THEME_ATTRIBUTE, state.theme);
    }

    function apply() {
      var previous = state;
      state = resolve(explicit, media ? media.matches : null, config.followSystem);
      paint();
      if (previous && previous.theme === state.theme && previous.source === state.source) return;
      listeners.slice().forEach(function (listener) {
        listener({ theme: state.theme, source: state.source });
      });
    }

    apply();

    // This script runs before <body> exists. Tag it as soon as the parser inserts it, before the first frame.
    if (!doc.body && typeof win.MutationObserver === "function") {
      var observer = new win.MutationObserver(function () {
        if (!doc.body) return;
        observer.disconnect();
        paint();
      });
      observer.observe(html, { childList: true });
    }
    if (typeof doc.addEventListener === "function") {
      doc.addEventListener("DOMContentLoaded", paint);
    }

    if (media) {
      var onSystemChange = function () {
        if (!explicit) apply();
      };
      if (typeof media.addEventListener === "function") media.addEventListener("change", onSystemChange);
      else if (typeof media.addListener === "function") media.addListener(onSystemChange);
    }

    // Another tab of the same app changed (or cleared) the choice.
    if (typeof win.addEventListener === "function") {
      win.addEventListener("storage", function (event) {
        if (event.key !== null && event.key !== config.storageKey) return;
        explicit = readStored(win, config.storageKey);
        apply();
      });
    }

    function choose(theme) {
      var chosen = normalize(theme);
      if (!chosen) return;
      explicit = chosen;
      writeStored(win, config.storageKey, chosen);
      apply();
    }

    return {
      scope: scope,
      current: function () {
        return { theme: state.theme, source: state.source };
      },
      choose: choose,
      toggle: function () {
        choose(state.theme === "dark" ? "light" : "dark");
      },
      subscribe: function (listener) {
        if (typeof listener === "function") listeners.push(listener);
      }
    };
  }

  return { SCOPES: SCOPES, normalize: normalize, resolve: resolve, start: start };
});
