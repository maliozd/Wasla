const test = require("node:test");
const assert = require("node:assert/strict");
const realtime = require("../../../src/Wasla.Web/wwwroot/js/realtime/wasla-realtime.js");

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}

function clock() {
  let seq = 1;
  const timers = new Map();
  return {
    timers: timers,
    setTimeout: function (fn, ms) { const id = seq++; timers.set(id, { fn: fn, ms: ms }); return id; },
    clearTimeout: function (id) { timers.delete(id); },
    runNext: function () {
      const [id, timer] = timers.entries().next().value;
      timers.delete(id);
      timer.fn();
      return timer.ms;
    }
  };
}

// A stand-in for the browser SignalR client: records the calls and lets the test decide each start's outcome.
function fakeSignalR() {
  const log = [];
  const starts = [];
  let connection = null;
  const api = {
    log: log,
    starts: starts,
    builds: 0,
    HubConnectionState: { Disconnected: "Disconnected", Connected: "Connected" },
    LogLevel: { None: 6 },
    connection: function () { return connection; },
    HubConnectionBuilder: function () {
      const self = this;
      self.withUrl = function (url) { log.push("withUrl:" + url); return self; };
      self.withAutomaticReconnect = function (policy) { api.policy = policy; return self; };
      self.configureLogging = function () { return self; };
      self.build = function () {
        api.builds += 1;
        const handlers = { on: {}, reconnecting: [], reconnected: [], close: [] };
        connection = {
          state: "Disconnected",
          handlers: handlers,
          on: function (name, fn) { log.push("on:" + name); handlers.on[name] = fn; },
          onreconnecting: function (fn) { handlers.reconnecting.push(fn); },
          onreconnected: function (fn) { handlers.reconnected.push(fn); },
          onclose: function (fn) { handlers.close.push(fn); },
          start: function () {
            log.push("start");
            const outcome = starts.shift() || "ok";
            if (outcome === "ok") {
              connection.state = "Connected";
              return Promise.resolve();
            }
            return Promise.reject(new Error("negotiate failed"));
          },
          stop: function () {
            log.push("stop");
            connection.state = "Disconnected";
            handlers.close.forEach(function (fn) { fn(); });
            return Promise.resolve();
          },
          emit: function (message) { handlers.on.tenantChange(message); },
          drop: function () { connection.state = "Reconnecting"; handlers.reconnecting.forEach(function (fn) { fn(); }); },
          back: function () { connection.state = "Connected"; handlers.reconnected.forEach(function (fn) { fn(); }); }
        };
        return connection;
      };
    }
  };
  return api;
}

function client(signalR, timers) {
  return realtime.createRealtimeClient({
    signalR: signalR,
    hubUrl: "/hubs/tenant-events",
    setTimeout: timers.setTimeout,
    clearTimeout: timers.clearTimeout
  });
}

test("one connection, with its handler attached before the first start", async function () {
  const signalR = fakeSignalR();
  const c = client(signalR, clock());
  c.start();
  c.start();
  await flush();

  assert.equal(signalR.builds, 1);
  assert.deepEqual(signalR.log, ["withUrl:/hubs/tenant-events", "on:tenantChange", "start"]);
  assert.equal(c.state(), "connected");
});

test("a failed first start is retried, and the first success after it counts as a reconnect", async function () {
  const signalR = fakeSignalR();
  const timers = clock();
  signalR.starts.push("fail", "fail", "ok");
  const c = client(signalR, timers);
  const states = [];
  c.onState(function (info) { states.push(info.state + (info.reconnected ? "+reconnected" : "")); });

  c.start();
  await flush();
  assert.equal(c.state(), "disconnected");
  assert.equal(timers.runNext(), 2000);
  await flush();
  assert.equal(timers.runNext(), 5000);
  await flush();

  assert.equal(signalR.log.filter(function (l) { return l === "start"; }).length, 3);
  assert.equal(c.state(), "connected");
  assert.deepEqual(states, ["disconnected", "connected+reconnected"]);
  assert.equal(timers.timers.size, 0);
});

test("automatic reconnect never gives up and settles at 30 seconds", function () {
  const signalR = fakeSignalR();
  client(signalR, clock());
  const delays = [0, 1, 2, 3, 4, 10, 100].map(function (n) {
    return signalR.policy.nextRetryDelayInMilliseconds({ previousRetryCount: n });
  });
  assert.deepEqual(delays, [0, 2000, 5000, 10000, 30000, 30000, 30000]);
});

test("a drop and a reconnect are reported, the reconnect flagged", async function () {
  const signalR = fakeSignalR();
  const c = client(signalR, clock());
  const states = [];
  c.onState(function (info) { states.push(info.state + (info.reconnected ? "+reconnected" : "")); });
  c.start();
  await flush();

  signalR.connection().drop();
  signalR.connection().back();

  assert.deepEqual(states, ["connected", "disconnected", "connected+reconnected"]);
});

test("notifications go to the handlers of their type only, and one failing handler does not stop the others", async function () {
  const signalR = fakeSignalR();
  const c = client(signalR, clock());
  const seen = [];
  c.on("PrintJobChanged", function () { throw new Error("broken screen"); });
  const off = c.on("PrintJobChanged", function (m) { seen.push(m.version); });
  c.on("OrderChanged", function () { seen.push("wrong"); });
  c.start();
  await flush();

  signalR.connection().emit({ type: "PrintJobChanged", version: 3 });
  signalR.connection().emit({ type: "Unknown", version: 4 });
  signalR.connection().emit(null);
  off();
  signalR.connection().emit({ type: "PrintJobChanged", version: 5 });

  assert.deepEqual(seen, [3]);
});

test("a closed connection is started again slowly, but not after stop", async function () {
  const signalR = fakeSignalR();
  const timers = clock();
  const c = client(signalR, timers);
  c.start();
  await flush();

  // The server closed it (for example, the session was revoked) and automatic reconnect did not take over.
  signalR.connection().state = "Disconnected";
  signalR.connection().handlers.close.forEach(function (fn) { fn(); });
  assert.equal(c.state(), "disconnected");
  assert.equal(timers.runNext(), 30000);
  await flush();
  assert.equal(c.state(), "connected");

  await c.stop();
  assert.equal(timers.timers.size, 0);
});
