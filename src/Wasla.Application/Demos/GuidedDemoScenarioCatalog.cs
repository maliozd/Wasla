namespace Wasla.Application.Demos;

/// <summary>
/// Fixed baskets chosen from the tenant's signup subtypes.
/// The first selected code that has a basket wins. Unknown selections use the default lokanta basket.
/// </summary>
public static class GuidedDemoScenarioCatalog
{
    private static readonly Dictionary<string, GuidedDemoScenario> BySubtype =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sushi"] = Scenario("sushi", "Demo.Sushi.Note", Line("Demo.Sushi.Roll", 2, 220m), Line("Demo.Sushi.Edamame", 1, 70m)),
            ["asian"] = Scenario("sushi", "Demo.Sushi.Note", Line("Demo.Sushi.Roll", 2, 220m), Line("Demo.Sushi.Edamame", 1, 70m)),
            ["burger"] = Scenario("burger", "Demo.Burger.Note", Line("Demo.Burger.Burger", 1, 240m), Line("Demo.Burger.Fries", 1, 60m), Line("Demo.Burger.Drink", 1, 40m)),
            ["cafe"] = Scenario("cafe", "Demo.Cafe.Note", Line("Demo.Cafe.Coffee", 1, 90m), Line("Demo.Cafe.Dessert", 1, 120m)),
            ["breakfast"] = Scenario("cafe", "Demo.Cafe.Note", Line("Demo.Cafe.Coffee", 1, 90m), Line("Demo.Cafe.Dessert", 1, 120m)),
            ["pizza"] = Scenario("pizza", "Demo.Pizza.Note", Line("Demo.Pizza.Pizza", 1, 280m), Line("Demo.Pizza.Drink", 1, 35m)),
            ["pide-lahmacun"] = Scenario("pizza", "Demo.Pizza.Note", Line("Demo.Pizza.Pizza", 1, 280m), Line("Demo.Pizza.Drink", 1, 35m)),
            ["doner"] = Scenario("doner", "Demo.Doner.Note", Line("Demo.Doner.Wrap", 1, 180m), Line("Demo.Doner.Drink", 1, 25m)),
            ["kebab"] = Scenario("kebab", "Demo.Kebab.Note", Line("Demo.Kebab.Kebab", 1, 260m), Line("Demo.Kebab.Rice", 1, 50m)),
            ["steakhouse"] = Scenario("kebab", "Demo.Kebab.Note", Line("Demo.Kebab.Kebab", 1, 260m), Line("Demo.Kebab.Rice", 1, 50m)),
            ["chicken"] = Scenario("kebab", "Demo.Kebab.Note", Line("Demo.Kebab.Kebab", 1, 260m), Line("Demo.Kebab.Rice", 1, 50m)),
            ["dessert-shop"] = Scenario("dessert", "Demo.Dessert.Note", Line("Demo.Dessert.Baklava", 1, 160m), Line("Demo.Dessert.Kunefe", 1, 150m)),
            ["ice-cream"] = Scenario("dessert", "Demo.Dessert.Note", Line("Demo.Dessert.Baklava", 1, 160m), Line("Demo.Dessert.Kunefe", 1, 150m)),
            ["waffle-crepe"] = Scenario("dessert", "Demo.Dessert.Note", Line("Demo.Dessert.Baklava", 1, 160m), Line("Demo.Dessert.Kunefe", 1, 150m)),
            ["patisserie"] = Scenario("dessert", "Demo.Dessert.Note", Line("Demo.Dessert.Baklava", 1, 160m), Line("Demo.Dessert.Kunefe", 1, 150m))
        };

    private static readonly GuidedDemoScenario DefaultLokanta = Scenario(
        "lokanta",
        "Demo.Lokanta.Note",
        Line("Demo.Lokanta.Kofte", 1, 210m),
        Line("Demo.Lokanta.Rice", 1, 45m));

    public static GuidedDemoScenario ForSubtypes(IReadOnlyList<string>? codes)
    {
        if (codes is not null)
        {
            foreach (var code in codes)
            {
                if (!string.IsNullOrWhiteSpace(code) && BySubtype.TryGetValue(code.Trim(), out var scenario))
                    return scenario;
            }
        }

        return DefaultLokanta;
    }

    private static GuidedDemoLine Line(string nameKey, int quantity, decimal unitPrice) =>
        new(nameKey, quantity, unitPrice);

    private static GuidedDemoScenario Scenario(string code, string noteKey, params GuidedDemoLine[] lines) =>
        new(code, "Demo.CustomerName", noteKey, lines);
}
