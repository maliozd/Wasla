(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupViewport = api;
    }
})(function () {
    "use strict";

    // Keeps the fixed bottom strip of the signup flow out of the way (WAS-194, Figma developer notes "Yazılım
    // klavyesi"): the page reserves the strip's measured height so nothing ends under it, and while the on-screen
    // keyboard is open the strip is hidden, so the focused field and its error stay visible.

    var TEXT_INPUT_TYPES = ["", "text", "email", "tel", "password", "search", "url", "number", "date", "time"];

    // Fields that open the on-screen keyboard. A select opens a picker and a checkbox nothing, so they do not count.
    function isEditable(element) {
        if (!element || !element.tagName) {
            return false;
        }

        var tag = element.tagName.toLowerCase();
        if (tag === "textarea") {
            return !element.readOnly && !element.disabled;
        }
        if (tag === "input") {
            var type = (element.getAttribute("type") || "").toLowerCase();
            return TEXT_INPUT_TYPES.indexOf(type) >= 0 && !element.readOnly && !element.disabled;
        }
        return element.isContentEditable === true;
    }

    // The visual viewport is shorter than the layout viewport by more than a keyboard's height while a field that
    // opens the keyboard has focus. Pinch zoom also shrinks the visual viewport, so a scale above 1 is not a keyboard.
    function isKeyboardOpen(layoutHeight, visualHeight, editableFocused, scale, threshold) {
        if (!editableFocused) {
            return false;
        }
        if (scale && scale > 1.05) {
            return false;
        }
        var minimum = typeof threshold === "number" ? threshold : 150;
        return layoutHeight - visualHeight > minimum;
    }

    // Wires the strip: measures it into --su-strip-height, toggles the keyboard state, and returns a dispose function.
    function init(options) {
        var win = options.window || window;
        var doc = win.document;
        var root = doc.documentElement;
        var shell = options.shell;
        var strip = options.strip;
        var viewport = win.visualViewport || null;
        var observer = null;
        var frame = null;

        function stripShown() {
            return !!strip && !strip.hidden && strip.getClientRects().length > 0;
        }

        function measure() {
            var height = stripShown() ? Math.ceil(strip.getBoundingClientRect().height) : 0;
            root.style.setProperty("--su-strip-height", height + "px");
            root.classList.toggle("su-has-strip", height > 0);
        }

        function update() {
            frame = null;
            var open = !!viewport && isKeyboardOpen(
                win.innerHeight,
                viewport.height,
                isEditable(doc.activeElement),
                viewport.scale);
            if (shell) {
                shell.classList.toggle("is-keyboard-open", open);
            }
            root.classList.toggle("su-keyboard-open", open);
            measure();
        }

        function schedule() {
            if (frame === null) {
                frame = win.requestAnimationFrame ? win.requestAnimationFrame(update) : win.setTimeout(update, 16);
            }
        }

        if (viewport) {
            viewport.addEventListener("resize", schedule);
            viewport.addEventListener("scroll", schedule);
        }
        win.addEventListener("resize", schedule);
        doc.addEventListener("focusin", schedule);
        doc.addEventListener("focusout", schedule);

        if (strip && typeof win.ResizeObserver === "function") {
            observer = new win.ResizeObserver(measure);
            observer.observe(strip);
        }

        update();

        return function dispose() {
            if (viewport) {
                viewport.removeEventListener("resize", schedule);
                viewport.removeEventListener("scroll", schedule);
            }
            win.removeEventListener("resize", schedule);
            doc.removeEventListener("focusin", schedule);
            doc.removeEventListener("focusout", schedule);
            if (observer) {
                observer.disconnect();
            }
            root.classList.remove("su-has-strip", "su-keyboard-open");
            root.style.removeProperty("--su-strip-height");
        };
    }

    return {
        isEditable: isEditable,
        isKeyboardOpen: isKeyboardOpen,
        init: init
    };
});
