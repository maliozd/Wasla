// Searching the signup address lists (city, district, neighbourhood, street). Pure functions, no DOM: the page's
// combobox (signup-combobox.js) and the Node tests use the same code.
//
// Matching is Turkish-aware and forgiving: case is ignored with the Turkish rules (I/ı and İ/i), and the Turkish
// letters also match their plain forms, so "kadikoy" finds "Kadıköy" and "caferaga" finds "CAFERAĞA". The key is only
// for comparing: labels and ids are never changed. The key keeps one character per character of the label, so a match
// can be marked in the label itself.
(function (root, factory) {
    var api = factory();
    if (typeof module === "object" && module.exports) {
        module.exports = api;
    } else {
        root.WaslaAddressSearch = api;
    }
})(typeof self !== "undefined" ? self : this, function () {
    "use strict";

    var TURKISH = {
        "I": "i", "ı": "i", "İ": "i", "i": "i",
        "Ç": "c", "ç": "c", "Ğ": "g", "ğ": "g", "Ö": "o", "ö": "o", "Ş": "s", "ş": "s", "Ü": "u", "ü": "u",
        "Â": "a", "â": "a", "Î": "i", "î": "i", "Û": "u", "û": "u"
    };

    function foldChar(ch) {
        if (Object.prototype.hasOwnProperty.call(TURKISH, ch)) {
            return TURKISH[ch];
        }
        var lower = ch.toLowerCase();
        if (lower.length !== 1) {
            return ch;
        }
        // Other accented letters match their base letter; one character in, one character out.
        var base = lower.normalize ? lower.normalize("NFD").charAt(0) : lower;
        return /\s/.test(base) ? " " : base;
    }

    // The comparison key of a text: same length as the text, whitespace as single spaces.
    function fold(text) {
        var value = String(text == null ? "" : text);
        var out = "";
        for (var i = 0; i < value.length; i++) {
            out += foldChar(value.charAt(i));
        }
        return out;
    }

    // The query's key: trimmed, runs of spaces collapsed.
    function foldQuery(text) {
        return fold(text).replace(/\s+/g, " ").trim();
    }

    function isWordStart(key, index) {
        if (index === 0) {
            return true;
        }
        return !/[0-9a-z]/.test(key.charAt(index - 1));
    }

    // Where the query matches a key and how well: 0 the start, 1 the start of a later word, 2 anywhere inside,
    // 3 every word of the query somewhere. Null when it does not match.
    function matchKey(key, query) {
        if (!query) {
            return { tier: 0, index: -1, length: 0 };
        }
        var index = key.indexOf(query);
        if (index === 0) {
            return { tier: 0, index: 0, length: query.length };
        }
        if (index > 0) {
            var from = index;
            while (from >= 0) {
                if (isWordStart(key, from)) {
                    return { tier: 1, index: from, length: query.length };
                }
                from = key.indexOf(query, from + 1);
            }
            return { tier: 2, index: index, length: query.length };
        }
        var words = query.split(" ");
        if (words.length > 1 && words.every(function (word) { return key.indexOf(word) >= 0; })) {
            return { tier: 3, index: key.indexOf(words[0]), length: words[0].length };
        }
        return null;
    }

    // items: [{ key, ... }] in the page's order. Returns the matching items with their match, best tier first and the
    // page's order within a tier, so the list keeps its useful order (plate order, catalog order).
    function filter(items, queryText) {
        var query = foldQuery(queryText);
        var matches = [];
        for (var i = 0; i < items.length; i++) {
            var match = matchKey(items[i].key, query);
            if (match) {
                matches.push({ item: items[i], match: match, order: i });
            }
        }
        matches.sort(function (a, b) {
            return a.match.tier - b.match.tier || a.order - b.order;
        });
        return matches;
    }

    // The item whose label is the typed text, ignoring case and Turkish letter forms, if exactly one is.
    function findEquivalent(items, text) {
        var query = foldQuery(text);
        if (!query) {
            return null;
        }
        var found = null;
        for (var i = 0; i < items.length; i++) {
            if (items[i].key.replace(/\s+/g, " ").trim() === query) {
                if (found) {
                    return null;
                }
                found = items[i];
            }
        }
        return found;
    }

    // Splits a label at its match: { before, match, after }, for marking the matched part.
    function split(label, match) {
        var text = String(label == null ? "" : label);
        if (!match || match.index < 0 || match.length <= 0) {
            return { before: text, match: "", after: "" };
        }
        return {
            before: text.slice(0, match.index),
            match: text.slice(match.index, match.index + match.length),
            after: text.slice(match.index + match.length)
        };
    }

    return {
        fold: fold,
        foldQuery: foldQuery,
        matchKey: matchKey,
        filter: filter,
        findEquivalent: findEquivalent,
        split: split
    };
});
