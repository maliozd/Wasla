const test = require("node:test");
const assert = require("node:assert/strict");

const phone = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-phone-format.js");

// Types the text one character at a time at the end of the field, as the page formats after every input.
function typeInto(text) {
    let value = "";
    for (const character of text) {
        value = phone.formatPhoneValue(value + character, value.length + 1).value;
    }
    return value;
}

test("a landline is grouped as it is typed, and a digit past the 10th stays in sight instead of being cut", () => {
    assert.equal(typeInto("2"), "2");
    assert.equal(typeInto("212"), "212");
    assert.equal(typeInto("2123"), "212 3");
    assert.equal(typeInto("2123456"), "212 345 6");
    assert.equal(typeInto("2123456789"), "212 345 67 89");
    assert.equal(phone.isTooLong("212 345 67 89"), false);
    assert.equal(typeInto("21234567890"), "212 345 67 89 0");
    assert.equal(phone.normalizeTurkishLocalDigits("212 345 67 89 0"), "21234567890");
    assert.equal(phone.isTooLong("212 345 67 89 0"), true);
});

test("a pasted number that is too long keeps every digit, also after its prefix goes, and is reported as too long", () => {
    const cases = [
        ["+90 212 345 67 89 12", "212 345 67 89 12", "212345678912"],
        ["0212 345 67 89 dahili 123", "212 345 67 89 123", "2123456789123"],
        ["0090 532 123 45 67 8", "532 123 45 67 8", "53212345678"],
        ["53212345678", "532 123 45 67 8", "53212345678"]
    ];
    for (const [pasted, shown, digits] of cases) {
        assert.equal(phone.formatPhoneValue(pasted).value, shown, pasted);
        assert.equal(phone.normalizeTurkishLocalDigits(pasted), digits, pasted);
        assert.equal(phone.isTooLong(pasted), true, pasted);
        // Formatting again (blur, a type switch, a restored draft) changes nothing.
        assert.equal(phone.formatPhoneValue(shown).value, shown, shown);
    }
    // A digit typed after a complete mobile is kept, not swallowed.
    assert.equal(typeInto("053212345678"), "532 123 45 67 8");
});

test("a short number is kept as typed and is not too long", () => {
    assert.equal(phone.formatPhoneValue("444 12 34").value, "444 123 4");
    assert.equal(phone.normalizeTurkishLocalDigits("444 12 34"), "4441234");
    assert.equal(phone.isTooLong("444 12 34"), false);
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
    // An extra digit typed in the middle of a full number pushes the last one into its own group; the caret follows it.
    assert.deepEqual(phone.formatPhoneValue("212 3945 67 89", 6), { value: "212 394 56 78 9", caret: 6 });
});

test("the landline example uses the city's area code only as an example", () => {
    assert.equal(phone.landlineExample("264"), "+90 264 345 67 89");
    assert.equal(phone.landlineExample("216"), "+90 216 345 67 89");
    assert.equal(phone.landlineExample(null), "+90 212 345 67 89");
    assert.equal(phone.landlineExample("21"), "+90 212 345 67 89");
});
