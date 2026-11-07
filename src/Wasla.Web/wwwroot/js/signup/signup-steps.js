(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupSteps = api;
    }
})(function () {
    "use strict";

    // The signup steps as a small state machine (WAS-194). It only orders the steps and decides which of them the
    // visitor may open; whether a step is complete is reported by the page (and, with the server draft, by the
    // server), never decided here. Back is always allowed; forward only up to the first step that is not complete.

    function create(order, current, completed) {
        if (!Array.isArray(order) || order.length === 0) {
            throw new Error("A signup flow needs at least one step.");
        }

        var done = {};
        (completed || []).forEach(function (id) {
            if (order.indexOf(id) >= 0) {
                done[id] = true;
            }
        });

        var state = { order: order.slice(), current: order[0], completed: done };
        return order.indexOf(current) >= 0 ? clamp(state, current) : state;
    }

    function indexOf(state, id) {
        return state.order.indexOf(id);
    }

    function isComplete(state, id) {
        return state.completed[id] === true;
    }

    // The furthest step the visitor may open: the first incomplete step, or the last step when all are complete.
    function furthestAllowed(state) {
        for (var i = 0; i < state.order.length; i++) {
            if (!isComplete(state, state.order[i])) {
                return state.order[i];
            }
        }
        return state.order[state.order.length - 1];
    }

    function canEnter(state, id) {
        var index = indexOf(state, id);
        return index >= 0 && index <= indexOf(state, furthestAllowed(state));
    }

    // The requested step when allowed, otherwise the furthest allowed step (a URL or history entry that points past
    // an incomplete step lands on that step instead).
    function clamp(state, id) {
        var target = canEnter(state, id) ? id : furthestAllowed(state);
        return { order: state.order, current: target, completed: state.completed };
    }

    function goTo(state, id) {
        return clamp(state, id);
    }

    function setComplete(state, id, complete) {
        if (indexOf(state, id) < 0) {
            return state;
        }

        var done = {};
        Object.keys(state.completed).forEach(function (key) {
            done[key] = state.completed[key];
        });
        if (complete) {
            done[id] = true;
        } else {
            delete done[id];
        }

        return { order: state.order, current: state.current, completed: done };
    }

    function next(state) {
        var index = indexOf(state, state.current);
        return index >= 0 && index < state.order.length - 1 ? state.order[index + 1] : null;
    }

    function previous(state) {
        var index = indexOf(state, state.current);
        return index > 0 ? state.order[index - 1] : null;
    }

    // How the receipt and the progress bar show a step: the open step is current, a completed one is done, the rest
    // are upcoming.
    function statusOf(state, id) {
        if (id === state.current) {
            return "current";
        }
        return isComplete(state, id) ? "done" : "upcoming";
    }

    // The steps between the current one and a later target that still need a check before the target opens.
    function stepsToValidate(state, target) {
        var from = indexOf(state, state.current);
        var to = indexOf(state, target);
        if (from < 0 || to <= from) {
            return [];
        }
        return state.order.slice(from, to);
    }

    // The first step, in flow order, that owns one of the fields with an error; null when none does.
    function firstStepWithErrors(errorFields, fieldSteps, order) {
        var steps = {};
        (errorFields || []).forEach(function (field) {
            var step = fieldSteps[field];
            if (step) {
                steps[step] = true;
            }
        });

        for (var i = 0; i < order.length; i++) {
            if (steps[order[i]]) {
                return order[i];
            }
        }
        return null;
    }

    function readStepFromUrl(href, order) {
        var url;
        try {
            url = new URL(href, "http://localhost");
        } catch (e) {
            return null;
        }
        var step = url.searchParams.get("step");
        return step && order.indexOf(step) >= 0 ? step : null;
    }

    // The same address with the step in its query; every other parameter (plan, culture) and the hash are kept.
    function urlForStep(href, id) {
        var url = new URL(href, "http://localhost");
        url.searchParams.set("step", id);
        var absolute = /^[a-z][a-z0-9+.-]*:/i.test(href);
        return absolute ? url.toString() : url.pathname + url.search + url.hash;
    }

    return {
        create: create,
        goTo: goTo,
        canEnter: canEnter,
        furthestAllowed: furthestAllowed,
        setComplete: setComplete,
        isComplete: isComplete,
        next: next,
        previous: previous,
        statusOf: statusOf,
        stepsToValidate: stepsToValidate,
        firstStepWithErrors: firstStepWithErrors,
        readStepFromUrl: readStepFromUrl,
        urlForStep: urlForStep
    };
});
