// Report page: applies filters in place, keeps them in the URL, and drives the charts.
// The server renders every figure and state; nothing here calculates a report value.
(function (global) {
  "use strict";

  const DEFAULT_PRESET = "last30days";
  const DEFAULT_SECTION = "overview";
  const REQUEST_TIMEOUT_MS = 30000;
  const SKELETON_DELAY_MS = 120;
  const SHEET_QUERY = "(max-width: 767.98px)";
  const STORAGE_KEY = "Wasla.reports.query";
  const EXPORT_TIMEOUT_MS = 90000;
  const EXPORTABLE_STATES = ["Report", "NoOrders"];
  const FILTER_PARAMETERS = ["preset", "startDate", "endDate", "platform"];

  function isPreset(state, name) {
    return state.preset.toLowerCase() === name;
  }

  /**
   * The page state from a query string, with the page's defaults (ReportPageQuery on the server). Whether a value is
   * valid, and its canonical form, is the server's to say: the page takes the state of every answer (data-query).
   */
  function readState(search) {
    const params = new URLSearchParams(search);
    const startDate = params.get("startDate") || "";
    const endDate = params.get("endDate") || "";
    const state = {
      preset: params.get("preset") || (startDate || endDate ? "custom" : DEFAULT_PRESET),
      startDate,
      endDate,
      platform: params.get("platform") || "",
      compare: params.get("compare") === "1",
      section: params.get("section") || DEFAULT_SECTION
    };
    FILTER_PARAMETERS.forEach(function (name) {
      const values = params.getAll(name);
      if (values.length > 1) {
        state.repeated = state.repeated || {};
        state.repeated[name] = values;
      }
    });
    return state;
  }

  /** The query string of a state, defaults left out, in the order the server writes it. A refused filter stays as submitted. */
  function toQuery(state) {
    const params = new URLSearchParams();
    const repeated = state.repeated || {};
    function filter(name, value, keepEmpty) {
      if (repeated[name]) repeated[name].forEach(function (item) { params.append(name, item); });
      else if (value || keepEmpty) params.set(name, value || "");
    }

    const custom = isPreset(state, "custom");
    filter("preset", isPreset(state, DEFAULT_PRESET) ? "" : state.preset);
    filter("startDate", state.startDate, custom);
    filter("endDate", state.endDate, custom);
    filter("platform", state.platform);
    if (state.compare) params.set("compare", "1");
    if (state.section !== DEFAULT_SECTION) params.set("section", state.section);
    const query = params.toString();
    return query ? "?" + query : "";
  }

  function isDefaultFilter(state) {
    return isPreset(state, DEFAULT_PRESET) && !state.platform && !state.compare && !state.repeated;
  }

  /** The state with the given values; a choice replaces any repeated values of that filter. */
  function withChanges(state, changes) {
    const next = Object.assign({}, state, changes);
    if (state.repeated) {
      const kept = Object.keys(state.repeated).filter(function (name) { return !(name in changes); });
      delete next.repeated;
      kept.forEach(function (name) {
        next.repeated = next.repeated || {};
        next.repeated[name] = state.repeated[name];
      });
    }
    return next;
  }

  /**
   * Loads one result at a time. A newer load aborts the older one, and an answer that arrives anyway is dropped, so a
   * slow response can never replace a newer selection.
   */
  function createLoader(options) {
    const fetchImpl = options.fetch;
    const timeoutMs = options.timeoutMs || REQUEST_TIMEOUT_MS;
    let sequence = 0;
    let controller = null;

    async function load(url) {
      const id = ++sequence;
      if (controller) controller.abort();
      const own = new AbortController();
      controller = own;
      let timedOut = false;
      const timer = setTimeout(function () {
        timedOut = true;
        own.abort();
      }, timeoutMs);
      options.onStart(id);
      try {
        const response = await fetchImpl(url, {
          credentials: "same-origin",
          headers: { "X-Requested-With": "XMLHttpRequest" },
          signal: own.signal
        });
        if (id !== sequence) return "stale";
        if (response.redirected || response.status === 401 || response.status === 403) {
          options.onSignedOut();
          return "signed-out";
        }
        if (response.ok || response.status === 400) {
          const html = await response.text();
          if (id !== sequence) return "stale";
          options.onResult(html, response.status);
          return response.status === 400 ? "invalid" : "ok";
        }
        options.onFailure();
        return "failed";
      } catch (error) {
        if (id !== sequence || (own.signal.aborted && !timedOut)) return "stale";
        options.onFailure();
        return "failed";
      } finally {
        clearTimeout(timer);
      }
    }

    return { load };
  }

  /** The export URL's query of a state: its filter only (section and comparison do not apply to a file) and the format. */
  function exportQuery(state, format) {
    const query = toQuery(Object.assign({}, state, { compare: false, section: DEFAULT_SECTION }));
    return (query ? query + "&" : "?") + "format=" + encodeURIComponent(format);
  }

  /**
   * Closes the export menu through Bootstrap's dropdown API, then marks its toggle busy. The order matters: Bootstrap 5.3
   * leaves a dropdown open when its toggle has the `disabled` class, both in `hide()` and in the document click handler
   * that closes menus after an item is chosen (that handler runs after this page's own click handler). Without Bootstrap
   * (its CDN did not load) there is no open menu to close.
   */
  function closeMenuThenMarkBusy(toggle, bootstrap) {
    const dropdown = bootstrap && bootstrap.Dropdown ? bootstrap.Dropdown.getInstance(toggle) : null;
    if (dropdown) dropdown.hide();
    toggle.classList.add("disabled");
    toggle.setAttribute("aria-disabled", "true");
    toggle.setAttribute("aria-busy", "true");
  }

  /** The file name the server chose (ASCII, ReportExportFormats.FileName), or a plain one when it is anything else. */
  function exportFileName(disposition, format) {
    const match = /filename="?([^";]*)"?/.exec(disposition || "");
    const name = match ? match[1].trim() : "";
    return /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name) ? name : "wasla-orders-report." + format;
  }

  /**
   * Creates one export file at a time. A second request while one runs is refused here, before any request is sent; the
   * server refuses a second one per user as well. The server's own text is shown for a refused filter (400) and a refused
   * concurrent export (429); anything else is a plain failure, and a lost session reloads the page.
   */
  function createExporter(options) {
    const fetchImpl = options.fetch;
    const timeoutMs = options.timeoutMs || EXPORT_TIMEOUT_MS;
    let running = false;

    async function run(url, format) {
      if (running) return "running";
      running = true;
      const controller = new AbortController();
      const timer = setTimeout(function () { controller.abort(); }, timeoutMs);
      options.onStart(format);
      try {
        const response = await fetchImpl(url, {
          credentials: "same-origin",
          headers: { "X-Requested-With": "XMLHttpRequest" },
          signal: controller.signal
        });
        if (response.redirected || response.status === 401 || response.status === 403) {
          options.onSignedOut();
          return "signed-out";
        }
        if (response.ok) {
          const blob = await response.blob();
          options.onFile(blob, exportFileName(response.headers.get("Content-Disposition"), format), format);
          return "ok";
        }
        const message = response.status === 400 || response.status === 429 ? (await response.text()).trim() : "";
        options.onFailure(format, message);
        return "failed";
      } catch (_) {
        options.onFailure(format, "");
        return "failed";
      } finally {
        clearTimeout(timer);
        running = false;
        options.onEnd(format);
      }
    }

    return {
      run,
      isRunning: function () { return running; }
    };
  }

  /** The column to move to from a key press, or null. Arrow keys follow the screen, so right-to-left swaps them. */
  function nextColumnIndex(key, index, count, isRtl) {
    const forward = isRtl ? "ArrowLeft" : "ArrowRight";
    const backward = isRtl ? "ArrowRight" : "ArrowLeft";
    if (key === forward) return Math.min(index + 1, count - 1);
    if (key === backward) return Math.max(index - 1, 0);
    if (key === "Home") return 0;
    if (key === "End") return count - 1;
    return null;
  }

  function init(document) {
    const root = document.getElementById("rptRoot");
    if (!root) return;

    const result = document.getElementById("rptResult");
    const period = document.getElementById("rptPeriod");
    const status = document.getElementById("rptStatus");
    const sheetElement = document.getElementById("rptFilterSheet");
    const customForm = document.getElementById("rptCustomForm");
    const customToggle = document.getElementById("rptCustomToggle");
    const startInput = document.getElementById("rptStartDate");
    const endInput = document.getElementById("rptEndDate");
    const startError = document.getElementById("rptStartError");
    const endError = document.getElementById("rptEndError");
    const platformSelect = document.getElementById("rptPlatform");
    const compareInput = document.getElementById("rptCompare");
    const compareWrap = document.getElementById("rptCompareWrap");
    const resetLink = document.getElementById("rptReset");
    const pageUrl = root.dataset.pageUrl;
    const contentUrl = root.dataset.contentUrl;
    const viewModes = new Map();

    const firstAnswer = result.querySelector("[data-rpt-result-root]");
    const openedBare = !global.location.search;

    let applied = readState(firstAnswer.dataset.query);
    let draft = null;
    let skeletonTimer = null;

    // Export (WAS-184): the file of the report the page shows, offered once a report has loaded for an accepted filter.
    const exportRoot = document.getElementById("rptExport");
    const exportToggle = document.getElementById("rptExportToggle");
    const exportMessage = document.getElementById("rptExportMessage");
    const exportSpinner = exportRoot.querySelector(".rpt-export__spinner");
    let exportState = firstAnswer.dataset.state;

    function exportLabel(format) {
      const item = exportRoot.querySelector('[data-rpt-export="' + format + '"]');
      return item ? item.dataset.formatLabel : format.toUpperCase();
    }

    function setExportMessage(text, tone) {
      exportMessage.textContent = text || "";
      if (tone) exportMessage.dataset.tone = tone;
      else delete exportMessage.dataset.tone;
    }

    const exporter = createExporter({
      fetch: function (url, init) {
        return global.fetch(url, init);
      },
      onStart: function (format) {
        // The menu closes and cannot open again until this file is done; focus moves from the chosen item to the button.
        const hadFocus = exportRoot.contains(document.activeElement);
        closeMenuThenMarkBusy(exportToggle, global.bootstrap);
        exportSpinner.hidden = false;
        if (hadFocus) exportToggle.focus({ preventScroll: true });
        setExportMessage(exportRoot.dataset.preparingText.replace("{0}", exportLabel(format)), "");
      },
      onFile: function (blob, name, format) {
        const url = global.URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = name;
        link.hidden = true;
        document.body.appendChild(link);
        link.click();
        link.remove();
        global.setTimeout(function () {
          global.URL.revokeObjectURL(url);
        }, 60000);
        setExportMessage(exportRoot.dataset.readyText.replace("{0}", exportLabel(format)), "success");
      },
      onFailure: function (_, message) {
        setExportMessage(message || exportRoot.dataset.failedText, "danger");
      },
      onSignedOut: function () {
        global.location.reload();
      },
      onEnd: function () {
        exportToggle.classList.remove("disabled");
        exportToggle.removeAttribute("aria-disabled");
        exportToggle.removeAttribute("aria-busy");
        exportSpinner.hidden = true;
        syncExport();
      }
    });

    /** Points the export at the applied filter and offers it only for a loaded report. */
    function syncExport() {
      const available = EXPORTABLE_STATES.indexOf(exportState) >= 0;
      // While a file is being made the button keeps focus (it is marked disabled, not disabled); onEnd syncs again.
      exportToggle.disabled = !available && !exporter.isRunning();
      if (available) exportToggle.removeAttribute("title");
      else exportToggle.title = exportRoot.dataset.unavailableText;
      exportRoot.querySelectorAll("[data-rpt-export]").forEach(function (link) {
        link.href = exportRoot.dataset.exportUrl + exportQuery(applied, link.dataset.rptExport);
      });
    }

    exportRoot.addEventListener("click", function (event) {
      const link = event.target.closest("a[data-rpt-export]");
      if (!link || event.metaKey || event.ctrlKey || event.shiftKey) return;
      event.preventDefault();
      exporter.run(link.href, link.dataset.rptExport);
    });

    const sheetQuery = global.matchMedia(SHEET_QUERY);
    function isSheetMode() {
      return sheetQuery.matches;
    }

    function sheet() {
      return global.bootstrap && global.bootstrap.Offcanvas ? global.bootstrap.Offcanvas.getOrCreateInstance(sheetElement) : null;
    }

    function remember() {
      try {
        global.sessionStorage.setItem(STORAGE_KEY, global.location.search);
      } catch (_) {
        /* storage may be unavailable; the URL still holds the filter */
      }
    }

    function remembered() {
      try {
        return global.sessionStorage.getItem(STORAGE_KEY) || "";
      } catch (_) {
        return "";
      }
    }

    function setCustomOpen(open) {
      customForm.hidden = !open;
      customToggle.setAttribute("aria-expanded", open ? "true" : "false");
    }

    function setFieldError(input, slot, message) {
      slot.textContent = message || "";
      if (message) input.setAttribute("aria-invalid", "true");
      else input.removeAttribute("aria-invalid");
    }

    /** Puts every persistent control in the given state; they are not re-rendered, so focus and open menus survive. */
    function syncControls(state) {
      root.querySelectorAll("[data-rpt-preset]").forEach(function (link) {
        const selected = state.preset === link.dataset.rptPreset;
        link.classList.toggle("is-selected", selected);
        if (selected) link.setAttribute("aria-current", "true");
        else link.removeAttribute("aria-current");
        link.href = pageUrl + toQuery(withChanges(state, { preset: link.dataset.rptPreset, startDate: "", endDate: "" }));
      });
      customToggle.classList.toggle("is-selected", isPreset(state, "custom"));
      if (isPreset(state, "custom")) {
        startInput.value = state.startDate;
        endInput.value = state.endDate;
      }

      platformSelect.value = state.platform;
      if (platformSelect.selectedIndex < 0) platformSelect.value = "";
      compareInput.checked = state.compare;
      compareWrap.hidden = state.section !== DEFAULT_SECTION;

      resetLink.hidden = isDefaultFilter(state);
      resetLink.href = pageUrl + toQuery(resetState(state));

      root.querySelectorAll("[data-rpt-section]").forEach(function (link) {
        const current = state.section === link.dataset.rptSection;
        link.classList.toggle("is-current", current);
        if (current) link.setAttribute("aria-current", "page");
        else link.removeAttribute("aria-current");
        link.href = pageUrl + toQuery(Object.assign({}, state, { section: link.dataset.rptSection }));
      });
    }

    function resetState(state) {
      return withChanges(state, { preset: DEFAULT_PRESET, startDate: "", endDate: "", platform: "", compare: false });
    }

    /** Takes the state the server answered with: what ran, in its canonical form, is what the URL and controls show. */
    function adopt(answer) {
      const query = answer.dataset.query;
      applied = readState(query);
      if (global.location.search !== query) global.history.replaceState(null, "", pageUrl + query);
      syncControls(draft || applied);
      if (!draft) compareInput.checked = answer.dataset.compareActive === "true";
      exportState = answer.dataset.state;
      syncExport();
    }

    function applyViewModes(scope) {
      scope.querySelectorAll("[data-rpt-panel]").forEach(function (panel) {
        setPanelView(panel, viewModes.get(panel.dataset.rptPanel) || "chart");
      });
    }

    function setPanelView(panel, view) {
      panel.querySelector("[data-rpt-panel-chart]").hidden = view !== "chart";
      panel.querySelector("[data-rpt-panel-table]").hidden = view !== "table";
      panel.querySelectorAll("[data-rpt-view]").forEach(function (button) {
        button.setAttribute("aria-pressed", button.dataset.rptView === view ? "true" : "false");
      });
    }

    function replaceResult(content) {
      const hadFocus = result.contains(document.activeElement) && document.activeElement !== result;
      result.replaceChildren(content);
      if (hadFocus) result.focus({ preventScroll: true });
    }

    function clearSkeletonTimer() {
      if (skeletonTimer !== null) {
        clearTimeout(skeletonTimer);
        skeletonTimer = null;
      }
    }

    const loader = createLoader({
      fetch: function (url, init) {
        return global.fetch(url, init);
      },
      onStart: function () {
        clearSkeletonTimer();
        result.setAttribute("aria-busy", "true");
        status.textContent = root.dataset.loadingText;
        exportState = "Loading";
        syncExport();
        skeletonTimer = setTimeout(function () {
          skeletonTimer = null;
          replaceResult(document.getElementById("rptLoadingTemplate").content.cloneNode(true));
        }, SKELETON_DELAY_MS);
      },
      onResult: function (html, statusCode) {
        clearSkeletonTimer();
        const holder = document.createElement("template");
        holder.innerHTML = html;
        const periodSource = holder.content.querySelector("[data-rpt-period-source]");
        const answer = holder.content.querySelector("[data-rpt-result-root]");
        period.replaceChildren(periodSource.content.cloneNode(true));
        periodSource.remove();

        setFieldError(startInput, startError, answer.dataset.startError);
        setFieldError(endInput, endError, answer.dataset.endError);
        if (answer.dataset.startError || answer.dataset.endError) setCustomOpen(true);

        adopt(answer);
        replaceResult(holder.content);
        applyViewModes(result);
        result.setAttribute("aria-busy", "false");
        status.textContent = answer.dataset.summary || "";
        if (statusCode !== 400) remember();
      },
      onFailure: function () {
        clearSkeletonTimer();
        replaceResult(document.getElementById("rptFailureTemplate").content.cloneNode(true));
        result.setAttribute("aria-busy", "false");
        status.textContent = "";
        exportState = "Failed";
        syncExport();
      },
      onSignedOut: function () {
        global.location.reload();
      }
    });

    function urlFor(state) {
      return contentUrl + toQuery(state);
    }

    function fetchApplied() {
      return loader.load(urlFor(applied));
    }

    function navigate(state, options) {
      applied = state;
      draft = null;
      syncControls(state);
      if (!options || !options.fromHistory) {
        const target = pageUrl + toQuery(state);
        global.history.pushState(null, "", target);
      }
      return fetchApplied();
    }

    /** A filter choice: applied at once in the card, held until Apply in the bottom sheet. */
    function choose(next) {
      if (isSheetMode()) {
        draft = next;
        syncControls(next);
        return;
      }
      navigate(next);
    }

    function current() {
      return draft || applied;
    }

    function withCustomRange(state) {
      return withChanges(state, { preset: "custom", startDate: startInput.value, endDate: endInput.value });
    }

    function applyDraft() {
      let next = current();
      if (!customForm.hidden) next = withCustomRange(next);
      navigate(next).then(function (outcome) {
        if (outcome !== "invalid") {
          const offcanvas = sheet();
          if (offcanvas) offcanvas.hide();
        }
      });
    }

    root.addEventListener("click", function (event) {
      const link = event.target.closest("a[data-rpt-link], a[data-rpt-section]");
      if (link && !event.metaKey && !event.ctrlKey && !event.shiftKey) {
        event.preventDefault();
        navigate(readState(new URL(link.href).search));
        return;
      }

      const presetLink = event.target.closest("a[data-rpt-preset]");
      if (presetLink && !event.metaKey && !event.ctrlKey && !event.shiftKey) {
        event.preventDefault();
        setCustomOpen(false);
        choose(withChanges(current(), { preset: presetLink.dataset.rptPreset, startDate: "", endDate: "" }));
        return;
      }

      if (event.target.closest("[data-rpt-retry]")) {
        fetchApplied();
        return;
      }

      const viewButton = event.target.closest("[data-rpt-view]");
      if (viewButton) {
        const panel = viewButton.closest("[data-rpt-panel]");
        viewModes.set(panel.dataset.rptPanel, viewButton.dataset.rptView);
        setPanelView(panel, viewButton.dataset.rptView);
      }
    });

    resetLink.addEventListener("click", function (event) {
      event.preventDefault();
      setCustomOpen(false);
      navigate(readState(new URL(resetLink.href).search));
    });

    customToggle.addEventListener("click", function () {
      setCustomOpen(customForm.hidden);
      if (!customForm.hidden) startInput.focus();
    });

    document.getElementById("rptCustomCancel").addEventListener("click", function () {
      setCustomOpen(false);
      startInput.value = isPreset(applied, "custom") ? applied.startDate : "";
      endInput.value = isPreset(applied, "custom") ? applied.endDate : "";
      setFieldError(startInput, startError, "");
      setFieldError(endInput, endError, "");
      customToggle.focus();
    });

    customForm.addEventListener("submit", function (event) {
      event.preventDefault();
      if (isSheetMode()) {
        applyDraft();
        return;
      }
      navigate(withCustomRange(applied)).then(function (outcome) {
        if (outcome !== "invalid") setCustomOpen(false);
      });
    });

    document.getElementById("rptFilterForm").addEventListener("submit", function (event) {
      event.preventDefault();
    });

    platformSelect.addEventListener("change", function () {
      choose(withChanges(current(), { platform: platformSelect.value }));
    });

    compareInput.addEventListener("change", function () {
      choose(withChanges(current(), { compare: compareInput.checked }));
    });

    document.getElementById("rptSheetApply").addEventListener("click", applyDraft);

    document.getElementById("rptSheetReset").addEventListener("click", function () {
      setCustomOpen(false);
      navigate(resetState(applied)).then(function () {
        const offcanvas = sheet();
        if (offcanvas) offcanvas.hide();
      });
    });

    sheetElement.addEventListener("hidden.bs.offcanvas", function () {
      if (draft) {
        draft = null;
        syncControls(applied);
      }
    });

    global.addEventListener("popstate", function () {
      navigate(readState(global.location.search), { fromHistory: true });
    });

    initCharts(root, document);

    if (openedBare && remembered()) {
      // Back from another page through the sidebar: bring the last filter back.
      global.history.replaceState(null, "", pageUrl + remembered());
      navigate(readState(global.location.search), { fromHistory: true });
    } else {
      adopt(firstAnswer);
      if (firstAnswer.dataset.state !== "InvalidFilter") remember();
    }
  }

  function initCharts(root, document) {
    let tip = null;
    let pointerAt = "";

    function hideTip() {
      if (tip) {
        tip.remove();
        tip = null;
      }
    }

    function showTip(column) {
      hideTip();
      const chart = column.closest("[data-rpt-chart]");
      const plot = chart.querySelector(".rpt-chart__plot");
      const element = document.createElement("div");
      element.className = "rpt-tip";
      element.setAttribute("aria-hidden", "true");

      const title = document.createElement("p");
      title.className = "rpt-tip__title";
      title.textContent = column.dataset.title;
      element.appendChild(title);
      JSON.parse(column.dataset.rows).forEach(function (row) {
        const line = document.createElement("p");
        line.className = "rpt-tip__row";
        const label = document.createElement("span");
        label.textContent = row[0];
        const value = document.createElement("strong");
        value.textContent = row[1];
        line.append(label, value);
        element.appendChild(line);
      });
      plot.appendChild(element);
      tip = element;

      const plotBox = plot.getBoundingClientRect();
      const columnBox = column.getBoundingClientRect();
      const left = Math.max(0, Math.min(columnBox.left - plotBox.left + columnBox.width / 2 - element.offsetWidth / 2, plotBox.width - element.offsetWidth));
      element.style.left = left + "px";
      element.style.top = "0";
    }

    function pointerPosition(event) {
      return event.clientX + "," + event.clientY;
    }

    root.addEventListener("pointermove", function (event) {
      pointerAt = pointerPosition(event);
    });

    // Browsers repeat pointerover when the page changes under a resting pointer; that must not take the values from the day chosen by keyboard.
    root.addEventListener("pointerover", function (event) {
      const moved = pointerPosition(event) !== pointerAt;
      pointerAt = pointerPosition(event);
      const column = event.target.closest("[data-rpt-col]");
      if (column && moved) showTip(column);
    });

    root.addEventListener("pointerout", function (event) {
      const chart = event.target.closest("[data-rpt-chart]");
      if (chart && !chart.contains(event.relatedTarget) && !chart.contains(document.activeElement)) hideTip();
    });

    root.addEventListener("focusin", function (event) {
      const column = event.target.closest("[data-rpt-col]");
      if (column) showTip(column);
    });

    root.addEventListener("focusout", function (event) {
      if (event.target.closest("[data-rpt-col]")) hideTip();
    });

    root.addEventListener("click", function (event) {
      const column = event.target.closest("[data-rpt-col]");
      if (column) showTip(column);
    });

    root.addEventListener("keydown", function (event) {
      if (event.key === "Escape") {
        hideTip();
        return;
      }
      const column = event.target.closest("[data-rpt-col]");
      if (!column) return;
      const columns = Array.from(column.parentElement.querySelectorAll("[data-rpt-col]"));
      const isRtl = global.getComputedStyle(column.parentElement).direction === "rtl";
      const index = nextColumnIndex(event.key, columns.indexOf(column), columns.length, isRtl);
      if (index === null) return;
      event.preventDefault();
      columns.forEach(function (item, i) {
        item.tabIndex = i === index ? 0 : -1;
      });
      columns[index].focus();
      showTip(columns[index]);
    });
  }

  if (typeof module === "object" && module.exports) {
    module.exports = { readState, toQuery, isDefaultFilter, withChanges, createLoader, nextColumnIndex, exportQuery, exportFileName, createExporter, closeMenuThenMarkBusy };
  } else {
    global.document.addEventListener("DOMContentLoaded", function () {
      init(global.document);
    });
  }
})(typeof window !== "undefined" ? window : globalThis);
