(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof document !== "undefined") {
        api.bind(document);
    }
})(function () {
    function syncCategory(categorySelected, subtypeSelections) {
        if (categorySelected) {
            return subtypeSelections.slice();
        }

        return subtypeSelections.map(function () { return false; });
    }

    function validate(categories) {
        var anySubtype = false;
        var categoryMissingSubtype = false;
        categories.forEach(function (category) {
            var selectedCount = category.subtypes.filter(Boolean).length;
            if (category.selected && selectedCount === 0) {
                categoryMissingSubtype = true;
            }
            if (selectedCount > 0) {
                anySubtype = true;
            }
        });

        return {
            ok: anySubtype && !categoryMissingSubtype,
            anySubtype: anySubtype,
            categoryMissingSubtype: categoryMissingSubtype
        };
    }

    function readCategories(root) {
        return Array.prototype.map.call(root.querySelectorAll("[data-category-toggle]"), function (toggle) {
            var code = toggle.getAttribute("data-category-toggle");
            var subtypes = Array.prototype.map.call(
                root.querySelectorAll('input[type="checkbox"][data-category="' + code + '"]'),
                function (input) { return input.checked; });
            return { selected: toggle.checked, subtypes: subtypes };
        });
    }

    function applyPanelState(root) {
        Array.prototype.forEach.call(root.querySelectorAll("[data-category-toggle]"), function (toggle) {
            var code = toggle.getAttribute("data-category-toggle");
            var panel = root.querySelector('[data-category-panel="' + code + '"]');
            if (!panel) return;
            panel.classList.toggle("is-collapsed", !toggle.checked);
        });
    }

    function bind(doc) {
        var root = doc.getElementById("signupBusinessTypes");
        var form = root ? root.closest("form") : null;
        if (!root || !form) return;

        function subtypeInputs(code) {
            return root.querySelectorAll('input[type="checkbox"][data-category="' + code + '"]');
        }

        Array.prototype.forEach.call(root.querySelectorAll("[data-category-toggle]"), function (toggle) {
            var code = toggle.getAttribute("data-category-toggle");
            var inputs = subtypeInputs(code);
            var anySelected = Array.prototype.some.call(inputs, function (input) { return input.checked; });
            if (anySelected) toggle.checked = true;

            toggle.addEventListener("change", function () {
                var current = Array.prototype.map.call(inputs, function (input) { return input.checked; });
                var next = syncCategory(toggle.checked, current);
                Array.prototype.forEach.call(inputs, function (input, index) {
                    input.checked = next[index];
                });
                applyPanelState(root);
            });
        });

        applyPanelState(root);

        form.addEventListener("submit", function (event) {
            var error = doc.getElementById("signupBusinessTypeClientError");
            var configEl = doc.getElementById("signupFormConfig");
            var config = {};
            try {
                config = configEl ? JSON.parse(configEl.textContent || "{}") : {};
            } catch (e) {
                config = {};
            }

            var result = validate(readCategories(root));
            if (result.ok) {
                if (error) {
                    error.textContent = "";
                    error.classList.add("d-none");
                }
                return;
            }

            event.preventDefault();
            if (!error) return;
            error.textContent = result.categoryMissingSubtype
                ? (config.businessCategorySubtypeRequiredText || "")
                : (config.businessSubtypeRequiredText || "");
            error.classList.remove("d-none");
        });
    }

    return {
        syncCategory: syncCategory,
        validate: validate,
        bind: bind
    };
});
