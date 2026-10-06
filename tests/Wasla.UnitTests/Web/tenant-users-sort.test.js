const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const root = path.join(__dirname, "..", "..", "..");
const users = require(path.join(root, "src", "Wasla.Web", "wwwroot", "js", "tenant-users-index.js"));

const collator = new Intl.Collator("tr-TR", { sensitivity: "base", numeric: true });

function rows() {
  return [
    { name: "Zeynep", role: "3", created: "2026-10-01T09:00:00.0000000Z", email: "z@example.test", index: 0 },
    { name: "Ali", role: "1", created: "2026-09-01T09:00:00.0000000Z", email: "ali@example.test", index: 1 },
    { name: "Çağla", role: "2", created: "2026-10-02T09:00:00.0000000Z", email: "c@example.test", index: 2 },
    { name: "ali", role: "3", created: "2026-10-01T09:00:00.0000000Z", email: "ali2@example.test", index: 3 },
    { name: "Mert", role: "5", created: "2026-08-01T09:00:00.0000000Z", email: "m@example.test", index: 4 }
  ];
}

function order(key, direction) {
  return rows()
    .sort((a, b) => users.compareRows(a, b, key, direction, collator))
    .map((row) => row.email);
}

test("name sorts with the page culture, ignoring case, and breaks ties by email", () => {
  assert.deepEqual(order("name", "asc"), [
    "ali@example.test", "ali2@example.test", "c@example.test", "m@example.test", "z@example.test"
  ]);
  // Descending reverses the names; equal names keep the email tie-breaker ascending.
  assert.deepEqual(order("name", "desc"), [
    "z@example.test", "m@example.test", "c@example.test", "ali@example.test", "ali2@example.test"
  ]);
});

test("role sorts by permission level (Owner first), not by the translated label", () => {
  assert.deepEqual(order("role", "asc"), [
    "ali@example.test", "c@example.test", "ali2@example.test", "z@example.test", "m@example.test"
  ]);
  assert.deepEqual(order("role", "desc"), [
    "m@example.test", "ali2@example.test", "z@example.test", "c@example.test", "ali@example.test"
  ]);
});

test("created sorts by date, and the same date falls back to email so the order is deterministic", () => {
  assert.deepEqual(order("created", "desc"), [
    "c@example.test", "ali2@example.test", "z@example.test", "ali@example.test", "m@example.test"
  ]);
  assert.deepEqual(order("created", "asc"), [
    "m@example.test", "ali@example.test", "ali2@example.test", "z@example.test", "c@example.test"
  ]);
});

test("a new column starts in its natural direction and the same column toggles", () => {
  assert.equal(users.nextDirection("name", null, null), "asc");
  assert.equal(users.nextDirection("role", "name", "asc"), "asc");
  assert.equal(users.nextDirection("created", "name", "asc"), "desc");
  assert.equal(users.nextDirection("name", "name", "asc"), "desc");
  assert.equal(users.nextDirection("name", "name", "desc"), "asc");
});

test("identical rows keep their rendered order", () => {
  const a = { name: "Same", role: "2", created: "x", email: "same@example.test", index: 0 };
  const b = { name: "Same", role: "2", created: "x", email: "same@example.test", index: 1 };
  assert.ok(users.compareRows(a, b, "name", "desc", collator) < 0);
  assert.ok(users.compareRows(b, a, "name", "asc", collator) > 0);
});

test("the Turkish-aware fold used by the filter is unchanged", () => {
  assert.equal(users.fold("İSTANBUL"), users.fold("istanbul"));
  assert.equal(users.fold("Işık"), "isik");
});

// data-sort-last-login holds the stored UTC value (ISO 8601, "Z"); it is empty when no login was recorded.
function loginRows() {
  return [
    { name: "A", role: "1", created: "", lastLogin: "2026-10-05T08:00:00.0000000Z", email: "a@example.test", index: 0 },
    { name: "B", role: "1", created: "", lastLogin: "", email: "b@example.test", index: 1 },
    { name: "C", role: "1", created: "", lastLogin: "2026-10-06T09:15:30.0000000Z", email: "c@example.test", index: 2 },
    { name: "D", role: "1", created: "", lastLogin: "2025-12-31T23:59:59.9990000Z", email: "d@example.test", index: 3 },
    { name: "E", role: "1", created: "", lastLogin: "", email: "e@example.test", index: 4 },
    { name: "F", role: "1", created: "", lastLogin: "2026-10-06T09:15:30.0000000Z", email: "f@example.test", index: 5 }
  ];
}

function loginOrder(direction) {
  return loginRows()
    .sort((a, b) => users.compareRows(a, b, "last-login", direction, collator))
    .map((row) => row.email);
}

test("last login sorts by the stored UTC value, newest first by default", () => {
  assert.equal(users.nextDirection("last-login", "name", "asc"), "desc");
  assert.deepEqual(loginOrder("desc"), [
    "c@example.test", "f@example.test", "a@example.test", "d@example.test", "b@example.test", "e@example.test"
  ]);
  assert.deepEqual(loginOrder("asc"), [
    "d@example.test", "a@example.test", "c@example.test", "f@example.test", "b@example.test", "e@example.test"
  ]);
});

test("users who never logged in stay last in both directions, ordered by email", () => {
  for (const direction of ["asc", "desc"]) {
    const order = loginOrder(direction);
    assert.deepEqual(order.slice(-2), ["b@example.test", "e@example.test"]);
  }
});

test("last login ignores the culture-formatted cell text and compares the ISO value only", () => {
  // A year boundary: "31.12.2025" would sort after "05.10.2026" as text; the ISO value does not.
  const older = { name: "x", role: "1", created: "", lastLogin: "2025-12-31T23:59:59.9990000Z", email: "x@example.test", index: 0 };
  const newer = { name: "y", role: "1", created: "", lastLogin: "2026-01-01T00:00:00.0000000Z", email: "y@example.test", index: 1 };
  assert.ok(users.compareRows(older, newer, "last-login", "asc", collator) < 0);
  assert.ok(users.compareRows(older, newer, "last-login", "desc", collator) > 0);
});
