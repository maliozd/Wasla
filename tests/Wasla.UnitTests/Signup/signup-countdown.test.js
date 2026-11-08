const test = require("node:test");
const assert = require("node:assert/strict");

const countdown = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-countdown.js");

function fakeClock(start) {
    const clock = { now: start, timers: [] };
    clock.read = () => clock.now;
    clock.setTimer = (fn, ms) => {
        const timer = { fn, at: clock.now + ms, cleared: false };
        clock.timers.push(timer);
        return timer;
    };
    clock.clearTimer = timer => { timer.cleared = true; };
    // Runs every timer due by the given time, in order, like the browser's event loop.
    clock.advanceTo = time => {
        for (;;) {
            const due = clock.timers
                .filter(t => !t.cleared && !t.ran && t.at <= time)
                .sort((a, b) => a.at - b.at)[0];
            if (!due) break;
            clock.now = due.at;
            due.ran = true;
            due.fn();
        }
        clock.now = time;
    };
    return clock;
}

test("seconds are shown as m:ss with Latin digits, rounded up", () => {
    assert.equal(countdown.format(42), "0:42");
    assert.equal(countdown.format(60), "1:00");
    assert.equal(countdown.format(125), "2:05");
    assert.equal(countdown.format(0.2), "0:01");
    assert.equal(countdown.format(0), "0:00");
    assert.equal(countdown.format(-5), "0:00");
    assert.equal(countdown.format("not a number"), "0:00");
});

test("the deadline comes from the server's relative seconds, not from any clock time", () => {
    const deadline = countdown.deadline(10_000, 42);

    assert.equal(deadline, 52_000);
    assert.equal(countdown.remaining(deadline, 10_000), 42);
    assert.equal(countdown.remaining(deadline, 51_001), 1);
    assert.equal(countdown.remaining(deadline, 52_000), 0);
    assert.equal(countdown.remaining(deadline, 60_000), 0);
    assert.equal(countdown.deadline(5, null), 5);
});

test("a countdown ticks once per second and finishes once", () => {
    const clock = fakeClock(1_000);
    const ticks = [];
    let done = 0;

    countdown.start({
        seconds: 3,
        now: clock.read,
        setTimer: clock.setTimer,
        clearTimer: clock.clearTimer,
        onTick: seconds => ticks.push(seconds),
        onDone: () => done++
    });
    clock.advanceTo(10_000);

    assert.deepEqual(ticks, [3, 2, 1, 0]);
    assert.equal(done, 1);
});

test("a late timer does not skip the finish or repeat a second", () => {
    const clock = fakeClock(0);
    const ticks = [];
    let done = 0;

    countdown.start({
        seconds: 2,
        now: clock.read,
        setTimer: (fn, ms) => clock.setTimer(fn, ms + 700),
        clearTimer: clock.clearTimer,
        onTick: seconds => ticks.push(seconds),
        onDone: () => done++
    });
    clock.advanceTo(10_000);

    assert.equal(ticks[0], 2);
    assert.equal(ticks[ticks.length - 1], 0);
    assert.equal(new Set(ticks).size, ticks.length);
    assert.equal(done, 1);
});

test("a stopped countdown stays silent", () => {
    const clock = fakeClock(0);
    const ticks = [];
    let done = 0;

    const running = countdown.start({
        seconds: 5,
        now: clock.read,
        setTimer: clock.setTimer,
        clearTimer: clock.clearTimer,
        onTick: seconds => ticks.push(seconds),
        onDone: () => done++
    });
    running.stop();
    clock.advanceTo(10_000);

    assert.deepEqual(ticks, [5]);
    assert.equal(done, 0);
});

test("zero seconds finishes at once", () => {
    const clock = fakeClock(0);
    let done = 0;

    countdown.start({ seconds: 0, now: clock.read, setTimer: clock.setTimer, clearTimer: clock.clearTimer, onDone: () => done++ });

    assert.equal(done, 1);
    assert.equal(clock.timers.length, 0);
});
