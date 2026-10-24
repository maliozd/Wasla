// Live Screen header: platform synchronization status. Each Live Screen snapshot carries the server's reading of the
// tenant's platform connection records (NotConnected, SyncDisabled, Live or Issue, each platform's state and the oldest
// last successful sync); this script only shows it. An open page or a working poll is never shown as "Live": a snapshot
// without the section, or a poll that fails, shows a connection issue. No request is sent and nothing is inferred.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document && root.WaslaOrders && !root.WaslaPlatformSyncStatus) {
    root.WaslaPlatformSyncStatus = api;
    api.boot(root.document, root.WaslaOrders);
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  "use strict";

  var STATUS_ID = "ordersLiveDisplaySyncStatus";
  var LAST_SYNC_ITEM_ID = "ordersLiveDisplayLastSyncItem";
  var LAST_SYNC_ID = "ordersLiveDisplayLastSync";
  var BASE_CLASS = "wasla-live-display-status";
  var MODIFIERS = ["pending", "issue", "off"];

  // Display names of the platforms the page has words for; any other value is left out of the text.
  var PLATFORM_MESSAGES = {
    Yemeksepeti: "platformYemeksepeti",
    GetirYemek: "platformGetirYemek",
    TrendyolYemek: "platformTrendyolYemek"
  };

  /**
   * Words, look and icon for a snapshot's sync section. Anything that is not one of the server's four states (a
   * missing section included) is a connection issue: the page never claims "Live" without the backend saying so.
   * Each state has its own words, so the meaning never depends on color or icon.
   */
  function stateView(sync, message) {
    var state = sync && typeof sync === "object" ? sync.state : null;
    if (state === "Live") return { key: "live", modifier: null, icon: null, text: message("syncStatusLive") };
    if (state === "NotConnected") return { key: "not-connected", modifier: "off", icon: "bi-plug", text: message("syncStatusNotConnected") };
    if (state === "SyncDisabled") return { key: "sync-disabled", modifier: "off", icon: "bi-pause-circle", text: message("syncStatusSyncDisabled") };

    var names = [];
    var platforms = sync && Array.isArray(sync.platforms) ? sync.platforms : [];
    for (var i = 0; i < platforms.length; i++) {
      var platform = platforms[i];
      var key = platform && platform.state === "Issue" ? PLATFORM_MESSAGES[platform.platform] : null;
      if (key && names.indexOf(message(key)) < 0) names.push(message(key));
    }
    var template = message("syncStatusIssueWithPlatforms");
    var text = names.length && template.indexOf("{0}") >= 0
      ? template.replace("{0}", names.join(", "))
      : message("syncStatusIssue");
    return { key: "issue", modifier: "issue", icon: "bi-exclamation-triangle-fill", text: text };
  }

  function isTimestamp(value) {
    return typeof value === "string" && value.length > 0 && isFinite(Date.parse(value));
  }

  /** The time of day when the sync was on the server's current day (in the page's time zone), else date and time. */
  function formatSyncTime(lastSyncUtc, serverTimeUtc, culture, timeZone) {
    var at = new Date(lastSyncUtc);
    if (isNaN(at.getTime())) return "";
    var zone = timeZone || "Europe/Istanbul";
    var day = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" });
    var server = new Date(serverTimeUtc);
    var sameDay = !isNaN(server.getTime()) && day.format(at) === day.format(server);
    // Gregorian in every language: ar-SA would otherwise default to the Umm al-Qura calendar.
    var options = sameDay
      ? { timeZone: zone, calendar: "gregory", timeStyle: "short" }
      : { timeZone: zone, calendar: "gregory", dateStyle: "short", timeStyle: "short" };
    try {
      return new Intl.DateTimeFormat(culture || "tr-TR", options).format(at);
    } catch (e) {
      return new Intl.DateTimeFormat(undefined, options).format(at);
    }
  }

  function showStatus(doc, view) {
    var el = doc.getElementById(STATUS_ID);
    if (!el) return false;
    if (el.getAttribute("data-sync-state") !== view.key) el.setAttribute("data-sync-state", view.key);
    for (var i = 0; i < MODIFIERS.length; i++) el.classList.remove(BASE_CLASS + "--" + MODIFIERS[i]);
    if (view.modifier) el.classList.add(BASE_CLASS + "--" + view.modifier);

    var text = el.querySelector("[data-sync-status-text]");
    // Only a real change is written, so the polite status region announces a change once, not on every poll.
    if (text && text.textContent !== view.text) text.textContent = view.text;

    var dot = el.querySelector(".wasla-live-display-status__dot");
    if (dot) dot.hidden = !!view.icon;
    var icon = el.querySelector(".wasla-live-display-status__icon");
    if (icon) {
      icon.className = "wasla-live-display-status__icon bi" + (view.icon ? " " + view.icon : "");
      icon.hidden = !view.icon;
    }
    return true;
  }

  function showLastSync(doc, sync, serverTimeUtc, options) {
    var item = doc.getElementById(LAST_SYNC_ITEM_ID);
    var time = doc.getElementById(LAST_SYNC_ID);
    if (!item || !time) return;
    var value = sync && isTimestamp(sync.lastSuccessfulSyncUtc) ? sync.lastSuccessfulSyncUtc : null;
    if (!value) {
      item.hidden = true;
      time.textContent = "";
      time.removeAttribute("datetime");
      return;
    }
    var text = formatSyncTime(value, serverTimeUtc, options.culture, options.timeZone);
    if (time.textContent !== text) time.textContent = text;
    time.setAttribute("datetime", value);
    item.hidden = !text;
  }

  /**
   * Shows a rendered snapshot's sync section. A section that is missing or not an object (the server could not read
   * the status) is a connection issue; the last successful sync shown before stays, because it is still the latest
   * known one.
   */
  function apply(doc, sync, serverTimeUtc, message, options) {
    var shown = showStatus(doc, stateView(sync, message));
    if (sync && typeof sync === "object") showLastSync(doc, sync, serverTimeUtc, options || {});
    return shown;
  }

  /** The snapshot poll failed (stale or session expired): the screen is not live, whatever the last snapshot said. */
  function applyPollFailure(doc, message) {
    return showStatus(doc, stateView(null, message));
  }

  function boot(doc, O) {
    var message = function (key) { return O.getMessage(key); };
    var options = {
      culture: O.opts.displayCulture || (doc.documentElement && doc.documentElement.lang) || "tr-TR",
      timeZone: O.opts.timeZoneId || "Europe/Istanbul"
    };
    doc.addEventListener("wasla:live-rendered", function (event) {
      var detail = event && event.detail ? event.detail : null;
      if (!detail) return;
      apply(doc, detail.sync, detail.serverTimeUtc, message, options);
    });
    doc.addEventListener("wasla:live-poll-status", function (event) {
      var kind = event && event.detail ? event.detail.kind : null;
      if (kind && kind !== "ok") applyPollFailure(doc, message);
    });
  }

  return {
    stateView: stateView,
    formatSyncTime: formatSyncTime,
    apply: apply,
    applyPollFailure: applyPollFailure,
    boot: boot
  };
});
