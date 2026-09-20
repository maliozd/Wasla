// Page-level notification sound select / test / persist for Orders and Live Display.
(function (global) {
  "use strict";

  const O = global.OrderHubOrders;
  if (!O || !O.audio) {
    return;
  }

  const OPERATOR_SOUND_KEY = "Wasla.operatorSoundName";
  const ALLOWED = ["bell1", "bell2", "bell3", "bell4", "bell5", "bell6"];

  function normalizeSoundName(name) {
    const n = String(name || "bell1").toLowerCase();
    return ALLOWED.indexOf(n) >= 0 ? n : "bell1";
  }

  function readOperatorSoundName() {
    try {
      const stored = localStorage.getItem(OPERATOR_SOUND_KEY);
      if (stored) {
        return normalizeSoundName(stored);
      }
    } catch (error) {
      /* ignore */
    }
    return null;
  }

  function writeOperatorSoundName(name) {
    const n = normalizeSoundName(name);
    try {
      localStorage.setItem(OPERATOR_SOUND_KEY, n);
    } catch (error) {
      /* ignore */
    }
    return n;
  }

  function getSelectElements() {
    return [
      document.getElementById("ordersSoundSelect"),
      document.getElementById("ordersLiveDisplaySoundSelect")
    ].filter(Boolean);
  }

  function getSelectedSoundName() {
    const selects = getSelectElements();
    for (let i = 0; i < selects.length; i++) {
      if (selects[i].value) {
        return normalizeSoundName(selects[i].value);
      }
    }
    const operator = readOperatorSoundName();
    if (operator) {
      return operator;
    }
    const st = O.state.notificationSettings || {};
    return normalizeSoundName(st.newOrderSoundName);
  }

  function applySelectedSoundToState(name) {
    const n = writeOperatorSoundName(name);
    if (!O.state.notificationSettings) {
      O.state.notificationSettings = O.notificationSettings && typeof O.notificationSettings.mergeDefaultNotificationState === "function"
        ? O.notificationSettings.mergeDefaultNotificationState({})
        : { newOrderSoundEnabled: true, newOrderSoundName: n, newOrderSoundRepeatCount: 1, newOrderSoundVolumePercent: 100 };
    }
    O.state.notificationSettings.newOrderSoundName = n;
    O.state.notificationSettings.newOrderSoundEnabled = true;
    return n;
  }

  function populateSelect(select, sounds, selectedName) {
    if (!select) {
      return;
    }
    const list = Array.isArray(sounds) && sounds.length
      ? sounds
      : ALLOWED.map(function (name) {
          return { name: name, label: "Bell " + name.replace("bell", ""), url: "/sounds/" + name + ".mp3" };
        });

    const current = normalizeSoundName(selectedName);
    select.innerHTML = "";
    list.forEach(function (s) {
      const name = normalizeSoundName(s.name || s.Name);
      const opt = document.createElement("option");
      opt.value = name;
      opt.textContent = s.label || s.Label || ("Bell " + name.replace("bell", ""));
      if (s.url || s.Url) {
        opt.setAttribute("data-sound-url", s.url || s.Url);
      } else {
        opt.setAttribute("data-sound-url", "/sounds/" + name + ".mp3");
      }
      if (name === current) {
        opt.selected = true;
      }
      select.appendChild(opt);
    });
    select.value = current;
  }

  function syncSelectsFromState() {
    const st = O.state.notificationSettings || {};
    const operator = readOperatorSoundName();
    const selected = operator || normalizeSoundName(st.newOrderSoundName);
    if (operator) {
      st.newOrderSoundName = operator;
      O.state.notificationSettings = st;
    }
    const sounds = st.availableSounds || [];
    getSelectElements().forEach(function (select) {
      populateSelect(select, sounds, selected);
    });
  }

  function getAntiforgeryForm() {
    return document.getElementById("ordersOperatorSoundForm")
      || document.getElementById("ordersLiveDisplayCsrfForm");
  }

  async function persistSoundName(name) {
    const n = applySelectedSoundToState(name);
    const form = getAntiforgeryForm();
    if (!form) {
      return n;
    }

    const tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    if (!tokenInput || !tokenInput.value) {
      return n;
    }

    const st = O.state.notificationSettings || {};
    const fd = new FormData();
    fd.set("__RequestVerificationToken", tokenInput.value);
    fd.set("NewOrderSoundEnabled", st.newOrderSoundEnabled !== false ? "true" : "false");
    fd.set("NewOrderSoundName", n);
    fd.set("NewOrderSoundRepeatCount", String(st.newOrderSoundRepeatCount != null ? st.newOrderSoundRepeatCount : 1));
    fd.set("NewOrderSoundVolumePercent", String(st.newOrderSoundVolumePercent != null ? st.newOrderSoundVolumePercent : 100));
    fd.set("ShowBrowserNotification", st.showBrowserNotification ? "true" : "false");
    fd.set("NewOrderHighlightColor", st.newOrderHighlightColor || "yellow");
    fd.set("NewOrderHighlightBehavior", st.newOrderHighlightBehavior || "fade");
    fd.set("NewOrderHighlightDurationSeconds", String(st.newOrderHighlightDurationSeconds != null ? st.newOrderHighlightDurationSeconds : 30));

    const url = O.opts.notificationSettingsUrl || "/notification-settings";
    try {
      const resp = await fetch(url, {
        method: "POST",
        body: fd,
        headers: { "X-Requested-With": "fetch" }
      });
      if (!resp.ok) {
        O.debugWarn("Operator sound persist failed", { status: resp.status });
      }
    } catch (error) {
      O.debugWarn("Operator sound persist exception", error);
    }
    return n;
  }

  async function testSelectedSound() {
    const select = document.getElementById("ordersSoundSelect")
      || document.getElementById("ordersLiveDisplaySoundSelect");
    const name = getSelectedSoundName();
    applySelectedSoundToState(name);
    const opt = select && select.options[select.selectedIndex];
    const url = (opt && opt.getAttribute("data-sound-url")) || O.audio.getSoundUrlFromSettings(name);

    if (typeof O.audio.stopCurrentSound === "function") {
      O.audio.stopCurrentSound();
    }
    if (typeof O.audio.playSoundPreview === "function") {
      await O.audio.playSoundPreview(name, url);
    }
  }

  function wireControls() {
    getSelectElements().forEach(function (select) {
      if (select.dataset.soundControlWired === "1") {
        return;
      }
      select.dataset.soundControlWired = "1";
      select.addEventListener("change", function () {
        const name = normalizeSoundName(select.value);
        getSelectElements().forEach(function (other) {
          if (other !== select) {
            other.value = name;
          }
        });
        persistSoundName(name).catch(function (error) {
          O.debugWarn("persistSoundName failed", error);
        });
      });
    });

    const testBtn = document.getElementById("ordersSoundTestBtn")
      || document.getElementById("ordersLiveDisplaySoundTestBtn");
    if (testBtn && testBtn.dataset.soundControlWired !== "1") {
      testBtn.dataset.soundControlWired = "1";
      testBtn.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        testSelectedSound().catch(function (error) {
          O.debugWarn("testSelectedSound failed", error);
        });
      });
    }
  }

  function initSoundControl() {
    wireControls();
    syncSelectsFromState();
  }

  O.soundControl = {
    init: initSoundControl,
    syncSelectsFromState: syncSelectsFromState,
    getSelectedSoundName: getSelectedSoundName,
    applySelectedSoundToState: applySelectedSoundToState,
    persistSoundName: persistSoundName,
    testSelectedSound: testSelectedSound,
    OPERATOR_SOUND_KEY: OPERATOR_SOUND_KEY
  };
})(window);
