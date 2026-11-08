(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupBusinessTypes = api;
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
            if (!check(doc).ok) {
                event.preventDefault();
            }
        });
    }

    // Validates the selection and shows or clears the field's error (WAS-194: the restaurant step runs it before it
    // lets the visitor continue). Returns the result with the message shown and the control to focus.
    function check(doc) {
        var root = doc.getElementById("signupBusinessTypes");
        var error = doc.getElementById("signupBusinessTypeClientError");
        if (!root) {
            return { ok: true, message: "", focusTarget: null };
        }

        var configEl = doc.getElementById("signupFormConfig");
        var config = {};
        try {
            config = configEl ? JSON.parse(configEl.textContent || "{}") : {};
        } catch (e) {
            config = {};
        }

        var result = validate(readCategories(root));
        var message = result.ok
            ? ""
            : (result.categoryMissingSubtype
                ? (config.businessCategorySubtypeRequiredText || "")
                : (config.businessSubtypeRequiredText || ""));

        if (error) {
            error.textContent = message;
            error.hidden = !message;
            error.classList.toggle("d-none", !message);
        }

        var focusTarget = null;
        if (!result.ok) {
            focusTarget = root.querySelector("[data-category-toggle]:checked") || root.querySelector("[data-category-toggle]");
            Array.prototype.some.call(root.querySelectorAll("[data-category-toggle]:checked"), function (toggle) {
                var code = toggle.getAttribute("data-category-toggle");
                var anySubtype = root.querySelector('input[type="checkbox"][data-category="' + code + '"]:checked');
                if (!anySubtype) {
                    focusTarget = root.querySelector('input[type="checkbox"][data-category="' + code + '"]') || toggle;
                    return true;
                }
                return false;
            });
        }

        return { ok: result.ok, message: message, focusTarget: focusTarget };
    }

    // The chosen subtypes per category, with their visible labels, for the review step.
    function selectedLabels(doc) {
        var root = doc.getElementById("signupBusinessTypes");
        if (!root) {
            return [];
        }

        function labelOf(input) {
            var label = input.id ? root.querySelector('label[for="' + input.id + '"]') : null;
            return label ? label.textContent.trim() : "";
        }

        return Array.prototype.map.call(root.querySelectorAll("[data-category-toggle]"), function (toggle) {
            var code = toggle.getAttribute("data-category-toggle");
            return {
                label: labelOf(toggle),
                subtypes: Array.prototype.filter.call(
                    root.querySelectorAll('input[type="checkbox"][data-category="' + code + '"]'),
                    function (input) { return input.checked; }).map(labelOf)
            };
        });
    }

    return {
        syncCategory: syncCategory,
        validate: validate,
        readCategories: readCategories,
        check: check,
        selectedLabels: selectedLabels,
        bind: bind
    };
});
