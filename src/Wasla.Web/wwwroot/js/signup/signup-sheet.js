(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupSheet = api;
    }
})(function () {
    "use strict";

    // The registration summary sheet below 992 px (WAS-194, Figma M-Özet): the amount in the bottom strip opens it as
    // a modal <dialog> (focus stays inside, Escape closes, the page behind is inert), and closing it returns focus
    // to the strip. It closes by itself when the window widens to the desktop layout, where the receipt is in view.

    function init(options) {
        var dialog = options.dialog;
        var trigger = options.trigger;
        var win = options.window || window;
        if (!dialog || !trigger || typeof dialog.showModal !== "function") {
            if (trigger) {
                trigger.hidden = !dialog;
            }
            return { open: function () { }, close: function () { }, dispose: function () { } };
        }

        var returnFocus = null;
        var desktop = win.matchMedia ? win.matchMedia("(min-width: 992px)") : null;

        function open() {
            if (dialog.open) {
                return;
            }
            returnFocus = trigger;
            dialog.showModal();
            trigger.setAttribute("aria-expanded", "true");
            var focusTarget = dialog.querySelector("[data-su-sheet-close]");
            if (focusTarget) {
                focusTarget.focus();
            }
            if (typeof options.onOpen === "function") {
                options.onOpen();
            }
        }

        // nextFocus: where focus goes after closing; the strip when omitted (an Edit link passes its step heading).
        function close(nextFocus) {
            if (!dialog.open) {
                return;
            }
            if (nextFocus) {
                returnFocus = nextFocus;
            }
            dialog.close();
        }

        function onClosed() {
            trigger.setAttribute("aria-expanded", "false");
            var target = returnFocus;
            returnFocus = null;
            if (target && typeof target.focus === "function") {
                target.focus();
            }
        }

        function onTriggerClick() {
            open();
        }

        function onDialogClick(event) {
            // A click on the backdrop lands on the dialog element itself.
            if (event.target === dialog) {
                close();
                return;
            }
            var closer = event.target.closest ? event.target.closest("[data-su-sheet-close]") : null;
            if (closer) {
                close();
            }
        }

        function onDesktop(query) {
            if (query.matches) {
                returnFocus = null;
                close();
            }
        }

        trigger.setAttribute("aria-expanded", "false");
        trigger.addEventListener("click", onTriggerClick);
        dialog.addEventListener("click", onDialogClick);
        dialog.addEventListener("close", onClosed);
        if (desktop) {
            if (desktop.addEventListener) {
                desktop.addEventListener("change", onDesktop);
            } else if (desktop.addListener) {
                desktop.addListener(onDesktop);
            }
        }

        return {
            open: open,
            close: close,
            isOpen: function () { return dialog.open; },
            dispose: function () {
                trigger.removeEventListener("click", onTriggerClick);
                dialog.removeEventListener("click", onDialogClick);
                dialog.removeEventListener("close", onClosed);
                if (desktop) {
                    if (desktop.removeEventListener) {
                        desktop.removeEventListener("change", onDesktop);
                    } else if (desktop.removeListener) {
                        desktop.removeListener(onDesktop);
                    }
                }
            }
        };
    }

    return {
        init: init
    };
});
