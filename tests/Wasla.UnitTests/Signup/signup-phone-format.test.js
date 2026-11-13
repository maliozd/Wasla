const test = require("node:test");
const assert = require("node:assert/strict");

const phone = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-phone-format.js");

// Types the text one character at a time at the end of the field, as the page does: a keystroke that would give more
// than 10 national digits is refused, and the field is formatted after every input.
function typeInto(text) {
    let value = "";
    for (const character of text) {
        if (phone.wouldOverflow(value, value.length, value.length, character)) {
            continue;
        }
        value = phone.formatPhoneValue(value + character, value.length + 1).value;
    }
    return value;
}

test("a landline is grouped as it is typed and stops at 10 national digits", () => {
    assert.equal(typeInto("2"), "2");
    assert.equal(typeInto("212"), "212");
    assert.equal(typeInto("2123"), "212 3");
    assert.equal(typeInto("2123456"), "212 345 6");
    assert.equal(typeInto("2123456789"), "212 345 67 89");
    assert.equal(typeInto("21234567890"), "212 345 67 89");
    assert.equal(typeInto("5441312312999"), "544 131 23 12");
});

test("a digit typed into a full number is refused, so no digit already there is pushed out", () => {
    const full = "544 131 23 12";
    // At the end, in the middle, at the start (a digit that is no prefix).
    assert.equal(phone.wouldOverflow(full, full.length, full.length, "9"), true);
    assert.equal(phone.wouldOverflow(full, 4, 4, "9"), true);
    assert.equal(phone.wouldOverflow(full, 0, 0, "5"), true);
    // A space or a selection replaced by as many digits fits.
    assert.equal(phone.wouldOverflow(full, 3, 3, " "), false);
    assert.equal(phone.wouldOverflow(full, 4, 7, "9"), false);
    assert.equal(phone.wouldOverflow(full, 4, 5, "9"), false);
    // A trunk zero typed in front fits: it comes off as a prefix.
    assert.equal(phone.wouldOverflow(full, 0, 0, "0"), false);
    assert.deepEqual(phone.formatPhoneValue("0" + full, 1), { value: full, caret: 0 });
    // Fewer digits: any digit fits.
    assert.equal(phone.wouldOverflow("544 131 23 1", 4, 4, "9"), false);
});

test("a selection replaced, a digit deleted or Backspaced, the caret follows its digit", () => {
    // "131" selected and replaced by "9".
    assert.deepEqual(phone.formatPhoneValue("544 9 23 12", 5), { value: "544 923 12", caret: 5 });
    // The "1" after the first space deleted (Delete): caret stays before the next digit.
    assert.deepEqual(phone.formatPhoneValue("544 31 23 12", 4), { value: "544 312 31 2", caret: 3 });
    // The last digit Backspaced.
    assert.deepEqual(phone.formatPhoneValue("544 131 23 1", 12), { value: "544 131 23 1", caret: 12 });
});

test("a longer paste keeps its first 10 national digits after its prefix goes, as asked", () => {
    const cases = [
        ["+90 544 131 23 12 99", "544 131 23 12", "5441312312"],
        ["0090 544 131 23 12 9", "544 131 23 12", "5441312312"],
        ["0544 131 23 12 9", "544 131 23 12", "5441312312"],
        ["54413123129", "544 131 23 12", "5441312312"],
        ["0212 345 67 89 dahili 123", "212 345 67 89", "2123456789"]
    ];
    for (const [pasted, shown, digits] of cases) {
        const result = phone.formatPhoneValue(pasted, pasted.length);
        assert.deepEqual(result, { value: shown, caret: shown.length }, pasted);
        assert.equal(phone.normalizeTurkishLocalDigits(pasted), digits, pasted);
        // Formatting again (blur, a type switch, a restored draft) changes nothing.
        assert.equal(phone.formatPhoneValue(shown).value, shown, shown);
    }
    // Pasted over a selection in the middle: the digits after it that no longer fit go, the caret ends after the paste.
    assert.deepEqual(phone.formatPhoneValue("544 1" + "999" + "31 23 12", 8), { value: "544 199 93 12", caret: 9 });
});

test("a short number is kept as typed", () => {
    assert.equal(phone.formatPhoneValue("444 12 34").value, "444 123 4");
    assert.equal(phone.normalizeTurkishLocalDigits("444 12 34"), "4441234");
    assert.equal(typeInto("4441234"), "444 123 4");
});

test("a trunk zero or country code typed or pasted in front is removed, never stored twice", () => {
    for (const written of ["0212 345 67 89", "+90 212 345 67 89", "0090 212 345 67 89", "(0212) 345-67-89", "+90 (212) 345 67 89", "90 212 345 67 89"]) {
        assert.equal(phone.normalizeTurkishLocalDigits(written), "2123456789", written);
        assert.equal(phone.formatPhoneValue(written).value, "212 345 67 89", written);
    }
    assert.equal(phone.normalizeTurkishLocalDigits("+90 532 123 45 67"), "5321234567");
});

test("a number with a trunk zero or country code can be typed digit by digit", () => {
    assert.equal(typeInto("02123456789"), "212 345 67 89");
    assert.equal(typeInto("+902123456789"), "212 345 67 89");
    assert.equal(typeInto("00902123456789"), "212 345 67 89");
    assert.equal(typeInto("05321234567"), "532 123 45 67");
});

test("a national number that starts with 90 keeps its digits", () => {
    assert.equal(phone.normalizeTurkishLocalDigits("900 123 45 67"), "9001234567");
});

test("mobile and landline share the format, so switching the type changes nothing", () => {
    for (const shown of ["212 345 67 89", "532 123 45 67", "264 123 4"]) {
        assert.equal(phone.formatPhoneValue(shown).value, shown);
        assert.equal(phone.formatPhoneValue(phone.formatPhoneValue(shown).value).value, shown);
    }
});

test("the caret stays after the digit it followed, also when a prefix in front goes", () => {
    // A digit typed in the middle of "212 345 67 8".
    assert.deepEqual(phone.formatPhoneValue("212 3945 67 8", 6), { value: "212 394 56 78", caret: 6 });
    // The trunk zero in front of the caret goes; the caret stays after "212".
    assert.deepEqual(phone.formatPhoneValue("0212 345 67 89", 4), { value: "212 345 67 89", caret: 3 });
    // A pasted full number leaves the caret at its end.
    const pasted = "+90 212 345 67 89";
    assert.deepEqual(phone.formatPhoneValue(pasted, pasted.length), { value: "212 345 67 89", caret: 13 });
    // Deleting the space before a group leaves the digits and puts the caret before the space.
    assert.deepEqual(phone.formatPhoneValue("212345 67 89", 3), { value: "212 345 67 89", caret: 3 });
});

test("the landline example uses the city's area code only as an example", () => {
    assert.equal(phone.landlineExample("264"), "+90 264 345 67 89");
    assert.equal(phone.landlineExample("216"), "+90 216 345 67 89");
    assert.equal(phone.landlineExample(null), "+90 212 345 67 89");
    assert.equal(phone.landlineExample("21"), "+90 212 345 67 89");
});
