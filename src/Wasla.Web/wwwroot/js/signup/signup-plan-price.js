(function (factory) {
    var api = factory();
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
    if (typeof window !== "undefined") {
        window.WaslaSignupPlanPrice = api;
    }
})(function () {
    // What the signup summary shows for the selected billing period (WAS-147). The server prepares each period's price
    // text from the Central Admin prices in effect; the yearly price is never derived from the monthly one, and a
    // missing price keeps the server's "not set" text instead of borrowing the other period's amount. The yearly saving
    // is shown only when the server found one in the configured prices.
    function forPeriod(plan, isYearly) {
        if (!plan) {
            return { price: "", vat: "", saving: "" };
        }

        var period = isYearly ? plan.yearly : plan.monthly;
        return {
            price: (period && period.price) || "",
            vat: (period && period.vat) || "",
            saving: isYearly ? (plan.yearlySaving || "") : ""
        };
    }

    return {
        forPeriod: forPeriod
    };
});
