// A searchable address field for the signup page: a text input with a list that opens under it (WAI-ARIA combobox
// with list autocomplete, the options reached with aria-activedescendant, so focus stays in the input).
//
// Two modes:
// - "select" (city, district): a choice from the list is required. The page's native <select> stays in the form,
//   visually hidden, and keeps the value, the validation and the events the page already listens to; this control
//   shows and changes it. Typing only searches; Escape, Tab or leaving the field puts the chosen name back.
// - "suggest" (neighbourhood, street): the input itself is the value. The list only suggests catalog names; whatever
//   the visitor types is kept, and the list offers to use it when the catalog does not have it.
//
// The list never picks on its own when focus merely moves (Tab, click elsewhere). Enter picks the active option and
// does not continue the step; with no active option a suggest field just closes its list.
(function (root, factory) {
    var api = factory(root.WaslaAddressSearch || (typeof require === "function" ? require("./signup-address-search.js") : null));
    if (typeof module === "object" && module.exports) {
        module.exports = api;
    } else {
        root.WaslaCombobox = api;
    }
})(typeof self !== "undefined" ? self : this, function (Search) {
    "use strict";

    var ROW_LIMIT = 6.5;

    // The input searches this control's own list, so the browser's address suggestions must not open over it. Chrome
    // (checked in 157 with a saved address) ignores autocomplete="off" on fields it reads as address fields: its
    // "Manage addresses…" popup opened on the city and street inputs beside the list. A token it does not recognise
    // keeps that popup closed, and filling a saved address from another field (the postal code) leaves these inputs
    // alone. The rest of the form keeps its standard tokens, so autofill and password managers work there as before.
    var SEARCH_AUTOCOMPLETE = "wasla-list-search";

    function format(template, value) {
        return String(template || "").replace("{0}", value);
    }

    function fireAll(element, types) {
        types.forEach(function (type) {
            element.dispatchEvent(new Event(type, { bubbles: true }));
        });
    }

    function attach(options) {
        var rootEl = options.root;
        var input = options.input;
        var list = options.list;
        var popup = options.popup;
        var stateEl = options.state;
        var alertEl = options.alert;
        var liveEl = options.live;
        var select = options.select || null;
        var mode = options.mode === "select" ? "select" : "suggest";
        var messages = options.messages || {};

        var items = [];
        var visible = [];
        var activeIndex = -1;
        var isOpen = false;
        var status = "ready";
        var typed = false;
        var picked = null;
        var typedRow = null;
        var liveTimer = 0;
        var frame = 0;
        var muted = false;

        input.setAttribute("role", "combobox");
        input.setAttribute("aria-autocomplete", "list");
        input.setAttribute("aria-expanded", "false");
        input.setAttribute("aria-controls", list.id);
        input.setAttribute("autocomplete", SEARCH_AUTOCOMPLETE);
        input.removeAttribute("list");

        // ------------------------------------------------------------------ items

        function build(next) {
            list.textContent = "";
            items = next.map(function (item, index) {
                var label = String(item.label == null ? "" : item.label);
                var li = document.createElement("li");
                li.id = list.id + "-" + index;
                li.className = "su-combo__option";
                li.setAttribute("role", "option");
                li.setAttribute("aria-selected", "false");
                li.setAttribute("data-value", label);
                li.setAttribute("data-id", item.id == null ? "" : String(item.id));
                var text = document.createElement("span");
                text.className = "su-combo__text";
                text.setAttribute("dir", "auto");
                text.textContent = label;
                var check = document.createElement("i");
                check.className = "bi bi-check2 su-combo__check";
                check.setAttribute("aria-hidden", "true");
                li.appendChild(text);
                li.appendChild(check);
                list.appendChild(li);
                return { id: item.id == null ? "" : String(item.id), label: label, key: Search.fold(label), node: li, text: text };
            });
            typedRow = null;
            if (mode === "suggest") {
                typedRow = document.createElement("li");
                typedRow.id = list.id + "-typed";
                typedRow.className = "su-combo__option su-combo__option--typed";
                typedRow.setAttribute("role", "option");
                typedRow.setAttribute("aria-selected", "false");
                typedRow.setAttribute("data-su-combo-typed", "");
                typedRow.hidden = true;
                list.appendChild(typedRow);
            }
            render();
        }

        function selectItems() {
            var next = [];
            Array.prototype.forEach.call(select.options, function (option) {
                if (option.value !== "") {
                    next.push({ id: option.value, label: option.textContent.trim() });
                }
            });
            return next;
        }

        function sameItems(next) {
            if (next.length !== items.length) {
                return false;
            }
            for (var i = 0; i < next.length; i++) {
                if (String(next[i].id) !== items[i].id || next[i].label !== items[i].label) {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ value

        function selectedItem() {
            if (mode === "select") {
                var value = select.value;
                for (var i = 0; i < items.length; i++) {
                    if (items[i].id === value) {
                        return items[i];
                    }
                }
                return null;
            }
            var current = input.value;
            if (picked && picked.label === current) {
                return picked;
            }
            for (var j = 0; j < items.length; j++) {
                if (items[j].label === current) {
                    return items[j];
                }
            }
            return null;
        }

        function placeholderText() {
            if (status === "loading") {
                return messages.loading || "";
            }
            if (status === "error" && mode === "select") {
                return messages.failedShort || messages.failed || "";
            }
            if (isOpen) {
                return messages.search || "";
            }
            if (mode === "select") {
                var first = select.options[0];
                return first && first.value === "" ? first.textContent.trim() : "";
            }
            return messages.placeholder || "";
        }

        function showChosen() {
            if (mode !== "select") {
                return;
            }
            var item = selectedItem();
            input.value = item ? item.label : "";
        }

        // ------------------------------------------------------------------ rendering

        function setActive(index) {
            visible.forEach(function (node, i) {
                node.classList.toggle("is-active", i === index);
            });
            activeIndex = index >= 0 && index < visible.length ? index : -1;
            if (activeIndex >= 0) {
                var node = visible[activeIndex];
                input.setAttribute("aria-activedescendant", node.id);
                var top = node.offsetTop;
                var bottom = top + node.offsetHeight;
                if (top < list.scrollTop) {
                    list.scrollTop = top;
                } else if (bottom > list.scrollTop + list.clientHeight) {
                    list.scrollTop = bottom - list.clientHeight;
                }
            } else {
                input.removeAttribute("aria-activedescendant");
            }
        }

        function mark(item, match) {
            var parts = Search.split(item.label, match);
            item.text.textContent = "";
            if (parts.before) {
                item.text.appendChild(document.createTextNode(parts.before));
            }
            if (parts.match) {
                var strong = document.createElement("mark");
                strong.className = "su-combo__match";
                strong.textContent = parts.match;
                item.text.appendChild(strong);
            }
            if (parts.after) {
                item.text.appendChild(document.createTextNode(parts.after));
            }
        }

        // A description is read even while its element is hidden, so the failure alert joins the field's description
        // only while it is shown.
        function describe(id, on) {
            if (!id) {
                return;
            }
            var ids = (input.getAttribute("aria-describedby") || "").split(/\s+/).filter(function (token) {
                return token && token !== id;
            });
            if (on) {
                ids.push(id);
            }
            if (ids.length) {
                input.setAttribute("aria-describedby", ids.join(" "));
            } else {
                input.removeAttribute("aria-describedby");
            }
        }

        function announce(text) {
            if (!liveEl) {
                return;
            }
            window.clearTimeout(liveTimer);
            liveTimer = window.setTimeout(function () {
                liveEl.textContent = text;
            }, 350);
        }

        function render() {
            var query = isOpen && typed ? input.value : "";
            var chosen = selectedItem();
            var matches = Search.filter(items, query);
            var shown = {};
            visible = [];
            matches.forEach(function (entry) {
                var item = entry.item;
                shown[item.node.id] = true;
                if (query) {
                    mark(item, entry.match);
                } else if (item.text.firstChild !== item.text.lastChild || !item.text.firstChild || item.text.firstChild.nodeType !== 3) {
                    item.text.textContent = item.label;
                }
                item.node.hidden = false;
                item.node.setAttribute("aria-selected", item === chosen ? "true" : "false");
                list.appendChild(item.node);
                visible.push(item.node);
            });
            items.forEach(function (item) {
                if (!shown[item.node.id]) {
                    item.node.hidden = true;
                }
            });

            var text = input.value.trim();
            var offerTyped = mode === "suggest" && typedRow && isOpen && typed && text !== ""
                && !Search.findEquivalent(items, text);
            if (typedRow) {
                typedRow.hidden = !offerTyped;
                if (offerTyped) {
                    typedRow.textContent = "";
                    var icon = document.createElement("i");
                    icon.className = "bi bi-pencil su-combo__typed-icon";
                    icon.setAttribute("aria-hidden", "true");
                    var label = document.createElement("span");
                    label.className = "su-combo__text";
                    var before = String(messages.useTyped || "{0}").split("{0}");
                    label.appendChild(document.createTextNode(before[0] || ""));
                    var value = document.createElement("bdi");
                    value.className = "su-combo__typed-value";
                    value.textContent = text;
                    label.appendChild(value);
                    label.appendChild(document.createTextNode(before[1] || ""));
                    typedRow.appendChild(icon);
                    typedRow.appendChild(label);
                    typedRow.classList.toggle("su-combo__option--after", visible.length > 0);
                    list.appendChild(typedRow);
                    visible.push(typedRow);
                }
            }

            // The state line under the options: loading, failed (with retry), nothing to suggest, nothing found.
            var stateText = "";
            var showRetry = false;
            if (status === "loading") {
                stateText = messages.loading || "";
            } else if (status === "error") {
                stateText = messages.failed || "";
                showRetry = !!options.onRetry;
            } else if (!items.length) {
                stateText = mode === "suggest" ? (messages.noSuggestions || "") : (messages.empty || "");
            } else if (!matches.length) {
                stateText = messages.noMatches || "";
            }
            if (stateEl) {
                stateEl.hidden = !stateText;
                stateEl.setAttribute("data-state", status);
                var stateLabel = stateEl.querySelector("[data-su-combo-state-text]");
                if (stateLabel) {
                    stateLabel.textContent = stateText;
                }
                var stateRetry = stateEl.querySelector("[data-su-combo-retry]");
                if (stateRetry) {
                    stateRetry.hidden = !showRetry;
                }
            }
            list.hidden = visible.length === 0;
            if (alertEl) {
                alertEl.hidden = status !== "error";
                describe(alertEl.id, status === "error");
            }

            input.placeholder = placeholderText();
            rootEl.setAttribute("data-status", status);

            var keep = activeIndex >= 0 ? visible.indexOf(document.getElementById(input.getAttribute("aria-activedescendant") || "")) : -1;
            if (keep < 0 && isOpen && mode === "select" && typed && query && matches.length) {
                keep = 0;
            }
            setActive(isOpen ? keep : -1);
            if (isOpen && typed) {
                announce(format(messages.results, String(matches.length)));
            }
        }

        // ------------------------------------------------------------------ open / close / position

        function place() {
            frame = 0;
            if (!isOpen) {
                return;
            }
            var rect = input.getBoundingClientRect();
            var viewport = window.visualViewport;
            var viewTop = viewport ? viewport.offsetTop : 0;
            // A bar fixed to the bottom of the screen (the signup's mobile strip) covers what is under it.
            var viewBottom = (viewport ? viewport.offsetTop + viewport.height : window.innerHeight)
                - (options.avoidBottom ? options.avoidBottom() : 0);
            var below = viewBottom - rect.bottom - 12;
            var above = rect.top - viewTop - 12;
            var sample = visible[0];
            var row = sample ? sample.getBoundingClientRect().height || 44 : 44;
            var wanted = Math.round(row * ROW_LIMIT) + 8;
            var up = below < Math.min(wanted, 220) && above > below;
            var room = up ? above : below;
            rootEl.classList.toggle("su-combo--up", up);
            // The popup's own padding, border and state line take room too; at least two rows stay usable when the
            // screen is very short (a high zoom).
            var chrome = popup.offsetHeight - list.offsetHeight;
            list.style.maxHeight = Math.min(wanted, Math.max(row * 2, room - chrome)) + "px";
        }

        function schedulePlace() {
            if (!frame) {
                frame = window.requestAnimationFrame(place);
            }
        }

        function open() {
            if (isOpen || input.disabled) {
                return;
            }
            isOpen = true;
            typed = false;
            popup.hidden = false;
            rootEl.classList.add("is-open");
            input.setAttribute("aria-expanded", "true");
            render();
            var chosen = visible.indexOf(selectedItem() ? selectedItem().node : null);
            setActive(chosen);
            place();
            window.addEventListener("resize", schedulePlace);
            window.addEventListener("scroll", schedulePlace, true);
            if (window.visualViewport) {
                window.visualViewport.addEventListener("resize", schedulePlace);
            }
        }

        function close(revert) {
            if (!isOpen) {
                if (revert) {
                    showChosen();
                }
                return;
            }
            isOpen = false;
            typed = false;
            popup.hidden = true;
            rootEl.classList.remove("is-open", "su-combo--up");
            input.setAttribute("aria-expanded", "false");
            setActive(-1);
            window.removeEventListener("resize", schedulePlace);
            window.removeEventListener("scroll", schedulePlace, true);
            if (window.visualViewport) {
                window.visualViewport.removeEventListener("resize", schedulePlace);
            }
            if (revert) {
                showChosen();
            }
            render();
        }

        // ------------------------------------------------------------------ choosing

        function revalidate(element) {
            var $ = window.jQuery;
            if (!$ || !element.form) {
                return;
            }
            var validator = $(element.form).data("validator");
            if (validator && (element.getAttribute("aria-invalid") === "true" || element.classList.contains("input-validation-error"))) {
                $(element).valid();
            }
        }

        // Tells the page about a change this control made, without the control reacting to its own events.
        function announceChange(element) {
            muted = true;
            try {
                fireAll(element, ["input", "change"]);
            } finally {
                muted = false;
            }
        }

        function choose(node) {
            if (!node) {
                return;
            }
            if (node === typedRow) {
                picked = null;
                var text = input.value.trim();
                input.value = text;
                close(false);
                announceChange(input);
                if (options.onUseTyped) {
                    options.onUseTyped(text);
                }
                return;
            }
            var item = null;
            for (var i = 0; i < items.length; i++) {
                if (items[i].node === node) {
                    item = items[i];
                    break;
                }
            }
            if (!item) {
                return;
            }
            if (mode === "select") {
                var changed = select.value !== item.id;
                select.value = item.id;
                close(true);
                if (changed) {
                    announceChange(select);
                }
                revalidate(select);
                return;
            }
            picked = item;
            input.value = item.label;
            close(false);
            announceChange(input);
            if (options.onPick) {
                options.onPick(item);
            }
            revalidate(input);
        }

        // ------------------------------------------------------------------ events

        input.addEventListener("keydown", function (event) {
            if (event.isComposing) {
                return;
            }
            switch (event.key) {
                case "ArrowDown":
                    event.preventDefault();
                    if (!isOpen) {
                        open();
                        if (activeIndex < 0) {
                            setActive(0);
                        }
                    } else {
                        setActive(Math.min(visible.length - 1, activeIndex + 1));
                    }
                    break;
                case "ArrowUp":
                    if (!isOpen) {
                        return;
                    }
                    event.preventDefault();
                    if (event.altKey) {
                        close(mode === "select");
                    } else {
                        setActive(Math.max(0, activeIndex - 1));
                    }
                    break;
                case "PageDown":
                case "PageUp":
                    if (!isOpen) {
                        return;
                    }
                    event.preventDefault();
                    var step = Math.max(1, Math.floor(ROW_LIMIT));
                    setActive(event.key === "PageDown"
                        ? Math.min(visible.length - 1, Math.max(0, activeIndex) + step)
                        : Math.max(0, activeIndex - step));
                    break;
                case "Enter":
                    if (!isOpen) {
                        return;
                    }
                    // Enter belongs to the list while it is open: it never continues the step.
                    event.preventDefault();
                    event.stopPropagation();
                    if (activeIndex >= 0) {
                        choose(visible[activeIndex]);
                    } else {
                        close(mode === "select");
                    }
                    break;
                case "Escape":
                    if (!isOpen) {
                        return;
                    }
                    event.preventDefault();
                    event.stopPropagation();
                    close(mode === "select");
                    break;
                case "Tab":
                    close(mode === "select");
                    break;
            }
        });

        input.addEventListener("input", function (event) {
            if (muted) {
                return;
            }
            if (event.isTrusted === false && document.activeElement !== input) {
                // A value set by the page itself (restore, tests): no list, only the current state.
                if (mode === "suggest" && picked && picked.label !== input.value) {
                    picked = null;
                }
                render();
                return;
            }
            if (mode === "suggest" && picked && picked.label !== input.value) {
                picked = null;
            }
            if (!isOpen) {
                open();
            }
            typed = true;
            render();
            place();
        });

        input.addEventListener("click", function () {
            if (!isOpen) {
                open();
                if (mode === "select") {
                    input.select();
                }
            }
        });

        input.addEventListener("blur", function () {
            window.setTimeout(function () {
                if (!rootEl.contains(document.activeElement)) {
                    close(mode === "select");
                }
            }, 0);
        });

        list.addEventListener("mousedown", function (event) {
            // Keeps focus in the input while an option is pressed.
            event.preventDefault();
        });

        list.addEventListener("click", function (event) {
            var node = event.target.closest('[role="option"]');
            if (node && !node.hidden) {
                choose(node);
                input.focus();
            }
        });

        list.addEventListener("mousemove", function (event) {
            var node = event.target.closest('[role="option"]');
            var index = node ? visible.indexOf(node) : -1;
            if (index >= 0 && index !== activeIndex) {
                setActive(index);
            }
        });

        var toggle = rootEl.querySelector("[data-su-combo-toggle]");
        if (toggle) {
            toggle.addEventListener("mousedown", function (event) {
                event.preventDefault();
                if (input.disabled) {
                    return;
                }
                input.focus();
                if (isOpen) {
                    close(mode === "select");
                } else {
                    open();
                    if (mode === "select") {
                        input.select();
                    }
                }
            });
        }

        document.addEventListener("mousedown", function (event) {
            if (isOpen && !rootEl.contains(event.target)) {
                close(mode === "select");
            }
        });

        // The retry button goes away once the list loads; focus returns to the field instead of the page.
        Array.prototype.forEach.call(rootEl.querySelectorAll("[data-su-combo-retry]"), function (button) {
            button.addEventListener("click", function () {
                var done = options.onRetry ? options.onRetry() : null;
                Promise.resolve(done).then(function () {
                    var active = document.activeElement;
                    if ((!active || active === document.body || !rootEl.contains(active)) && !input.disabled) {
                        input.focus();
                    }
                });
            });
        });

        if (select) {
            // The hidden select keeps the value; whatever focuses it (an error link, the page's error handling) lands
            // in this field instead.
            select.addEventListener("focus", function () {
                if (!input.disabled) {
                    input.focus();
                }
            });
            select.addEventListener("change", function () {
                refresh();
            });
            var mirror = function () {
                var invalid = select.getAttribute("aria-invalid") === "true" || select.classList.contains("input-validation-error");
                if (invalid) {
                    input.setAttribute("aria-invalid", "true");
                } else {
                    input.removeAttribute("aria-invalid");
                }
            };
            new MutationObserver(function (records) {
                var structural = records.some(function (record) {
                    return record.type === "childList" || record.attributeName === "disabled";
                });
                if (structural) {
                    refresh();
                }
                mirror();
            }).observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled", "aria-invalid", "class"] });
            mirror();
        }

        // ------------------------------------------------------------------ public

        // Reads the select again (its options, value and disabled state) after the page changed it.
        function refresh() {
            if (!select) {
                render();
                return;
            }
            var next = selectItems();
            if (!sameItems(next)) {
                build(next);
            }
            var disabled = select.disabled && status !== "error";
            input.disabled = disabled;
            if (disabled && isOpen) {
                close(true);
            }
            if (!isOpen) {
                showChosen();
            }
            render();
            // A value the page set (a restore, a retry) clears the "choose one" error it may still show.
            if (select.value) {
                revalidate(select);
            }
        }

        function setItems(next) {
            build(next || []);
            if (isOpen) {
                place();
            }
        }

        function setStatus(next) {
            status = next;
            if (select) {
                refresh();
            } else {
                render();
            }
            if (next === "error" || next === "loading") {
                announce(next === "error" ? (messages.failed || "") : (messages.loading || ""));
            }
        }

        // The id of the option the visitor picked, while the field still shows its name.
        function pickedId() {
            return picked && picked.label === input.value ? picked.id : null;
        }

        // The id of the one option whose name the text is, ignoring case and Turkish letter forms ("kosuyolu" is
        // KOŞUYOLU); none when no option or several options match. The list offers no "keep as typed" row then.
        function equivalentId(text) {
            var item = Search.findEquivalent(items, String(text == null ? "" : text));
            return item && item.id ? item.id : null;
        }

        function forget() {
            picked = null;
        }

        if (select) {
            build(selectItems());
            refresh();
        } else {
            build([]);
        }

        return {
            refresh: refresh,
            setItems: setItems,
            setStatus: setStatus,
            pickedId: pickedId,
            equivalentId: equivalentId,
            forget: forget,
            open: open,
            close: close,
            isOpen: function () { return isOpen; }
        };
    }

    return { attach: attach };
});
