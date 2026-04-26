// Notification settings modal: load, save, status UI (OrderHubOrders + O.audio).
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.audio) {
    return;
  }

  function getModalState() {
    const enabledHidden = document.getElementById("NewOrderSoundEnabled");
    const enabled = enabledHidden ? (enabledHidden.value === "true") : true;

    const nameEl = document.getElementById("NewOrderSoundName");
    const name = (nameEl && nameEl.value || "bell1").toLowerCase();
    const repeat = parseInt((document.getElementById("NewOrderSoundRepeatCount") && document.getElementById("NewOrderSoundRepeatCount").value) || "3", 10);
    const volPct = parseInt((document.getElementById("NewOrderSoundVolumePercent") && document.getElementById("NewOrderSoundVolumePercent").value) || "100", 10);
    const showChk = document.getElementById("ShowBrowserNotification");

    return {
      newOrderSoundEnabled: enabled,
      newOrderSoundName: name,
      newOrderSoundRepeatCount: isNaN(repeat) ? 3 : repeat,
      newOrderSoundVolumePercent: isNaN(volPct) ? 100 : volPct,
      showBrowserNotification: showChk ? showChk.checked === true : false
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
    const list = document.getElementById("soundOptionsList");
    if (!list) return;
    list.querySelectorAll(".sound-option").forEach(function (row) {
      const name = row.getAttribute("data-sound-name");
      const isActive = name && name.toLowerCase() === soundName.toLowerCase();
      row.classList.toggle("active", !!isActive);
      const radio = row.querySelector("input[type=radio]");
      if (radio) radio.checked = !!isActive;
    });

    const hidden = document.getElementById("NewOrderSoundName");
    if (hidden) hidden.value = soundName;

    const label = document.getElementById("selectedSoundLabel");
    if (label) {
      const row = list.querySelector(".sound-option[data-sound-name=\"" + soundName + "\"]");
      var firstDiv = row && row.querySelector("div");
      label.textContent = row && firstDiv ? (firstDiv.textContent || soundName) : soundName;
    }
  }

  async function loadNotificationSettings() {
    try {
      const resp = await fetch(O.opts.notificationSettingsJsonUrl, { headers: { "X-Requested-With": "fetch" } });
      O.debugLog("Loading notification settings", {
        url: O.opts.notificationSettingsJsonUrl,
        status: resp.status,
        redirected: resp.redirected,
        responseUrl: resp.url,
        contentType: resp.headers.get("content-type")
      });

      if (!resp.ok) {
        const base = O.getMessage("notificationSettingsLoadFailed");
        O.showOrdersWarning("notification-settings-failed", base + " (HTTP " + resp.status + ")");
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

      O.state.notificationSettings = json;
    } catch (error) {
      O.showOrdersWarning("notification-settings-exception", O.getMessage("notificationSettingsLoadException"));
      O.debugWarn("loadNotificationSettings failed", error);
    }
  }

  async function saveNotificationSettings(mode) {
    const form = document.getElementById("notificationSettingsForm");
    const body = document.getElementById("notificationSettingsModalBody");
    if (!form || !body) return;

    try {
      const state = getModalState();
      await O.audio.maybeRequestBrowserNotificationPermission(state);

      const fd = new FormData(form);
      fd.set("NewOrderSoundEnabled", state.newOrderSoundEnabled ? "true" : "false");

      const url = form.action;

      const resp = await fetch(url, { method: "POST", body: fd, headers: { "X-Requested-With": "fetch" } });
      if (!resp.ok) {
        const saveErr = O.getMessage("notificationSettingsSaveFailed");
        showModalWarning(saveErr);
        O.showOrdersWarning("notification-settings-save-http", saveErr + " (HTTP " + resp.status + ")");
        O.debugWarn("Notification settings save failed", resp);
        return;
      }

      const html = await resp.text();
      body.innerHTML = html;

      await loadNotificationSettings();
    } catch (error) {
      const saveErr = O.getMessage("notificationSettingsSaveFailed");
      showModalWarning(saveErr);
      O.showOrdersWarning("notification-settings-save-exception", O.getMessage("notificationSettingsSaveException"));
      O.debugWarn("saveNotificationSettings failed", error);
    }
  }

  async function openNotificationSettingsModal() {
    var resp;
    try {
      resp = await fetch(O.opts.notificationSettingsUrl, { headers: { "X-Requested-With": "fetch" } });
    } catch (error) {
      O.showOrdersWarning("notification-settings-modal-failed", O.getMessage("notificationSettingsModalOpenFailed"));
      O.debugWarn("Notification settings modal fetch failed", error);
      return;
    }

    O.debugLog("Notification settings modal response", { status: resp.status, redirected: resp.redirected, responseUrl: resp.url });
    if (!resp.ok) {
      const base = O.getMessage("notificationSettingsModalOpenFailed");
      O.showOrdersWarning("notification-settings-modal-http", base + " (HTTP " + resp.status + ")");
      O.debugWarn("Notification settings modal HTTP failed", resp);
      return;
    }

    const html = await resp.text();

    const body = document.getElementById("notificationSettingsModalBody");
    if (!body) return;
    body.innerHTML = html;

    if (O.state.notificationSettings) {
      setModalEnabled(!!O.state.notificationSettings.newOrderSoundEnabled);
      const nameSel = document.getElementById("NewOrderSoundName");
      if (nameSel) nameSel.value = O.state.notificationSettings.newOrderSoundName || "bell1";
      const repeatSel = document.getElementById("NewOrderSoundRepeatCount");
      if (repeatSel) repeatSel.value = String(O.state.notificationSettings.newOrderSoundRepeatCount || 3);
      const volSel = document.getElementById("NewOrderSoundVolumePercent");
      if (volSel) volSel.value = String(O.state.notificationSettings.newOrderSoundVolumePercent || 100);
      const showChk = document.getElementById("ShowBrowserNotification");
      if (showChk) showChk.checked = !!O.state.notificationSettings.showBrowserNotification;

      selectNotificationSound(O.state.notificationSettings.newOrderSoundName || "bell1");
    }

    const toggle = document.getElementById("NewOrderSoundEnabledToggle");
    if (toggle) {
      toggle.addEventListener("change", function () {
        setModalEnabled(toggle.checked);
        updateNotificationStatusUi(getModalState());
      });
    }

    const list = document.getElementById("soundOptionsList");
    if (list) {
      list.querySelectorAll(".sound-option").forEach(function (row) {
        row.addEventListener("click", function (e) {
          const isListenBtn = e.target && e.target.classList && e.target.classList.contains("listen-sound-btn");
          const name = row.getAttribute("data-sound-name");
          if (!name) return;

          selectNotificationSound(name);

          if (isListenBtn) {
            e.preventDefault();
            e.stopPropagation();
            const u = row.getAttribute("data-sound-url");
            O.audio.playSoundPreview(name, u);
          }
        });

        const btn = row.querySelector(".listen-sound-btn");
        if (btn) {
          btn.addEventListener("click", function (e) {
            e.preventDefault();
            e.stopPropagation();
            const name = row.getAttribute("data-sound-name");
            const u = row.getAttribute("data-sound-url");
            if (!name) return;
            selectNotificationSound(name);
            O.audio.playSoundPreview(name, u);
          });
        }
      });
    }

    updateNotificationStatusUi(getModalState());

    const testSelectedBtn = document.getElementById("testSelectedSoundBtn");
    if (testSelectedBtn) {
      testSelectedBtn.addEventListener("click", async function () {
        const st = getModalState();
        await O.audio.maybeRequestBrowserNotificationPermission(st);
        const active = document.querySelector(".sound-option.active");
        const url = active && active.getAttribute("data-sound-url");
        await O.audio.playSoundNow(st, url || null);
        localStorage.setItem("orderhub.soundUnlocked", "true");
      });
    }

    const form = document.getElementById("notificationSettingsForm");
    if (form) {
      form.addEventListener("submit", async function (e) {
        e.preventDefault();
        await saveNotificationSettings("save");
        updateNotificationStatusUi(getModalState());
      });
    }
  }

  O.notificationSettings = {
    load: loadNotificationSettings,
    openNotificationSettingsModal: openNotificationSettingsModal,
    getModalState: getModalState,
    setModalEnabled: setModalEnabled,
    updateNotificationStatusUi: updateNotificationStatusUi,
    showModalWarning: showModalWarning,
    selectNotificationSound: selectNotificationSound,
    saveNotificationSettings: saveNotificationSettings
  };
})(window);
