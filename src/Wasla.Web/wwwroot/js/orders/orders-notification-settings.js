// Notification settings modal: load, save, status UI (WaslaOrders + O.audio).
(function (global) {
  "use strict";

  const O = global.WaslaOrders;
  if (!O || !O.audio) {
    return;
  }

  let notificationSettingsBindingsAbort = null;
  let isSavingNotificationSettings = false;

  function isHexColor(v) {
    if (!v) return false;
    const s = String(v).trim();
    return /^#[0-9a-fA-F]{6}$/.test(s);
  }

  function hexToRgba(hex, alpha) {
    try {
      const h = String(hex).trim().replace("#", "");
      const r = parseInt(h.substring(0, 2), 16);
      const g = parseInt(h.substring(2, 4), 16);
      const b = parseInt(h.substring(4, 6), 16);
      const a = Math.max(0, Math.min(1, alpha == null ? 0.18 : alpha));
      return "rgba(" + r + "," + g + "," + b + "," + a + ")";
    } catch (e) {
      return "rgba(255,243,205,0.18)";
    }
  }

  function restartPreviewAnimation(el) {
    if (!el) return;
    el.classList.remove("preview-anim");
    // Force reflow.
    void el.offsetHeight;
    el.classList.add("preview-anim");
  }

  function clearPreviewClasses(preview) {
    preview.classList.remove(
      "highlight-color-yellow",
      "highlight-color-orange",
      "highlight-color-blue",
      "highlight-color-green",
      "highlight-color-red",
      "highlight-color-custom",
      "highlight-behavior-fade",
      "highlight-behavior-pulse",
      "highlight-behavior-blink",
      "highlight-behavior-borderGlow",
      "highlight-behavior-none"
    );
  }

  function getSelectedHighlightColor() {
    const hidden = document.getElementById("NewOrderHighlightColor");
    const custom = document.getElementById("NewOrderHighlightColorCustom");
    const v = hidden ? String(hidden.value || "").trim() : "";
    if (isHexColor(v)) return v;
    // If custom color input exists and last chosen is custom, hidden will be hex. Otherwise ignore.
    if (!v && custom && isHexColor(custom.value)) return custom.value;
    return (v || "yellow").toLowerCase();
  }

  function setSelectedHighlightColor(value) {
    const hidden = document.getElementById("NewOrderHighlightColor");
    if (hidden) hidden.value = String(value || "");

    const swatches = document.querySelectorAll(".wasla-color-swatch[data-highlight-color]");
    swatches.forEach(function (b) {
      const c = (b.getAttribute("data-highlight-color") || "").toLowerCase();
      b.classList.toggle("active", c && String(value).toLowerCase() === c);
    });
  }

  function updateHighlightPreview() {
    const preview = document.getElementById("newOrderHighlightPreview");
    if (!preview) return;

    const color = getSelectedHighlightColor();
    const behavior = (document.getElementById("NewOrderHighlightBehavior") && document.getElementById("NewOrderHighlightBehavior").value) || "fade";

    preview.className = "order-highlight-preview";
    clearPreviewClasses(preview);
    preview.classList.add("highlight-behavior-" + behavior);

    if (color && String(color).startsWith("#")) {
      preview.classList.add("highlight-color-custom");
      preview.style.setProperty("--new-order-highlight-bg", hexToRgba(color, 0.18));
      preview.style.setProperty("--new-order-highlight-border", String(color));
    } else {
      preview.classList.add("highlight-color-" + (color || "yellow"));
      preview.style.removeProperty("--new-order-highlight-bg");
      preview.style.removeProperty("--new-order-highlight-border");
    }

    restartPreviewAnimation(preview);
  }

  function parseHighlightDurationSeconds() {
    const el = document.getElementById("NewOrderHighlightDurationSeconds");
    const v = el ? parseInt(el.value, 10) : 30;
    if (isNaN(v) || v < 1) return 30;
    return v;
  }

  function getModalState() {
    const enabledHidden = document.getElementById("NewOrderSoundEnabled");
    const enabled = enabledHidden ? (enabledHidden.value === "true") : true;

    const soundSel = document.getElementById("newOrderSoundSelect");
    const nameFromSelect = soundSel && soundSel.value
      ? String(soundSel.value).toLowerCase()
      : (document.getElementById("NewOrderSoundName") && document.getElementById("NewOrderSoundName").value) || "bell1";
    const nameEl = document.getElementById("NewOrderSoundName");
    if (nameEl) nameEl.value = nameFromSelect;

    const repeat = parseInt((document.getElementById("NewOrderSoundRepeatCount") && document.getElementById("NewOrderSoundRepeatCount").value) || "3", 10);
    const volPct = parseInt((document.getElementById("NewOrderSoundVolumePercent") && document.getElementById("NewOrderSoundVolumePercent").value) || "100", 10);
    const showChk = document.getElementById("ShowBrowserNotification");

    const hb = document.getElementById("NewOrderHighlightBehavior");
    const newOrderHighlightColor = getSelectedHighlightColor();
    const newOrderHighlightBehavior = (hb && hb.value) ? String(hb.value) : "fade";
    const newOrderHighlightDurationSeconds = parseHighlightDurationSeconds();

    return {
      newOrderSoundEnabled: enabled,
      newOrderSoundName: nameFromSelect,
      newOrderSoundRepeatCount: isNaN(repeat) ? 3 : repeat,
      newOrderSoundVolumePercent: isNaN(volPct) ? 100 : volPct,
      showBrowserNotification: showChk ? showChk.checked === true : false,
      newOrderHighlightColor: newOrderHighlightColor,
      newOrderHighlightBehavior: newOrderHighlightBehavior,
      newOrderHighlightDurationSeconds: newOrderHighlightDurationSeconds
    };
  }

  function setModalEnabled(enabled) {
    const el = document.getElementById("NewOrderSoundEnabled");
    if (el) el.value = enabled ? "true" : "false";
  }

  function updateNotificationStatusUi(state) {
    const badge = document.getElementById("notificationStatusBadge");
    const help = document.getElementById("notificationStatusHelp");
    const warn = document.getElementById("notificationStatusWarning");
    if (!badge || !help || !warn) return;

    warn.classList.add("d-none");
    warn.textContent = "";

    if (!state.newOrderSoundEnabled) {
      badge.className = "badge text-bg-secondary";
      badge.textContent = O.getMessage("notificationsOff");
      help.textContent = O.getMessage("notificationsOffHelp");
      return;
    }

    if (!O.audio.isSoundUnlocked()) {
      badge.className = "badge text-bg-warning";
      badge.textContent = O.getMessage("waitingForAudio");
      help.textContent = O.getMessage("waitingForAudioHelp");
      return;
    }

    badge.className = "badge text-bg-success";
    badge.textContent = O.getMessage("notificationsOn");
    help.textContent = O.getMessage("notificationsOnHelp");
  }

  function showModalWarning(text) {
    const warn = document.getElementById("notificationStatusWarning");
    if (!warn) return;
    warn.textContent = text;
    warn.classList.remove("d-none");
  }

  function selectNotificationSound(soundName) {
    const sel = document.getElementById("newOrderSoundSelect");
    if (sel) {
      var found = false;
      for (var i = 0; i < sel.options.length; i++) {
        if (sel.options[i].value && sel.options[i].value.toLowerCase() === String(soundName).toLowerCase()) {
          sel.selectedIndex = i;
          found = true;
          break;
        }
      }
      if (!found && sel.options.length) sel.selectedIndex = 0;
    }
    const hidden = document.getElementById("NewOrderSoundName");
    if (hidden) hidden.value = (sel && sel.value) ? sel.value : String(soundName);
  }

  function hideNotificationSettingsModal() {
    const modalEl = document.getElementById("notificationSettingsModal");
    if (!modalEl || !global.bootstrap || !global.bootstrap.Modal) return;
    const inst = global.bootstrap.Modal.getInstance(modalEl) || global.bootstrap.Modal.getOrCreateInstance(modalEl);
    if (inst) inst.hide();
  }

  function mergeDefaultNotificationState(json) {
    const n = json || {};
    return {
      newOrderSoundEnabled: n.newOrderSoundEnabled !== false,
      newOrderSoundName: (n.newOrderSoundName || "bell1").toLowerCase(),
      newOrderSoundRepeatCount: n.newOrderSoundRepeatCount != null ? n.newOrderSoundRepeatCount : 3,
      newOrderSoundVolumePercent: n.newOrderSoundVolumePercent != null ? n.newOrderSoundVolumePercent : 100,
      showBrowserNotification: !!n.showBrowserNotification,
      newOrderHighlightColor: n.newOrderHighlightColor || "yellow",
      newOrderHighlightBehavior: n.newOrderHighlightBehavior || "fade",
      newOrderHighlightDurationSeconds: n.newOrderHighlightDurationSeconds != null ? n.newOrderHighlightDurationSeconds : 30
    };
  }

  async function loadNotificationSettings() {
    try {
      const resp = await fetch(O.opts.notificationSettingsJsonUrl, { headers: { "X-Requested-With": "fetch" } });
      O.debugLog("Loading notification settings", {
        url: O.opts.notificationSettingsJsonUrl,
        status: resp.status
      });

      if (!resp.ok) {
        const base = O.getMessage("notificationSettingsLoadFailed");
        if (global.WaslaToast) {
          global.WaslaToast.error(base + " (HTTP " + resp.status + ")", { key: "notification-settings-load" });
        } else {
          O.showOrdersWarning("notification-settings-failed", base + " (HTTP " + resp.status + ")");
        }
        O.debugWarn("Notification settings request failed", resp);
        return;
      }

      const json = await resp.json();
      O.debugLog("Notification settings JSON", json);

      const required = [
        "newOrderSoundEnabled",
        "newOrderSoundName",
        "newOrderSoundRepeatCount",
        "newOrderSoundVolumePercent",
        "showBrowserNotification"
      ];
      const missing = required.filter(function (k) { return !(k in json); });
      if (missing.length > 0) {
        O.showOrdersWarning("notification-settings-shape", O.getMessage("notificationSettingsUnexpectedFormat"));
        O.debugWarn("Notification settings JSON missing fields", { missing: missing, json: json });
      }

      O.state.notificationSettings = mergeDefaultNotificationState(json);
    } catch (error) {
      if (global.WaslaToast) {
        global.WaslaToast.error(O.getMessage("notificationSettingsLoadException"), { key: "notification-settings-load-ex" });
      } else {
        O.showOrdersWarning("notification-settings-exception", O.getMessage("notificationSettingsLoadException"));
      }
      O.debugWarn("loadNotificationSettings failed", error);
    }
  }

  function wireNotificationModalContent() {
    if (notificationSettingsBindingsAbort) {
      try { notificationSettingsBindingsAbort.abort(); } catch (e) { /* ignore */ }
    }
    notificationSettingsBindingsAbort = new AbortController();
    const signal = notificationSettingsBindingsAbort.signal;

    function on(el, evt, handler) {
      if (!el) return;
      el.addEventListener(evt, handler, { signal: signal });
    }

    const toggle = document.getElementById("NewOrderSoundEnabledToggle");
    if (toggle) {
      on(toggle, "change", function () {
        setModalEnabled(toggle.checked);
        updateNotificationStatusUi(getModalState());
      });
    }

    const soundSel = document.getElementById("newOrderSoundSelect");
    if (soundSel) {
      on(soundSel, "change", function () {
        selectNotificationSound(soundSel.value);
        updateNotificationStatusUi(getModalState());
      });
    }

    const testSelectedBtn = document.getElementById("testSelectedSoundBtn");
    if (testSelectedBtn) {
      on(testSelectedBtn, "click", async function () {
        const st = getModalState();
        await O.audio.maybeRequestBrowserNotificationPermission(st);
        if (O.audio && typeof O.audio.stopCurrentPreviewSound === "function") {
          O.audio.stopCurrentPreviewSound();
        }
        const opt = soundSel && soundSel.options[soundSel.selectedIndex];
        const url = opt && opt.getAttribute("data-sound-url");
        await O.audio.playSoundNow(st, url || null);
        updateNotificationStatusUi(getModalState());
      });
    }

    // Highlight swatches + custom color
    document.querySelectorAll(".wasla-color-swatch[data-highlight-color]").forEach(function (btn) {
      on(btn, "click", function (e) {
        e.preventDefault();
        const c = btn.getAttribute("data-highlight-color") || "yellow";
        setSelectedHighlightColor(String(c).toLowerCase());
        updateHighlightPreview();
      });
    });

    const customColor = document.getElementById("NewOrderHighlightColorCustom");
    if (customColor) {
      on(customColor, "input", function () {
        const v = String(customColor.value || "").trim();
        if (isHexColor(v)) {
          setSelectedHighlightColor(v.toLowerCase());
          updateHighlightPreview();
        }
      });
    }

    const hb = document.getElementById("NewOrderHighlightBehavior");
    if (hb) on(hb, "change", updateHighlightPreview);
    const hd = document.getElementById("NewOrderHighlightDurationSeconds");
    if (hd) on(hd, "change", updateHighlightPreview);

    const form = document.getElementById("notificationSettingsForm");
    if (form) {
      on(form, "submit", async function (e) {
        e.preventDefault();
        await saveNotificationSettings("save");
        updateNotificationStatusUi(getModalState());
      });
    }

    // Stop preview sound when modal closes
    const modalEl = document.getElementById("notificationSettingsModal");
    if (modalEl) {
      on(modalEl, "hidden.bs.modal", function () {
        if (O.audio && typeof O.audio.stopCurrentPreviewSound === "function") {
          O.audio.stopCurrentPreviewSound();
        }
        if (notificationSettingsBindingsAbort) {
          try { notificationSettingsBindingsAbort.abort(); } catch (e) { /* ignore */ }
          notificationSettingsBindingsAbort = null;
        }
      });
    }

    updateHighlightPreview();
  }

  async function saveNotificationSettings() {
    if (isSavingNotificationSettings) return;
    const form = document.getElementById("notificationSettingsForm");
    const body = document.getElementById("notificationSettingsModalBody");
    if (!form || !body) return;

    const saveBtn = document.getElementById("saveNotificationsBtn");

    try {
      isSavingNotificationSettings = true;
      if (saveBtn) saveBtn.disabled = true;
      const state = getModalState();
      await O.audio.maybeRequestBrowserNotificationPermission(state);

      const fd = new FormData(form);
      fd.set("NewOrderSoundEnabled", state.newOrderSoundEnabled ? "true" : "false");
      fd.set("NewOrderSoundName", state.newOrderSoundName);
      fd.set("NewOrderHighlightColor", state.newOrderHighlightColor);
      fd.set("NewOrderHighlightBehavior", state.newOrderHighlightBehavior);
      fd.set("NewOrderHighlightDurationSeconds", String(state.newOrderHighlightDurationSeconds));

      const url = form.action;
      const resp = await fetch(url, { method: "POST", body: fd, headers: { "X-Requested-With": "fetch" } });

      const ct = (resp.headers.get("content-type") || "").toLowerCase();
      if (resp.ok && ct.indexOf("application/json") >= 0) {
        var data = null;
        try { data = await resp.json(); } catch (e1) { data = null; }
        if (data && data.success) {
          await loadNotificationSettings();
          if (global.WaslaToast) {
            global.WaslaToast.success(O.getMessage("settingsSaved"), { key: "notification-settings-save" });
          }
          setTimeout(function () { hideNotificationSettingsModal(); }, 200);
          return;
        }
        if (global.WaslaToast) {
          global.WaslaToast.error(O.getMessage("settingsSaveFailed"), { key: "notification-settings-save" });
        }
        return;
      }

      if (!resp.ok) {
        const html = await resp.text();
        if (html) {
          body.innerHTML = html;
          wireNotificationModalContent();
        }
        if (global.WaslaToast) {
          global.WaslaToast.error(O.getMessage("settingsSaveFailed"), { key: "notification-settings-save" });
        }
        O.debugWarn("Notification settings save failed", resp.status);
        return;
      }

      const html = await resp.text();
      if (html) {
        body.innerHTML = html;
        wireNotificationModalContent();
      }
    } catch (error) {
      if (global.WaslaToast) {
        global.WaslaToast.error(O.getMessage("notificationSettingsSaveException"), { key: "notification-settings-save-ex" });
      } else {
        O.showOrdersWarning("notification-settings-save-exception", O.getMessage("notificationSettingsSaveException"));
      }
      O.debugWarn("saveNotificationSettings failed", error);
    } finally {
      isSavingNotificationSettings = false;
      if (saveBtn) saveBtn.disabled = false;
    }
  }

  async function openNotificationSettingsModal() {
    var resp;
    try {
      resp = await fetch(O.opts.notificationSettingsUrl, { headers: { "X-Requested-With": "fetch" } });
    } catch (error) {
      if (global.WaslaToast) {
        global.WaslaToast.error(O.getMessage("notificationSettingsModalOpenFailed"), { key: "notification-settings-open" });
      } else {
        O.showOrdersWarning("notification-settings-modal-failed", O.getMessage("notificationSettingsModalOpenFailed"));
      }
      O.debugWarn("Notification settings modal fetch failed", error);
      return;
    }

    O.debugLog("Notification settings modal response", { status: resp.status });
    if (!resp.ok) {
      const base = O.getMessage("notificationSettingsModalOpenFailed");
      if (global.WaslaToast) {
        global.WaslaToast.error(base + " (HTTP " + resp.status + ")", { key: "notification-settings-open" });
      } else {
        O.showOrdersWarning("notification-settings-modal-http", base + " (HTTP " + resp.status + ")");
      }
      O.debugWarn("Notification settings modal HTTP failed", resp);
      return;
    }

    const html = await resp.text();
    const body = document.getElementById("notificationSettingsModalBody");
    if (!body) return;
    body.innerHTML = html;

    if (O.state.notificationSettings) {
      const m = O.state.notificationSettings;
      setModalEnabled(!!m.newOrderSoundEnabled);
      if (m.newOrderSoundName) selectNotificationSound(m.newOrderSoundName);
      const repeatSel = document.getElementById("NewOrderSoundRepeatCount");
      if (repeatSel) repeatSel.value = String(m.newOrderSoundRepeatCount || 3);
      const volSel = document.getElementById("NewOrderSoundVolumePercent");
      if (volSel) volSel.value = String(m.newOrderSoundVolumePercent != null ? m.newOrderSoundVolumePercent : 100);
      const showChk = document.getElementById("ShowBrowserNotification");
      if (showChk) showChk.checked = !!m.showBrowserNotification;
      const hlC = document.getElementById("NewOrderHighlightColor");
      if (hlC && m.newOrderHighlightColor) hlC.value = m.newOrderHighlightColor;
      const hlB = document.getElementById("NewOrderHighlightBehavior");
      if (hlB && m.newOrderHighlightBehavior) hlB.value = m.newOrderHighlightBehavior;
      const hlD = document.getElementById("NewOrderHighlightDurationSeconds");
      if (hlD && m.newOrderHighlightDurationSeconds != null) hlD.value = String(m.newOrderHighlightDurationSeconds);

      const custom = document.getElementById("NewOrderHighlightColorCustom");
      if (custom && m.newOrderHighlightColor && isHexColor(m.newOrderHighlightColor)) {
        custom.value = String(m.newOrderHighlightColor);
      }

      setSelectedHighlightColor(m.newOrderHighlightColor || "yellow");
    }

    wireNotificationModalContent();
    updateNotificationStatusUi(getModalState());
  }

  O.notificationSettings = {
    load: loadNotificationSettings,
    openNotificationSettingsModal: openNotificationSettingsModal,
    getModalState: getModalState,
    setModalEnabled: setModalEnabled,
    updateNotificationStatusUi: updateNotificationStatusUi,
    showModalWarning: showModalWarning,
    selectNotificationSound: selectNotificationSound,
    saveNotificationSettings: saveNotificationSettings,
    mergeDefaultNotificationState: mergeDefaultNotificationState
  };
})(window);
