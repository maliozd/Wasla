(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupApi = api;
    }
})(function () {
    "use strict";

    // The registration draft API client (WAS-194 over the WAS-192 contract, docs/architecture/signup-registration.md).
    // Every answer becomes one result shape the page can act on, without parsing HTTP itself:
    //   { ok: true, status, data }
    //   { ok: false, status, kind, error, retryAfterSeconds }
    // kind: "api" (a SignupApiError body), "antiforgery" (400 without a JSON body: the page must be reloaded),
    // "closed" (404 without a body: public signup was closed), "server" (any other answer without a JSON error),
    // "network" (no answer) or "aborted". Writes run one after another: the first save creates the draft cookie,
    // so a second write must not start before it has finished. The page's antiforgery token goes in the header.

    function isJson(contentType) {
        return typeof contentType === "string" && /\bjson\b/i.test(contentType);
    }

    function parseRetryAfter(value) {
        var seconds = Number(value);
        return isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : null;
    }

    // Classifies a finished HTTP answer. body is the parsed JSON (or null when the body was empty or not JSON).
    function classify(status, contentType, body, retryAfterHeader) {
        if (status >= 200 && status < 300) {
            return { ok: true, status: status, data: body };
        }

        var hasError = isJson(contentType) && body && typeof body === "object" && typeof body.code === "string";
        var retryAfter = hasError && typeof body.retryAfterSeconds === "number"
            ? body.retryAfterSeconds
            : parseRetryAfter(retryAfterHeader);

        if (hasError) {
            return { ok: false, status: status, kind: "api", error: body, retryAfterSeconds: retryAfter };
        }
        if (status === 400) {
            return { ok: false, status: status, kind: "antiforgery", error: null, retryAfterSeconds: null };
        }
        if (status === 404) {
            return { ok: false, status: status, kind: "closed", error: null, retryAfterSeconds: null };
        }
        return { ok: false, status: status, kind: "server", error: null, retryAfterSeconds: retryAfter };
    }

    function create(options) {
        var endpoints = options.endpoints || {};
        var fetchImpl = options.fetch || (typeof fetch === "function" ? fetch.bind(typeof window !== "undefined" ? window : undefined) : null);
        var tokenOf = typeof options.token === "function" ? options.token : function () { return options.token || ""; };
        var writeQueue = Promise.resolve();

        function send(method, url, body, signal) {
            if (!url || !fetchImpl) {
                return Promise.resolve({ ok: false, status: 0, kind: "server", error: null, retryAfterSeconds: null });
            }

            var headers = { Accept: "application/json" };
            var init = { method: method, headers: headers, credentials: "same-origin", cache: "no-store" };
            if (method !== "GET") {
                headers["RequestVerificationToken"] = tokenOf();
                headers["Content-Type"] = "application/json";
                init.body = JSON.stringify(body === undefined ? {} : body);
            }
            if (signal) {
                init.signal = signal;
            }

            return Promise.resolve()
                .then(function () { return fetchImpl(url, init); })
                .then(function (response) {
                    var contentType = response.headers && response.headers.get ? response.headers.get("Content-Type") : "";
                    var retryAfter = response.headers && response.headers.get ? response.headers.get("Retry-After") : null;
                    return response.text().then(function (text) {
                        var parsed = null;
                        if (text && isJson(contentType)) {
                            try {
                                parsed = JSON.parse(text);
                            } catch (e) {
                                parsed = null;
                            }
                        }
                        return classify(response.status, contentType, parsed, retryAfter);
                    });
                }, function (error) {
                    var aborted = error && error.name === "AbortError";
                    return { ok: false, status: 0, kind: aborted ? "aborted" : "network", error: null, retryAfterSeconds: null };
                });
        }

        // Writes wait for every earlier write, whatever its outcome.
        function write(method, url, body) {
            var run = writeQueue.then(function () { return send(method, url, body); });
            writeQueue = run.then(function () { }, function () { });
            return run;
        }

        return {
            getDraft: function () { return send("GET", endpoints.draft); },
            savePlan: function (body) { return write("PUT", endpoints.planSection, body); },
            saveRestaurant: function (body) { return write("PUT", endpoints.restaurantSection, body); },
            saveOwner: function (body) { return write("PUT", endpoints.ownerSection, body); },
            getPhone: function () { return send("GET", endpoints.phone); },
            sendCode: function () { return write("POST", endpoints.phoneSend, {}); },
            verifyCode: function (code) { return write("POST", endpoints.phoneVerify, { code: code }); },
            changePhone: function (mobilePhone) { return write("PUT", endpoints.phone, { mobilePhone: mobilePhone }); },
            submit: function () { return write("POST", endpoints.submit, {}); },
            checkAddress: function (slug, signal) {
                var base = endpoints.addressAvailability;
                if (!base) {
                    return Promise.resolve({ ok: false, status: 0, kind: "server", error: null, retryAfterSeconds: null });
                }
                var url = base + (base.indexOf("?") >= 0 ? "&" : "?") + "slug=" + encodeURIComponent(slug);
                return send("GET", url, undefined, signal);
            }
        };
    }

    return {
        classify: classify,
        create: create
    };
});
