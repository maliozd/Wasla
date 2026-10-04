namespace Wasla.Infrastructure.Platform.Mock;

/// <summary>
/// Fifty composition profiles for Mengen Lokantası mock orders.
/// A scenario sets party size and basket tendencies. Products inside it still vary.
/// </summary>
internal static class MockLokantaScenarios
{
    internal static IReadOnlyList<MockScenario> All => Scenarios;

    internal static MockScenario Pick(Random rng)
    {
        var total = 0;
        foreach (var scenario in Scenarios)
            total += scenario.Weight;

        var roll = rng.Next(total);
        foreach (var scenario in Scenarios)
        {
            if (roll < scenario.Weight)
                return scenario;

            roll -= scenario.Weight;
        }

        return Scenarios[^1];
    }

    // name, weight, partyMin, partyMax, focus, mix,
    // mains, soup chance/min/max, side, salad, drink, dessert, order-note chance, style
    private static readonly MockScenario[] Scenarios =
    [
        S("01 Solo budget lunch", 6, 1, 1, MockMainFocus.Budget, MockMainMix.SinglePool, 1, 1, 0.22, 1, 1, 1, 1, 1, 0, 0, 0, 0.85, 1, 1, 0.06, 1, 1, 0.34, MockScenarioStyle.BudgetDrinks),
        S("02 Solo soup and main lunch", 5, 1, 1, MockMainFocus.Mixed, MockMainMix.SinglePool, 1, 1, 1, 1, 1, 0.55, 1, 1, 0.20, 1, 1, 0.75, 1, 1, 0.12, 1, 1, 0.36, MockScenarioStyle.None),
        S("03 Solo quick chicken meal", 5, 1, 1, MockMainFocus.Chicken, MockMainMix.SinglePool, 1, 1, 0.15, 1, 1, 0.70, 1, 1, 0, 0, 0, 0.80, 1, 1, 0.05, 1, 1, 0.32, MockScenarioStyle.None),
        S("04 Solo kuru fasulye and pilav", 6, 1, 1, MockMainFocus.LegumeBeans, MockMainMix.SinglePool, 1, 1, 0.10, 1, 1, 1, 1, 1, 0.15, 1, 1, 0.90, 1, 1, 0.05, 1, 1, 0.33, MockScenarioStyle.BudgetDrinks),
        S("05 Solo nohut and pilav", 5, 1, 1, MockMainFocus.Nohut, MockMainMix.SinglePool, 1, 1, 0.10, 1, 1, 1, 1, 1, 0.12, 1, 1, 0.88, 1, 1, 0.05, 1, 1, 0.33, MockScenarioStyle.BudgetDrinks),
        S("06 Solo premium beef lunch", 4, 1, 1, MockMainFocus.Beef, MockMainMix.SinglePool, 1, 1, 0.25, 1, 1, 0.80, 1, 1, 0.45, 1, 1, 0.70, 1, 1, 0.40, 1, 1, 0.38, MockScenarioStyle.None),
        S("07 Solo regional Mengen meal", 4, 1, 1, MockMainFocus.Regional, MockMainMix.SinglePool, 1, 1, 0.35, 1, 1, 0.75, 1, 1, 0.30, 1, 1, 0.65, 1, 1, 0.45, 1, 1, 0.36, MockScenarioStyle.RegionalDessert),
        S("08 Solo soup-only light meal", 4, 1, 1, MockMainFocus.SoupOnly, MockMainMix.SinglePool, 0, 0, 1, 1, 2, 0, 0, 0, 0, 0, 0, 0.55, 1, 1, 0, 0, 0, 0.30, MockScenarioStyle.None),
        S("09 Solo late-night kelle paça", 4, 1, 1, MockMainFocus.Offal, MockMainMix.SinglePool, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0.70, 1, 1, 0, 0, 0, 0.28, MockScenarioStyle.OffalDrinks),
        S("10 Solo vegetarian lokanta meal", 4, 1, 1, MockMainFocus.Vegetarian, MockMainMix.SinglePool, 1, 1, 0.30, 1, 1, 0.65, 1, 1, 0.55, 1, 1, 0.70, 1, 1, 0.18, 1, 1, 0.34, MockScenarioStyle.None),

        S("11 Two-person budget lunch", 5, 2, 2, MockMainFocus.Budget, MockMainMix.SinglePool, 2, 2, 0.25, 1, 1, 1, 1, 1, 0.35, 1, 1, 1, 2, 2, 0.12, 1, 1, 0.38, MockScenarioStyle.BudgetDrinks),
        S("12 Two-person standard lunch", 5, 2, 2, MockMainFocus.Mixed, MockMainMix.SinglePool, 2, 2, 0.30, 1, 1, 1, 1, 2, 0.50, 1, 1, 1, 2, 2, 0.32, 1, 1, 0.40, MockScenarioStyle.None),
        S("13 Two-person soup and main", 4, 2, 2, MockMainFocus.Mixed, MockMainMix.SinglePool, 2, 2, 1, 2, 2, 1, 1, 2, 0.25, 1, 1, 1, 2, 2, 0.18, 1, 1, 0.38, MockScenarioStyle.None),
        S("14 Two-person regional Mengen tasting", 4, 2, 2, MockMainFocus.Regional, MockMainMix.SinglePool, 2, 2, 0.40, 1, 1, 1, 1, 2, 0.40, 1, 1, 1, 2, 2, 0.75, 1, 1, 0.40, MockScenarioStyle.RegionalDessert),
        S("15 Two-person meat-heavy dinner", 4, 2, 2, MockMainFocus.Meat, MockMainMix.SinglePool, 2, 2, 0.15, 1, 1, 1, 1, 2, 0.45, 1, 1, 1, 2, 2, 0.28, 1, 1, 0.40, MockScenarioStyle.None),
        S("16 Two-person chicken dinner", 4, 2, 2, MockMainFocus.Chicken, MockMainMix.SinglePool, 2, 2, 0.20, 1, 1, 1, 1, 1, 0.20, 1, 1, 1, 2, 2, 0.22, 1, 1, 0.36, MockScenarioStyle.None),
        S("17 Two-person vegetarian meal", 4, 2, 2, MockMainFocus.Vegetarian, MockMainMix.SinglePool, 2, 2, 0.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 0.30, 1, 1, 0.36, MockScenarioStyle.None),
        S("18 Two-person quick office lunch", 5, 2, 2, MockMainFocus.Mixed, MockMainMix.SinglePool, 2, 2, 0.15, 1, 1, 0.40, 1, 1, 0.10, 1, 1, 1, 2, 2, 0.08, 1, 1, 0.66, MockScenarioStyle.Office),
        S("19 Two-person shared dessert order", 4, 2, 2, MockMainFocus.Mixed, MockMainMix.SinglePool, 2, 2, 0.15, 1, 1, 1, 1, 1, 0.30, 1, 1, 1, 2, 2, 1, 1, 2, 0.42, MockScenarioStyle.None),
        S("20 Two-person late evening meal", 4, 2, 2, MockMainFocus.Grill, MockMainMix.SinglePool, 2, 2, 0.10, 1, 1, 0.70, 1, 1, 0.25, 1, 1, 1, 2, 2, 0.15, 1, 1, 0.36, MockScenarioStyle.None),

        S("21 Three-person office lunch", 6, 3, 3, MockMainFocus.Mixed, MockMainMix.SinglePool, 3, 3, 0.20, 1, 1, 1, 1, 1, 0.20, 1, 1, 1, 3, 3, 0.12, 1, 1, 0.66, MockScenarioStyle.Office),
        S("22 Three-person mixed lokanta meal", 5, 3, 3, MockMainFocus.Mixed, MockMainMix.SinglePool, 3, 3, 0.55, 1, 2, 1, 1, 2, 1, 1, 1, 1, 3, 3, 0.40, 1, 1, 0.42, MockScenarioStyle.None),
        S("23 Three-person soup-heavy order", 4, 3, 3, MockMainFocus.Mixed, MockMainMix.SinglePool, 2, 2, 1, 2, 3, 1, 1, 1, 0.20, 1, 1, 1, 3, 3, 0.12, 1, 1, 0.40, MockScenarioStyle.None),
        S("24 Three-person grill order", 4, 3, 3, MockMainFocus.Grill, MockMainMix.SinglePool, 3, 3, 0.10, 1, 1, 1, 1, 2, 1, 1, 1, 1, 3, 3, 0.22, 1, 1, 0.38, MockScenarioStyle.None),
        S("25 Three-person regional selection", 4, 3, 3, MockMainFocus.Regional, MockMainMix.SinglePool, 2, 3, 0.45, 1, 1, 1, 1, 2, 0.50, 1, 1, 1, 3, 3, 0.65, 1, 1, 0.40, MockScenarioStyle.RegionalDessert),
        S("26 Three-person economical order", 5, 3, 3, MockMainFocus.Budget, MockMainMix.SinglePool, 3, 3, 0.20, 1, 1, 1, 1, 2, 0.40, 1, 1, 1, 3, 3, 0.06, 1, 1, 0.36, MockScenarioStyle.BudgetDrinks),
        S("27 Three-person premium order", 4, 3, 3, MockMainFocus.Premium, MockMainMix.SinglePool, 3, 3, 0.30, 1, 1, 1, 2, 2, 1, 1, 1, 1, 3, 3, 1, 1, 2, 0.44, MockScenarioStyle.None),
        S("28 Three-person no-dessert quick order", 4, 3, 3, MockMainFocus.Mixed, MockMainMix.SinglePool, 3, 3, 0.15, 1, 1, 1, 1, 2, 0.35, 1, 1, 1, 3, 3, 0, 0, 0, 0.40, MockScenarioStyle.None),
        S("29 Three-person dessert-added meal", 4, 3, 3, MockMainFocus.Mixed, MockMainMix.SinglePool, 3, 3, 0.25, 1, 1, 1, 1, 2, 0.40, 1, 1, 1, 3, 3, 1, 2, 3, 0.42, MockScenarioStyle.None),
        S("30 Three-person mixed dietary preferences", 4, 3, 3, MockMainFocus.Mixed, MockMainMix.VegChickenMeat, 3, 3, 0.20, 1, 1, 1, 1, 2, 1, 1, 1, 1, 3, 3, 0.35, 1, 1, 0.40, MockScenarioStyle.None),

        S("31 Four-person family lunch", 5, 4, 4, MockMainFocus.Mixed, MockMainMix.SinglePool, 4, 4, 0.55, 1, 2, 1, 2, 2, 1, 1, 1, 1, 4, 4, 0.50, 1, 1, 0.42, MockScenarioStyle.None),
        S("32 Four-person family dinner", 4, 4, 4, MockMainFocus.Mixed, MockMainMix.SinglePool, 4, 4, 0.35, 1, 1, 1, 2, 2, 1, 1, 2, 1, 4, 4, 0.75, 1, 2, 0.44, MockScenarioStyle.None),
        S("33 Four-person mixed meat and chicken", 4, 4, 4, MockMainFocus.Mixed, MockMainMix.MeatAndChicken, 4, 4, 0.20, 1, 1, 1, 2, 2, 0.60, 1, 1, 1, 4, 4, 0.40, 1, 1, 0.40, MockScenarioStyle.None),
        S("34 Four-person traditional pot meal", 4, 4, 4, MockMainFocus.Pot, MockMainMix.SinglePool, 4, 4, 0.25, 1, 1, 1, 2, 2, 1, 1, 1, 1, 4, 4, 0.28, 1, 1, 0.40, MockScenarioStyle.BudgetDrinks),
        S("35 Four-person grill-heavy order", 4, 4, 4, MockMainFocus.Grill, MockMainMix.SinglePool, 4, 4, 0.10, 1, 1, 1, 2, 2, 1, 1, 1, 1, 4, 4, 0.32, 1, 1, 0.38, MockScenarioStyle.None),
        S("36 Four-person regional Mengen meal", 4, 4, 4, MockMainFocus.Regional, MockMainMix.SinglePool, 3, 4, 0.40, 1, 1, 1, 1, 2, 0.70, 1, 1, 1, 4, 4, 0.80, 1, 2, 0.42, MockScenarioStyle.RegionalDessert),
        S("37 Four-person budget family meal", 4, 4, 4, MockMainFocus.Budget, MockMainMix.SinglePool, 4, 4, 0.30, 1, 1, 1, 2, 2, 1, 1, 1, 1, 4, 4, 0.15, 1, 1, 0.36, MockScenarioStyle.BudgetDrinks),
        S("38 Four-person premium family meal", 4, 4, 4, MockMainFocus.Premium, MockMainMix.SinglePool, 4, 4, 0.25, 1, 1, 1, 2, 3, 1, 1, 2, 1, 4, 4, 1, 2, 2, 0.46, MockScenarioStyle.None),
        S("39 Four-person meal with children", 4, 4, 4, MockMainFocus.Mild, MockMainMix.SinglePool, 4, 4, 0.30, 1, 1, 1, 2, 2, 1, 1, 1, 1, 4, 4, 0.85, 1, 2, 0.48, MockScenarioStyle.MildMenu),
        S("40 Four-person dessert-heavy family order", 4, 4, 4, MockMainFocus.Mixed, MockMainMix.SinglePool, 4, 4, 0.20, 1, 1, 1, 1, 2, 0.50, 1, 1, 1, 4, 4, 1, 3, 4, 0.44, MockScenarioStyle.None),

        S("41 Five-person family dinner", 4, 5, 5, MockMainFocus.Mixed, MockMainMix.SinglePool, 5, 5, 0.40, 1, 2, 1, 2, 3, 1, 1, 2, 1, 5, 5, 0.80, 1, 2, 0.45, MockScenarioStyle.None),
        S("42 Five-person office lunch", 5, 5, 5, MockMainFocus.Mixed, MockMainMix.SinglePool, 5, 5, 0.25, 1, 1, 1, 1, 2, 0.25, 1, 1, 1, 5, 5, 0.18, 1, 1, 0.68, MockScenarioStyle.Office),
        S("43 Five-person economical lunch", 4, 5, 5, MockMainFocus.Budget, MockMainMix.SinglePool, 5, 5, 0.20, 1, 1, 1, 2, 2, 1, 1, 1, 1, 5, 5, 0.10, 1, 1, 0.40, MockScenarioStyle.BudgetDrinks),
        S("44 Five-person regional feast", 4, 5, 5, MockMainFocus.Regional, MockMainMix.SinglePool, 4, 5, 0.35, 1, 1, 1, 2, 2, 1, 1, 1, 1, 5, 5, 1, 2, 2, 0.46, MockScenarioStyle.RegionalDessert),
        S("45 Five-person mixed grill and sides", 4, 5, 5, MockMainFocus.Mixed, MockMainMix.GrillAndEveryday, 5, 5, 0.15, 1, 1, 1, 2, 3, 1, 1, 1, 1, 5, 5, 0.40, 1, 1, 0.42, MockScenarioStyle.None),
        S("46 Six-person office order", 4, 6, 6, MockMainFocus.Mixed, MockMainMix.SinglePool, 6, 6, 0.20, 1, 1, 1, 2, 2, 0.30, 1, 1, 1, 6, 6, 0.22, 1, 1, 0.70, MockScenarioStyle.Office),
        S("47 Six-person family gathering", 4, 6, 6, MockMainFocus.Mixed, MockMainMix.SinglePool, 6, 6, 0.45, 1, 2, 1, 3, 3, 1, 2, 2, 1, 6, 6, 0.85, 2, 2, 0.46, MockScenarioStyle.None),
        S("48 Seven-to-eight-person workplace order", 4, 7, 8, MockMainFocus.Mixed, MockMainMix.SinglePool, 7, 8, 0.25, 1, 2, 1, 2, 3, 1, 1, 1, 1, 7, 8, 0.40, 1, 2, 0.72, MockScenarioStyle.Office),
        S("49 Large eight-to-ten-person group meal", 4, 8, 10, MockMainFocus.Mixed, MockMainMix.SinglePool, 8, 10, 0.30, 1, 2, 1, 3, 4, 1, 2, 2, 1, 8, 10, 0.88, 2, 3, 0.58, MockScenarioStyle.None),
        S("50 High-value business team meal", 4, 6, 8, MockMainFocus.Premium, MockMainMix.SinglePool, 6, 8, 0.25, 1, 1, 1, 3, 3, 1, 2, 2, 1, 6, 8, 1, 2, 3, 0.74, MockScenarioStyle.Office)
    ];

    private static MockScenario S(
        string name,
        int weight,
        int partyMin,
        int partyMax,
        MockMainFocus focus,
        MockMainMix mix,
        int mainMin,
        int mainMax,
        double soupChance,
        int soupMin,
        int soupMax,
        double sideChance,
        int sideMin,
        int sideMax,
        double saladChance,
        int saladMin,
        int saladMax,
        double drinkChance,
        int drinkMin,
        int drinkMax,
        double dessertChance,
        int dessertMin,
        int dessertMax,
        double orderNoteChance,
        MockScenarioStyle style) =>
        new(
            name,
            weight,
            partyMin,
            partyMax,
            focus,
            mix,
            mainMin,
            mainMax,
            soupChance,
            soupMin,
            soupMax,
            sideChance,
            sideMin,
            sideMax,
            saladChance,
            saladMin,
            saladMax,
            drinkChance,
            drinkMin,
            drinkMax,
            dessertChance,
            dessertMin,
            dessertMax,
            orderNoteChance,
            style);
}

internal enum MockMainFocus
{
    Mixed,
    Budget,
    LegumeBeans,
    Nohut,
    Chicken,
    Beef,
    Meat,
    Regional,
    Vegetarian,
    Grill,
    Offal,
    Pot,
    Premium,
    Mild,
    SoupOnly
}

internal enum MockMainMix
{
    SinglePool,
    MeatAndChicken,
    VegChickenMeat,
    GrillAndEveryday
}

[Flags]
internal enum MockScenarioStyle
{
    None = 0,
    Office = 1,
    RegionalDessert = 2,
    BudgetDrinks = 4,
    MildMenu = 8,
    OffalDrinks = 16
}

internal sealed record MockScenario(
    string Name,
    int Weight,
    int PartyMin,
    int PartyMax,
    MockMainFocus Focus,
    MockMainMix Mix,
    int MainMin,
    int MainMax,
    double SoupChance,
    int SoupMin,
    int SoupMax,
    double SideChance,
    int SideMin,
    int SideMax,
    double SaladChance,
    int SaladMin,
    int SaladMax,
    double DrinkChance,
    int DrinkMin,
    int DrinkMax,
    double DessertChance,
    int DessertMin,
    int DessertMax,
    double OrderNoteChance,
    MockScenarioStyle Style);
