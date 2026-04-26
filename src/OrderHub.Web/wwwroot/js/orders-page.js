// Orders page behavior: filters, polling refresh, new-order notifications.
// Persistent preferences are stored in CustomerDb per user.
// Browser-specific audio unlock is stored in localStorage (orderhub.soundUnlocked).

(function () {
  const defaults = {
    tableUrl: "/orders/table",
    notificationSettingsUrl: "/notification-settings",
    notificationSettingsJsonUrl: "/notification-settings/current",
    pollingIntervalMs: 10000
  };

  const opts = Object.assign({}, defaults, window.orderHubOrdersOptions || {});
  const i18n = Object.assign({
    newOrderArrived: "Yeni sipariş geldi",
    checkOrdersPage: "Siparişler ekranını kontrol edin.",
    soundCouldNotPlay: "Ses çalınamadı. Ses dosyasını veya tarayıcı izinlerini kontrol edin.",
    settingsSaved: "Ayarlar kaydedildi.",
    soundUnlockHint: "Yeni sipariş sesini duymak için önce Bildirim Ayarları içinden bir sesi dinleyin.",
    notificationsOff: "Sesli bildirimler kapalı",
    notificationsOffHelp: "Yeni sipariş sesi kapalı.",
    waitingForAudio: "Tarayıcı izni bekleniyor",
    waitingForAudioHelp: "Sesli bildirimler açık ancak bu tarayıcıda ses çalmak için izin gerekiyor.",
    notificationsOn: "Sesli bildirimler aktif",
    notificationsOnHelp: "Yeni sipariş geldiğinde seçili zil sesi çalacak."
  }, window.orderHubLocalization || {});

  let knownOrderIds = new Set();
  let notificationSettings = null; // loaded from server JSON
  let hintShownForUnlock = false;
  let currentPreviewAudio = null;
  const recentlyNewOrderIds = new Map(); // orderId -> expiresAt (ms)
  const NEW_ORDER_HIGHLIGHT_MS = 30000;

  function showMessage(message, type) {
    const host = document.getElementById("ordersMessageHost");
    if (!host) return;
    const cls = type === "error" ? "alert-danger" : (type === "warning" ? "alert-warning" : "alert-info");
    host.innerHTML = `<div class="alert ${cls} py-2 mb-2">${escapeHtml(message)}</div>`;
    setTimeout(() => { if (host.innerHTML) host.innerHTML = ""; }, 4000);
  }

  function escapeHtml(s) {
    return (s || "").replace(/[&<>"']/g, (c) => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;"
    }[c]));
  }

  function isSoundUnlocked() {
    return localStorage.getItem("orderhub.soundUnlocked") === "true";
  }

  function stopCurrentPreviewSound() {
    try {
      if (!currentPreviewAudio) return;
      currentPreviewAudio.pause();
      currentPreviewAudio.currentTime = 0;
    } catch { }
    currentPreviewAudio = null;
  }

  async function tryUnlockSound() {
    // Unlock attempt must be called from a user gesture handler.
    try {
      const a = new Audio();
      a.volume = 0;
      await a.play();
      localStorage.setItem("orderhub.soundUnlocked", "true");
      return true;
    } catch {
      return false;
    }
  }

  function getModalState() {
    const enabledHidden = document.getElementById("NewOrderSoundEnabled");
    const enabled = enabledHidden ? (enabledHidden.value === "true") : true;

    const name = (document.getElementById("NewOrderSoundName")?.value || "bell1").toLowerCase();
    const repeat = parseInt(document.getElementById("NewOrderSoundRepeatCount")?.value || "3", 10);
    const volPct = parseInt(document.getElementById("NewOrderSoundVolumePercent")?.value || "100", 10);
    const showBrowser = document.getElementById("ShowBrowserNotification")?.checked === true;

    return {
      newOrderSoundEnabled: enabled,
      newOrderSoundName: name,
      newOrderSoundRepeatCount: isNaN(repeat) ? 3 : repeat,
      newOrderSoundVolumePercent: isNaN(volPct) ? 100 : volPct,
      showBrowserNotification: showBrowser
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
      badge.textContent = i18n.notificationsOff;
      help.textContent = i18n.notificationsOffHelp;
      return;
    }

    if (!isSoundUnlocked()) {
      badge.className = "badge text-bg-warning";
      badge.textContent = i18n.waitingForAudio;
      help.textContent = i18n.waitingForAudioHelp;
      return;
    }

    badge.className = "badge text-bg-success";
    badge.textContent = i18n.notificationsOn;
    help.textContent = i18n.notificationsOnHelp;
  }

  function showModalWarning(text) {
    const warn = document.getElementById("notificationStatusWarning");
    if (!warn) return;
    warn.textContent = text;
    warn.classList.remove("d-none");
  }

  function getSoundUrlFromSettings(name) {
    const n = (name || "bell1").toLowerCase();
    const list = notificationSettings?.availableSounds || [];
    const found = list.find(x => (x.name || "").toLowerCase() === n);
    return found?.url || ("/sounds/" + n + ".mp3");
  }

  async function playSoundNow(state, urlOverride) {
    const repeat = Math.max(1, Math.min(3, state.newOrderSoundRepeatCount || 1));
    const pct = state.newOrderSoundVolumePercent;
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const url = urlOverride || getSoundUrlFromSettings(state.newOrderSoundName);

    for (let i = 0; i < repeat; i++) {
      try {
        const audio = new Audio(url);
        audio.volume = vol;
        await new Promise((resolve) => {
          audio.addEventListener("ended", resolve, { once: true });
          audio.addEventListener("error", resolve, { once: true });
          audio.play().catch(() => resolve());
        });
      } catch {
      }
    }

    // if sound file missing or blocked, audio error will resolve. warn once.
  }

  async function playSoundPreview(soundName, soundUrl) {
    stopCurrentPreviewSound();
    const state = getModalState();
    const pct = state.newOrderSoundVolumePercent;
    const vol = Math.max(0, Math.min(1, (pct || 100) / 100.0));
    const url = soundUrl || getSoundUrlFromSettings(soundName);
    try {
      const audio = new Audio(url);
      audio.volume = vol;
      currentPreviewAudio = audio;
      await audio.play();
      localStorage.setItem("orderhub.soundUnlocked", "true");
    } catch {
      stopCurrentPreviewSound();
      showModalWarning(i18n.soundCouldNotPlay);
    }
  }

  async function maybeRequestBrowserNotificationPermission(state) {
    if (!state.showBrowserNotification) return;
    if (!("Notification" in window)) return;
    if (Notification.permission !== "default") return;
    try { await Notification.requestPermission(); } catch { }
  }

  function showBrowserNotificationIfAllowed() {
    try {
      if (!notificationSettings?.showBrowserNotification) return;
      if (!("Notification" in window)) return;
      if (Notification.permission !== "granted") return;
      new Notification(i18n.newOrderArrived, { body: i18n.checkOrdersPage });
    } catch { }
  }

  async function loadNotificationSettings() {
    try {
      const resp = await fetch(opts.notificationSettingsJsonUrl, { headers: { "X-Requested-With": "fetch" } });
      if (!resp.ok) return;
      notificationSettings = await resp.json();
    } catch {
      // ignore
    }
  }

  function extractOrderIdsFromHtml(html) {
    const tmp = document.createElement("div");
    tmp.innerHTML = html;
    const ids = [];
    tmp.querySelectorAll("[data-order-id]").forEach((r) => {
      const id = r.getAttribute("data-order-id");
      if (id) ids.push(id);
    });
    return { ids, tmp };
  }

  function detectNewOrderIds(incomingIds) {
    const newIds = [];
    for (const id of incomingIds) {
      if (!knownOrderIds.has(id)) newIds.push(id);
    }
    return newIds;
  }

  function captureKnownOrderIdsFromContainer() {
    const container = document.getElementById("ordersTableContainer");
    if (!container) return;
    knownOrderIds = new Set();
    container.querySelectorAll("[data-order-id]").forEach((r) => {
      const id = r.getAttribute("data-order-id");
      if (id) knownOrderIds.add(id);
    });
  }

  function markOrdersAsRecentlyNew(orderIds) {
    const expiresAt = Date.now() + NEW_ORDER_HIGHLIGHT_MS;
    orderIds.forEach((id) => recentlyNewOrderIds.set(id, expiresAt));
  }

  function applyNewOrderVisualState() {
    const container = document.getElementById("ordersTableContainer");
    if (!container) return;

    const now = Date.now();
    for (const [id, exp] of Array.from(recentlyNewOrderIds.entries())) {
      if (exp <= now) {
        recentlyNewOrderIds.delete(id);
        continue;
      }

      const row = container.querySelector(`[data-order-id="${id}"]`);
      if (!row) continue;
      row.classList.add("order-row-new");
      row.classList.add("order-row-new-flash");
    }
  }

  function showSpeakerIndicators(orderIds) {
    const container = document.getElementById("ordersTableContainer");
    if (!container) return;
    orderIds.forEach((id) => {
      const el = container.querySelector(`[data-order-speaker="${id}"]`);
      if (!el) return;
      el.classList.remove("d-none");
      el.classList.add("is-playing");
    });
  }

  function hideSpeakerIndicators(orderIds) {
    const container = document.getElementById("ordersTableContainer");
    if (!container) return;
    orderIds.forEach((id) => {
      const el = container.querySelector(`[data-order-speaker="${id}"]`);
      if (!el) return;
      el.classList.remove("is-playing");
      el.classList.add("d-none");
    });
  }

  async function refreshOrdersTable() {
    try {
      const url = new URL(window.location.origin + opts.tableUrl);
      url.search = window.location.search || "";
      url.searchParams.set("_", Date.now().toString());

      const resp = await fetch(url.toString(), { headers: { "X-Requested-With": "fetch" } });
      if (!resp.ok) return;

      const html = await resp.text();
      const { ids } = extractOrderIdsFromHtml(html);
      const newIds = detectNewOrderIds(ids);

      const container = document.getElementById("ordersTableContainer");
      if (!container) return;
      container.innerHTML = html;

      // Update "known" after successful render.
      captureKnownOrderIdsFromContainer();
      applyNewOrderVisualState();

      if (newIds.length > 0) {
        markOrdersAsRecentlyNew(newIds);
        applyNewOrderVisualState();

        if (notificationSettings?.newOrderSoundEnabled) {
          if (!isSoundUnlocked()) {
            if (!hintShownForUnlock) {
              hintShownForUnlock = true;
              showMessage(i18n.soundUnlockHint, "info");
            }
          } else {
            showSpeakerIndicators(newIds);
            try {
              await playSoundNow({
                newOrderSoundEnabled: true,
                newOrderSoundName: notificationSettings.newOrderSoundName,
                newOrderSoundRepeatCount: notificationSettings.newOrderSoundRepeatCount,
                newOrderSoundVolumePercent: notificationSettings.newOrderSoundVolumePercent || Math.round((notificationSettings.newOrderSoundVolume || 1) * 100),
                showBrowserNotification: notificationSettings.showBrowserNotification
              });
            } finally {
              hideSpeakerIndicators(newIds);
            }
          }
        }

        showBrowserNotificationIfAllowed();
      }
    } catch {
      // ignore
    }
  }

  async function openNotificationSettingsModal() {
    const resp = await fetch(opts.notificationSettingsUrl, { headers: { "X-Requested-With": "fetch" } });
    if (!resp.ok) return;
    const html = await resp.text();

    const body = document.getElementById("notificationSettingsModalBody");
    if (!body) return;
    body.innerHTML = html;

    // Seed modal fields from current loaded settings (if available).
    if (notificationSettings) {
      setModalEnabled(!!notificationSettings.newOrderSoundEnabled);
      const nameSel = document.getElementById("NewOrderSoundName");
      if (nameSel) nameSel.value = notificationSettings.newOrderSoundName || "bell1";
      const repeatSel = document.getElementById("NewOrderSoundRepeatCount");
      if (repeatSel) repeatSel.value = String(notificationSettings.newOrderSoundRepeatCount || 3);
      const volSel = document.getElementById("NewOrderSoundVolumePercent");
      if (volSel) volSel.value = String(notificationSettings.newOrderSoundVolumePercent || 100);
      const showChk = document.getElementById("ShowBrowserNotification");
      if (showChk) showChk.checked = !!notificationSettings.showBrowserNotification;

      // select sound in list
      selectNotificationSound(notificationSettings.newOrderSoundName || "bell1");
    }

    // Bind modal behaviors
    const toggle = document.getElementById("NewOrderSoundEnabledToggle");
    if (toggle) {
      toggle.addEventListener("change", function () {
        setModalEnabled(toggle.checked);
        updateNotificationStatusUi(getModalState());
      });
    }

    const list = document.getElementById("soundOptionsList");
    if (list) {
      list.querySelectorAll(".sound-option").forEach((row) => {
        row.addEventListener("click", function (e) {
          const isListenBtn = (e.target && e.target.classList && e.target.classList.contains("listen-sound-btn"));
          const name = row.getAttribute("data-sound-name");
          if (!name) return;

          // select row
          selectNotificationSound(name);

          if (isListenBtn) {
            e.preventDefault();
            e.stopPropagation();
            const url = row.getAttribute("data-sound-url");
            playSoundPreview(name, url);
          }
        });

        const btn = row.querySelector(".listen-sound-btn");
        if (btn) {
          btn.addEventListener("click", function (e) {
            e.preventDefault();
            e.stopPropagation();
            const name = row.getAttribute("data-sound-name");
            const url = row.getAttribute("data-sound-url");
            if (!name) return;
            selectNotificationSound(name);
            playSoundPreview(name, url);
          });
        }
      });
    }

    updateNotificationStatusUi(getModalState());

    const testSelectedBtn = document.getElementById("testSelectedSoundBtn");
    if (testSelectedBtn) {
      testSelectedBtn.addEventListener("click", async function () {
        const state = getModalState();
        await maybeRequestBrowserNotificationPermission(state);
        const url = (document.querySelector(".sound-option.active")?.getAttribute("data-sound-url")) || null;
        await playSoundNow(state, url);
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

  function selectNotificationSound(soundName) {
    const list = document.getElementById("soundOptionsList");
    if (!list) return;
    list.querySelectorAll(".sound-option").forEach((row) => {
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
      const row = list.querySelector(`.sound-option[data-sound-name="${soundName}"]`);
      label.textContent = row ? (row.querySelector("div")?.textContent || soundName) : soundName;
    }
  }

  async function saveNotificationSettings(mode) {
    const form = document.getElementById("notificationSettingsForm");
    const body = document.getElementById("notificationSettingsModalBody");
    if (!form || !body) return;

    try {
      const state = getModalState();
      await maybeRequestBrowserNotificationPermission(state);

      const fd = new FormData(form);
      // ensure hidden enabled is in sync
      fd.set("NewOrderSoundEnabled", state.newOrderSoundEnabled ? "true" : "false");

      let url = form.action;

      const resp = await fetch(url, { method: "POST", body: fd, headers: { "X-Requested-With": "fetch" } });
      if (!resp.ok) {
        showModalWarning("Kaydetme sırasında bir hata oluştu.");
        return;
      }

      const html = await resp.text();
      body.innerHTML = html;

      // Refresh in-memory settings from server JSON so polling works even if modal closes.
      await loadNotificationSettings();
    } catch {
      showModalWarning("Kaydetme sırasında bir hata oluştu.");
    }
  }

  function initFilters() {
    const platformSelect = document.getElementById("platformSelect");
    if (platformSelect) {
      platformSelect.addEventListener("change", function () {
        const url = new URL(window.location.href);
        if (platformSelect.value) url.searchParams.set("platform", platformSelect.value);
        else url.searchParams.delete("platform");
        url.searchParams.set("page", "1");
        window.location.href = url.toString();
      });
    }

    const statusSelect = document.getElementById("statusSelect");
    if (statusSelect) {
      statusSelect.addEventListener("change", function () {
        const url = new URL(window.location.href);
        if (statusSelect.value) url.searchParams.set("status", statusSelect.value);
        else url.searchParams.delete("status");
        url.searchParams.set("page", "1");
        window.location.href = url.toString();
      });
    }
  }

  function initPolling() {
    setInterval(refreshOrdersTable, opts.pollingIntervalMs);
  }

  async function initOrdersPage() {
    initFilters();
    captureKnownOrderIdsFromContainer(); // initial: no sound
    await loadNotificationSettings();    // independent of modal
    initPolling();

    const btn = document.getElementById("notificationSettingsBtn");
    if (btn) {
      btn.addEventListener("click", async function () {
        await openNotificationSettingsModal();
        const modalEl = document.getElementById("notificationSettingsModal");
        if (!modalEl) return;
        const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
        modal.show();
      });
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    initOrdersPage().catch(() => { });
  });
})();

