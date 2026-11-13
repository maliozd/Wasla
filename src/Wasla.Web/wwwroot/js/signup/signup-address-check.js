(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupAddressCheck = api;
    }
})(function () {
    "use strict";

    // The live panel address check of the restaurant step (WAS-194, Figma P1). Advisory only: it reserves nothing,
    // and the server checks the address again when the registration is created (S10). This module only orders the
    // requests: it waits until typing pauses, sends one request at a time, aborts the one in flight when the address
    // changes, and drops any answer that is not for the latest address. How to ask the server is injected.

    var MAX_LENGTH = 63;
    var FORMAT = /^[a-z0-9][a-z0-9_-]*$/;

    // Instant feedback on the shape of the address; the server stays the authority (reserved names, taken addresses).
    function checkFormat(slug) {
        var value = typeof slug === "string" ? slug : "";
        if (value.length === 0) {
            return "empty";
        }
        if (value.length > MAX_LENGTH) {
            return "too-long";
        }
        return FORMAT.test(value) ? "ok" : "invalid";
    }

    // options.check(slug, signal) resolves to { state: "available" | "taken" | "invalid" | "error", ... }.
    // options.onState(result) receives { state, slug, ... }: "idle", "invalid", "checking", or the server's answer.
    function create(options) {
        var check = options.check;
        var onState = options.onState || function () { };
        var debounceMs = typeof options.debounceMs === "number" ? options.debounceMs : 450;
        var setTimer = options.setTimer || function (fn, ms) { return setTimeout(fn, ms); };
        var clearTimer = options.clearTimer || function (id) { clearTimeout(id); };
        var createController = options.createAbortController || function () {
            return typeof AbortController === "function" ? new AbortController() : null;
        };

        var latest = null;
        var timer = null;
        var controller = null;
        var sequence = 0;
        var lastSettled = null;

        function cancelPending() {
            if (timer !== null) {
                clearTimer(timer);
                timer = null;
            }
            if (controller) {
                controller.abort();
                controller = null;
            }
        }

        function run(slug) {
            timer = null;
            if (slug !== latest) {
                return;
            }

            var mine = ++sequence;
            controller = createController();
            onState({ state: "checking", slug: slug });

            Promise.resolve()
                .then(function () {
                    return check(slug, controller ? controller.signal : undefined);
                })
                .then(function (result) {
                    if (mine !== sequence || slug !== latest) {
                        return;
                    }
                    controller = null;
                    lastSettled = slug;
                    var answer = result && typeof result === "object" ? result : { state: "error" };
                    var settled = {};
                    Object.keys(answer).forEach(function (key) {
                        settled[key] = answer[key];
                    });
                    settled.slug = slug;
                    settled.state = settled.state || "error";
                    onState(settled);
                }, function () {
                    if (mine !== sequence || slug !== latest) {
                        return;
                    }
                    controller = null;
                    lastSettled = null;
                    onState({ state: "error", slug: slug });
                });
        }

        // The address changed: show its shape at once, and ask the server after a pause in typing.
        function input(value) {
            var slug = typeof value === "string" ? value.trim() : "";
            if (slug === latest) {
                return;
            }

            latest = slug;
            lastSettled = null;
            cancelPending();
            sequence++;

            var format = checkFormat(slug);
            if (format === "empty") {
                onState({ state: "idle", slug: slug });
                return;
            }
            if (format !== "ok") {
                onState({ state: "invalid", slug: slug, reason: format });
                return;
            }

            // The old answer goes at once; the new address waits ("idle") for its own check.
            onState({ state: "idle", slug: slug });
            timer = setTimer(function () { run(slug); }, debounceMs);
        }

        // The field lost focus: ask now instead of waiting for the pause, unless the answer is already in.
        function flush() {
            if (latest === null || checkFormat(latest) !== "ok" || lastSettled === latest || controller) {
                return;
            }
            if (timer !== null) {
                clearTimer(timer);
                timer = null;
            }
            run(latest);
        }

        function dispose() {
            cancelPending();
            sequence++;
            latest = null;
        }

        return {
            input: input,
            flush: flush,
            dispose: dispose
        };
    }

    return {
        MAX_LENGTH: MAX_LENGTH,
        checkFormat: checkFormat,
        create: create
    };
});
