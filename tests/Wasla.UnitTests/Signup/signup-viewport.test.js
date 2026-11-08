const test = require("node:test");
const assert = require("node:assert/strict");

const viewport = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-viewport.js");

function element(tag, attributes, properties) {
    return Object.assign({
        tagName: tag.toUpperCase(),
        readOnly: false,
        disabled: false,
        isContentEditable: false,
        getAttribute: name => (attributes && name in attributes ? attributes[name] : null)
    }, properties);
}

test("text fields open the keyboard; pickers, boxes and buttons do not", () => {
    assert.equal(viewport.isEditable(element("input", { type: "tel" })), true);
    assert.equal(viewport.isEditable(element("input", {})), true, "an input without a type is a text field");
    assert.equal(viewport.isEditable(element("input", { type: "TEXT" })), true);
    assert.equal(viewport.isEditable(element("textarea")), true);
    assert.equal(viewport.isEditable(element("div", null, { isContentEditable: true })), true);

    assert.equal(viewport.isEditable(element("select")), false);
    assert.equal(viewport.isEditable(element("input", { type: "checkbox" })), false);
    assert.equal(viewport.isEditable(element("input", { type: "radio" })), false);
    assert.equal(viewport.isEditable(element("button")), false);
    assert.equal(viewport.isEditable(element("input", { type: "text" }, { readOnly: true })), false);
    assert.equal(viewport.isEditable(element("input", { type: "text" }, { disabled: true })), false);
    assert.equal(viewport.isEditable(null), false);
});

test("the keyboard is open when a text field has focus and the visual viewport lost a keyboard's height", () => {
    assert.equal(viewport.isKeyboardOpen(844, 500, true), true);
    assert.equal(viewport.isKeyboardOpen(844, 800, true), false, "the browser's own toolbar is not a keyboard");
    assert.equal(viewport.isKeyboardOpen(844, 500, false), false, "without a focused text field it is not a keyboard");
    assert.equal(viewport.isKeyboardOpen(844, 500, true, 2), false, "pinch zoom shrinks the visual viewport too");
    assert.equal(viewport.isKeyboardOpen(844, 700, true, 1, 100), true);
});
