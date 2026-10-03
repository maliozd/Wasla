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
