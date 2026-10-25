const test = require("node:test");
const assert = require("node:assert/strict");

const api = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-plan-price.js");

const plan = {
    monthly: { price: "300 TL / ay", vat: "KDV dahil" },
    yearly: { price: "2.750 TL / yıl", vat: "KDV hariç" },
    yearlySaving: null
};

test("each billing period shows its own price and VAT, never derived from the other", () => {
    assert.deepEqual(api.forPeriod(plan, false), { price: "300 TL / ay", vat: "KDV dahil", saving: "" });
    assert.deepEqual(api.forPeriod(plan, true), { price: "2.750 TL / yıl", vat: "KDV hariç", saving: "" });
});

test("a period without a price keeps the server's not-set text instead of borrowing the other period", () => {
    const monthlyOnly = {
        monthly: { price: "300 TL / ay", vat: "KDV dahil" },
        yearly: { price: "Bu planın fiyatı henüz belirlenmedi.", vat: null },
        yearlySaving: null
    };

    const yearly = api.forPeriod(monthlyOnly, true);

    assert.equal(yearly.price, "Bu planın fiyatı henüz belirlenmedi.");
    assert.equal(yearly.vat, "");
    assert.doesNotMatch(yearly.price, /300/);
});

test("the yearly saving appears only for the yearly period and only when the server found one", () => {
    const saving = { ...plan, yearlySaving: "Yıllık ödeme, 12 aylık ödemeden 850 TL daha az." };

    assert.equal(api.forPeriod(saving, true).saving, "Yıllık ödeme, 12 aylık ödemeden 850 TL daha az.");
    assert.equal(api.forPeriod(saving, false).saving, "");
    assert.equal(api.forPeriod(plan, true).saving, "");
});

test("no plan shows nothing", () => {
    assert.deepEqual(api.forPeriod(null, true), { price: "", vat: "", saving: "" });
});
