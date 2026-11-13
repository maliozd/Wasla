const test = require("node:test");
const assert = require("node:assert/strict");

const search = require("../../../src/Wasla.Web/wwwroot/js/signup/signup-address-search.js");

const items = labels => labels.map((label, index) => ({ id: String(index + 1), label, key: search.fold(label) }));
const labels = matches => matches.map(entry => entry.item.label);

test("the key ignores case with the Turkish rules and matches Turkish letters to their plain forms", () => {
    assert.equal(search.fold("İSTANBUL"), search.fold("istanbul"));
    assert.equal(search.fold("ISPARTA"), search.fold("ıSPARTA"));
    assert.equal(search.fold("Kadıköy"), "kadikoy");
    assert.equal(search.fold("CAFERAĞA"), "caferaga");
    assert.equal(search.fold("Şişli Çeşme Gölü"), "sisli cesme golu");
    assert.equal(search.fold("Hâkim"), "hakim");
});

test("the key keeps one character per character of the label, so a match can be marked in the label", () => {
    for (const label of ["İSTANBUL", "Kadıköy", "MODA SAHİL YOLU Sokak", "Ağrı", "19 MAYIS"]) {
        assert.equal(search.fold(label).length, label.length, label);
    }
    const [first] = search.filter(items(["MODA Cadde"]), "mod");
    assert.deepEqual(search.split("MODA Cadde", first.match), { before: "", match: "MOD", after: "A Cadde" });
    const [inside] = search.filter(items(["KÜÇÜKMODA BURNU Sokak"]), "moda");
    assert.deepEqual(search.split("KÜÇÜKMODA BURNU Sokak", inside.match), { before: "KÜÇÜK", match: "MODA", after: " BURNU Sokak" });
});

test("searching never changes a label or an id", () => {
    const list = items(["İstanbul", "İzmir"]);
    const [match] = search.filter(list, "ISTANBUL");
    assert.equal(match.item.label, "İstanbul");
    assert.equal(match.item.id, "1");
});

test("matches at the start come first, then at a later word, then inside a word, each in the page's order", () => {
    const list = items(["KÜÇÜKMODA BURNU Sokak", "MODA Cadde", "ESKİ MODA Sokak", "MODA BOSTAN Sokak", "BAĞDAT Caddesi"]);
    assert.deepEqual(labels(search.filter(list, "moda")),
        ["MODA Cadde", "MODA BOSTAN Sokak", "ESKİ MODA Sokak", "KÜÇÜKMODA BURNU Sokak"]);
});

test("an empty query keeps every item in the page's order", () => {
    const list = items(["Adana", "Adıyaman", "Afyonkarahisar"]);
    assert.deepEqual(labels(search.filter(list, "  ")), ["Adana", "Adıyaman", "Afyonkarahisar"]);
});

test("a query of several words finds names that hold every word, in any order", () => {
    const list = items(["ATATÜRK Caddesi", "MODA Cadde", "CADDE BOSTAN Sokak"]);
    assert.deepEqual(labels(search.filter(list, "cadde moda")), ["MODA Cadde"]);
    assert.deepEqual(labels(search.filter(list, "zzz")), []);
});

test("typed text that is a catalog name in another case or spelling finds that one item", () => {
    const list = items(["CAFERAĞA", "OSMANAĞA"]);
    assert.equal(search.findEquivalent(list, " caferağa ").label, "CAFERAĞA");
    assert.equal(search.findEquivalent(list, "caferaga").label, "CAFERAĞA");
    assert.equal(search.findEquivalent(list, "cafer"), null);
    assert.equal(search.findEquivalent(list, ""), null);
});

test("two catalog names that only differ in Turkish letter forms are not guessed between", () => {
    const list = items(["BARIŞ Sokak", "BARİŞ Sokak"]);
    assert.equal(search.findEquivalent(list, "baris sokak"), null);
    assert.equal(search.filter(list, "baris").length, 2);
});
