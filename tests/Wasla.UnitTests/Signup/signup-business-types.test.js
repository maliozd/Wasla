const test = require("node:test");
const assert = require("node:assert/strict");

const api = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-business-types.js");

test("deselecting a category clears its subtype selections", () => {
    const cleared = api.syncCategory(false, [true, false, true]);
    assert.deepEqual(cleared, [false, false, false]);

    const kept = api.syncCategory(true, [true, false, true]);
    assert.deepEqual(kept, [true, false, true]);
});

test("validation requires a subtype for every selected category and at least one subtype", () => {
    const empty = api.validate([
        { selected: false, subtypes: [false, false] },
        { selected: false, subtypes: [false] }
    ]);
    assert.equal(empty.ok, false);
    assert.equal(empty.anySubtype, false);

    const categoryWithoutSubtype = api.validate([
        { selected: true, subtypes: [false, false] },
        { selected: true, subtypes: [true] }
    ]);
    assert.equal(categoryWithoutSubtype.ok, false);
    assert.equal(categoryWithoutSubtype.categoryMissingSubtype, true);

    const valid = api.validate([
        { selected: true, subtypes: [true, false] },
        { selected: true, subtypes: [false, true] }
    ]);
    assert.equal(valid.ok, true);
    assert.equal(valid.categoryMissingSubtype, false);
});
