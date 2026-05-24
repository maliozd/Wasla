(function () {
    "use strict";

    function isTrendyolPlatform(value) {
        return String(value || "").toLowerCase() === "trendyolyemek";
    }

    function updateTrendyolFields() {
        const select = document.getElementById("platformSelect");
        if (!select) return;

        const show = isTrendyolPlatform(select.value);

        document.querySelectorAll(".trendyol-only").forEach(function (el) {
            el.classList.toggle("d-none", !show);
        });

        const help = document.getElementById("trendyolStoreIdHelp");
        if (help) {
            help.classList.toggle("d-none", !show);
        }
    }

    document.addEventListener("DOMContentLoaded", function () {
        const select = document.getElementById("platformSelect");
        if (!select) return;

        select.addEventListener("change", updateTrendyolFields);
        updateTrendyolFields();
    });
})();
