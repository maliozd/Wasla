(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupReview = api;
    }
})(function () {
    "use strict";

    // How the review step words what was entered (WAS-194, Figma D4/M4). Values are shown as typed (user input is
    // never translated); only the labels around them come from the localized templates.

    function format(template, args) {
        return String(template == null ? "" : template).replace(/\{(\d+)\}/g, function (match, index) {
            var value = args[Number(index)];
            return value == null ? "" : String(value);
        });
    }

    function clean(value) {
        return typeof value === "string" ? value.trim() : "";
    }

    // Two lines: street, building, floor and door; then neighbourhood, postal code with district, and city.
    // "Moda Cad., No 18, Kat 2, Daire 4" / "Caferağa Mah., 34710 Kadıköy / İstanbul".
    function composeAddress(parts, messages) {
        parts = parts || {};
        messages = messages || {};

        var first = [clean(parts.street)];
        if (clean(parts.building)) {
            first.push(format(messages.building || "{0}", [clean(parts.building)]));
        }
        if (clean(parts.floor)) {
            first.push(format(messages.floor || "{0}", [clean(parts.floor)]));
        }
        if (clean(parts.door)) {
            first.push(format(messages.door || "{0}", [clean(parts.door)]));
        }

        var place = [clean(parts.postalCode), clean(parts.district)].filter(Boolean).join(" ");
        var region = [place, clean(parts.city)].filter(Boolean).join(" / ");
        var second = [clean(parts.neighborhood), region].filter(Boolean);

        return [first.filter(Boolean).join(", "), second.join(", ")].filter(Boolean).join("\n");
    }

    // "Dünya Mutfağı: Sushi, Uzak Doğu" per category with a selected subtype, one category per line.
    function businessTypes(categories, template) {
        return (categories || [])
            .map(function (category) {
                var chosen = (category.subtypes || []).filter(Boolean);
                return chosen.length ? format(template || "{0}: {1}", [category.label, chosen.join(", ")]) : "";
            })
            .filter(Boolean)
            .join("\n");
    }

    // "+90 531 324 12 45" for a number typed under the fixed +90 prefix; empty when nothing was typed.
    function phone(value, countryCode) {
        var typed = clean(value);
        return typed ? (countryCode || "+90") + " " + typed : "";
    }

    return {
        format: format,
        composeAddress: composeAddress,
        businessTypes: businessTypes,
        phone: phone
    };
});
