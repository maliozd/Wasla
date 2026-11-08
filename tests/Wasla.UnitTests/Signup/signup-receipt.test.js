const test = require("node:test");
const assert = require("node:assert/strict");

const receipt = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-receipt.js");
const review = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-review.js");

const messages = {
    periodMonthly: "aylık",
    periodYearly: "yıllık",
    planLine: "{0} · {1}",
    priceLine: "{0}, {1}",
    periodNoteMonthly: "{0} planı, 1 aylık dönem. Her ay otomatik yenilenir.",
    periodNoteYearly: "{0} planı, 12 aylık dönem. Her 12 ayda bir otomatik yenilenir.",
    stripDetail: "{0}, {1}, {2}",
    choosePlan: "Bir plan seçin",
    priceNotSet: "Bu planın fiyatı henüz belirlenmedi."
};

// Server-prepared prices (test values, not real prices): monthly only for this plan.
const pro = {
    code: "Pro",
    name: "Pro",
    isContactSales: false,
    monthly: { price: "300 TL / ay", vat: "KDV dahil", amount: "300 TL", vatIncluded: true },
    yearly: { price: "Bu planın fiyatı henüz belirlenmedi.", vat: null, amount: null, vatIncluded: null }
};
const enterprise = { code: "Enterprise", name: "Enterprise", isContactSales: true, monthly: { price: "Teklif alın" }, yearly: { price: "Teklif alın" } };

test("the selected period's server amount and VAT text make the receipt lines", () => {
    const view = receipt.planView(pro, false, messages);

    assert.equal(view.selected, true);
    assert.equal(view.planLine, "Pro · aylık");
    assert.equal(view.priceLine, "300 TL / ay, KDV dahil");
    assert.equal(view.amount, "300 TL");
    assert.equal(view.amountSet, true);
    assert.equal(view.vat, "KDV dahil");
    assert.equal(view.periodNote, "Pro planı, 1 aylık dönem. Her ay otomatik yenilenir.");
    assert.equal(view.stripDetail, "Pro, aylık, KDV dahil");
});

test("a period without a price shows the server's not-set text and borrows nothing from the other period", () => {
    const view = receipt.planView(pro, true, messages);

    assert.equal(view.amount, "Bu planın fiyatı henüz belirlenmedi.");
    assert.equal(view.amountSet, false);
    assert.equal(view.vat, "");
    assert.equal(view.periodNote, "", "no renewal sentence without a price");
    assert.doesNotMatch(JSON.stringify(view), /300/);
    assert.equal(view.stripDetail, "Pro · yıllık");
});

test("without a self-service plan the receipt asks for one; a contact-sales plan is never selected", () => {
    const none = receipt.planView(null, false, messages);
    assert.equal(none.selected, false);
    assert.equal(none.amount, "Bir plan seçin");
    assert.equal(none.amountSet, false);

    assert.equal(receipt.findPlan([pro, enterprise], "Enterprise"), null);
    assert.equal(receipt.findPlan([pro, enterprise], "Pro"), pro);
    assert.equal(receipt.findPlan([pro], ""), null);
    assert.equal(receipt.findPlan(null, "Pro"), null);
});

test("templates take positional values and leave missing ones empty", () => {
    assert.equal(receipt.format("{0} · {1}", ["Pro", "yıllık"]), "Pro · yıllık");
    assert.equal(receipt.format("{1} / {0}", ["a", "b"]), "b / a");
    assert.equal(receipt.format("{0}{2}", ["x"]), "x");
    assert.equal(receipt.format(null, []), "");
});

test("the panel address appears once a slug is typed", () => {
    assert.equal(receipt.domainOf(" Sushim ", "app.wasla.tr"), "sushim.app.wasla.tr");
    assert.equal(receipt.domainOf("", "app.wasla.tr"), "");
    assert.equal(receipt.domainOf("sushim", ""), "");
});

test("a step shows its values once open or done and filled; an upcoming step shows its hint", () => {
    assert.equal(receipt.shouldShowValues("current", ["Sushim Kadıköy", ""]), true);
    assert.equal(receipt.shouldShowValues("done", ["", "  "]), false);
    assert.equal(receipt.shouldShowValues("upcoming", ["Sushim Kadıköy"]), false);
});

test("the review writes the address in two lines from the parts that were filled", () => {
    const words = { building: "No {0}", floor: "Kat {0}", door: "Daire {0}" };

    assert.equal(review.composeAddress({
        street: "Moda Cad.", building: "18", floor: "2", door: "4",
        neighborhood: "Caferağa Mah.", postalCode: "34710", district: "Kadıköy", city: "İstanbul"
    }, words), "Moda Cad., No 18, Kat 2, Daire 4\nCaferağa Mah., 34710 Kadıköy / İstanbul");

    assert.equal(review.composeAddress({ street: "Moda Cad.", district: "Kadıköy", city: "İstanbul" }, words),
        "Moda Cad.\nKadıköy / İstanbul");
    assert.equal(review.composeAddress({}, words), "");
});

test("the review lists each category with its chosen subtypes", () => {
    const text = review.businessTypes([
        { label: "Dünya Mutfağı", subtypes: ["Sushi", "Uzak Doğu"] },
        { label: "Kafe", subtypes: [] },
        { label: "Tatlı", subtypes: ["Pastane"] }
    ], "{0}: {1}");

    assert.equal(text, "Dünya Mutfağı: Sushi, Uzak Doğu\nTatlı: Pastane");
    assert.equal(review.businessTypes([], "{0}: {1}"), "");
});

test("a typed phone number is shown with the fixed country code", () => {
    assert.equal(review.phone("531 324 12 45", "+90"), "+90 531 324 12 45");
    assert.equal(review.phone("  ", "+90"), "");
    assert.equal(review.phone("2163304567"), "+90 2163304567");
});
