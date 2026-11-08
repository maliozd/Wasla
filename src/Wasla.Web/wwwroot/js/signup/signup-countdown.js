(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupCountdown = api;
    }
})(function () {
    "use strict";

    // Countdowns of the phone verification step (WAS-194): when a new code may be requested, when the code expires,
    // when a lock ends. The server sends relative seconds, never a clock time, so the browser's clock and time zone
    // do not matter: the deadline is measured on the page's monotonic clock from the moment the answer arrived.

    function toSeconds(value) {
        var number = Number(value);
        return isFinite(number) && number > 0 ? number : 0;
    }

    function deadline(nowMs, relativeSeconds) {
        return nowMs + toSeconds(relativeSeconds) * 1000;
    }

    // Whole seconds left, rounded up, so "0:01" shows until the deadline has really passed.
    function remaining(deadlineMs, nowMs) {
        var ms = deadlineMs - nowMs;
        return ms > 0 ? Math.ceil(ms / 1000) : 0;
    }

    // "m:ss" with Latin digits in every culture: the code and the SMS use Latin digits too (Figma L3), and the value
    // is isolated left to right in the markup.
    function format(seconds) {
        var total = Math.max(0, Math.ceil(toSeconds(seconds)));
        var minutes = Math.floor(total / 60);
        var rest = total % 60;
        return String(minutes) + ":" + (rest < 10 ? "0" : "") + String(rest);
    }

    // Calls onTick with the seconds left at once and on every whole-second change, then onDone once at zero.
    // Ticks are scheduled for the next second boundary of the deadline, so a slow timer does not drift the display.
    function start(options) {
        var now = options.now || function () {
            return typeof performance !== "undefined" && performance.now ? performance.now() : Date.now();
        };
        var setTimer = options.setTimer || function (fn, ms) { return setTimeout(fn, ms); };
        var clearTimer = options.clearTimer || function (id) { clearTimeout(id); };
        var onTick = options.onTick || function () { };
        var onDone = options.onDone || function () { };
        var end = deadline(now(), options.seconds);
        var timer = null;
        var stopped = false;
        var last = null;

        function tick() {
            timer = null;
            if (stopped) {
                return;
            }

            var current = now();
            var left = remaining(end, current);
            if (left !== last) {
                last = left;
                onTick(left);
            }

            if (left === 0) {
                stopped = true;
                onDone();
                return;
            }

            var untilNextSecond = (end - current) - (left - 1) * 1000;
            timer = setTimer(tick, Math.max(16, Math.ceil(untilNextSecond)));
        }

        tick();

        return {
            stop: function () {
                stopped = true;
                if (timer !== null) {
                    clearTimer(timer);
                    timer = null;
                }
            },
            remaining: function () {
                return remaining(end, now());
            }
        };
    }

    return {
        deadline: deadline,
        remaining: remaining,
        format: format,
        start: start
    };
});
