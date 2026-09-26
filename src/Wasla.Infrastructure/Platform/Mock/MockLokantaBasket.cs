using Wasla.Application.Platform.Dtos;

namespace Wasla.Infrastructure.Platform.Mock;

internal static class MockLokantaBasket
{
    internal static MockBuiltOrder Build(string externalOrderId, Random rng)
    {
        var scenario = MockLokantaScenarios.Pick(rng);
        var lines = new List<Line>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var main in PickMains(scenario, rng))
            Add(lines, used, main, 1, scenario, rng);

        foreach (var soup in PickDistinct(SoupPool(scenario), Roll(rng, scenario.SoupChance, scenario.SoupMin, scenario.SoupMax), rng))
            Add(lines, used, soup, 1, scenario, rng);

        foreach (var side in PickDistinct(SidePool(scenario, used), Roll(rng, scenario.SideChance, scenario.SideMin, scenario.SideMax), rng))
            Add(lines, used, side, SideQuantity(side, scenario, rng), scenario, rng);

        foreach (var salad in PickDistinct(SaladPool(scenario, used), Roll(rng, scenario.SaladChance, scenario.SaladMin, scenario.SaladMax), rng))
            Add(lines, used, salad, 1, scenario, rng);

        foreach (var drink in AggregateDrinks(PickDrinks(scenario, Roll(rng, scenario.DrinkChance, scenario.DrinkMin, scenario.DrinkMax), rng)))
            Add(lines, used, drink.Product, drink.Quantity, scenario, rng);

        foreach (var dessert in PickDistinct(DessertPool(scenario, used), Roll(rng, scenario.DessertChance, scenario.DessertMin, scenario.DessertMax), rng))
            Add(lines, used, dessert, 1, scenario, rng);

        StripExtraRiceWhenRiceIsAlreadyOrdered(lines);

        if (lines.Count == 0)
            throw new InvalidOperationException($"Mock scenario '{scenario.Name}' produced an empty basket.");

        return new MockBuiltOrder(Materialize(externalOrderId, lines), scenario);
    }

    private static List<MockProduct> PickMains(MockScenario scenario, Random rng)
    {
        if (scenario.MainMax <= 0)
            return new List<MockProduct>();

        var count = rng.Next(scenario.MainMin, scenario.MainMax + 1);
        if (count <= 0)
            return new List<MockProduct>();

        return scenario.Mix switch
        {
            MockMainMix.VegChickenMeat => PickVegChickenMeat(rng),
            MockMainMix.MeatAndChicken => PickSplit(PoolFor(MockMainFocus.Meat), PoolFor(MockMainFocus.Chicken), count, rng),
            MockMainMix.GrillAndEveryday => PickSplit(
                PoolFor(MockMainFocus.Grill),
                EverydayMains(),
                Math.Max(1, count * 3 / 5),
                count,
                rng),
            _ => PickDistinct(PoolFor(scenario.Focus), count, rng)
        };
    }

    private static List<MockProduct> PickVegChickenMeat(Random rng)
    {
        var picked = new List<MockProduct>
        {
            PickWeighted(PoolFor(MockMainFocus.Vegetarian), rng),
            PickWeighted(PoolFor(MockMainFocus.Chicken), rng),
            PickWeighted(PoolFor(MockMainFocus.Meat), rng)
        };
        return picked;
    }

    private static List<MockProduct> PickSplit(
        IReadOnlyList<MockProduct> firstPool,
        IReadOnlyList<MockProduct> secondPool,
        int count,
        Random rng)
    {
        var firstCount = Math.Max(1, count / 2);
        return PickSplit(firstPool, secondPool, firstCount, count, rng);
    }

    private static List<MockProduct> PickSplit(
        IReadOnlyList<MockProduct> firstPool,
        IReadOnlyList<MockProduct> secondPool,
        int firstCount,
        int total,
        Random rng)
    {
        var picked = PickDistinct(firstPool, Math.Min(firstCount, total), rng);
        var used = new HashSet<string>(picked.Select(p => p.Name), StringComparer.Ordinal);
        var rest = secondPool.Where(p => !used.Contains(p.Name)).ToList();
        picked.AddRange(PickDistinct(rest, total - picked.Count, rng));
        return picked;
    }

    private static List<MockProduct> PoolFor(MockMainFocus focus)
    {
        var pool = focus switch
        {
            MockMainFocus.Budget => Mains(p => p.Tags.Has(MockFoodTag.Budget) && !p.Tags.Has(MockFoodTag.Premium) && !p.Tags.Has(MockFoodTag.Offal)),
            MockMainFocus.LegumeBeans => Mains(p => p.Name is "Kuru Fasulye" or "Etli Kuru Fasulye"),
            MockMainFocus.Nohut => Mains(p => p.Name is "Nohut" or "Etli Nohut"),
            MockMainFocus.Chicken => Mains(p => p.Tags.Has(MockFoodTag.Chicken) && p.Course == MockCourse.Main),
            MockMainFocus.Beef => Mains(p => p.Tags.Has(MockFoodTag.Beef) && p.Course == MockCourse.Main),
            MockMainFocus.Meat => Mains(p => p.Tags.Has(MockFoodTag.Meat) && !p.Tags.Has(MockFoodTag.Chicken) && p.Course == MockCourse.Main),
            MockMainFocus.Regional => Mains(p => p.Tags.Has(MockFoodTag.Regional) && (p.Course == MockCourse.Main || p.LightMain)),
            MockMainFocus.Vegetarian => Mains(p => p.Tags.Has(MockFoodTag.Vegetarian) && !p.Tags.Has(MockFoodTag.Meat) && !p.Tags.Has(MockFoodTag.Chicken)),
            MockMainFocus.Grill => Mains(p => p.Tags.Has(MockFoodTag.Grill)),
            MockMainFocus.Pot => Mains(p => p.Tags.Has(MockFoodTag.Pot) && p.Course == MockCourse.Main),
            MockMainFocus.Premium => Mains(p => p.Tags.Has(MockFoodTag.Premium) && p.Course == MockCourse.Main),
            MockMainFocus.Mild => Mains(p => p.Tags.Has(MockFoodTag.Mild) && p.Course == MockCourse.Main && !p.Tags.Has(MockFoodTag.Premium)),
            MockMainFocus.Mixed => Mains(p => p.Course == MockCourse.Main),
            _ => new List<MockProduct>()
        };

        if (pool.Count == 0 && focus is not (MockMainFocus.SoupOnly or MockMainFocus.Offal))
            throw new InvalidOperationException($"Mock main pool '{focus}' is empty.");

        return pool;
    }

    private static List<MockProduct> EverydayMains() =>
        Mains(p => p.Course == MockCourse.Main && !p.Tags.Has(MockFoodTag.Grill) && !p.Tags.Has(MockFoodTag.Premium));

    private static List<MockProduct> Mains(Func<MockProduct, bool> predicate)
    {
        var pool = new List<MockProduct>();
        foreach (var product in MockLokantaMenu.Products)
        {
            var isMain = product.Course == MockCourse.Main || product.LightMain;
            if (isMain && predicate(product))
                pool.Add(product);
        }

        return pool;
    }

    private static List<MockProduct> SoupPool(MockScenario scenario)
    {
        var soups = ByCourse(MockCourse.Soup);
        if (scenario.Focus == MockMainFocus.Offal)
            return Require(soups.Where(p => p.Tags.Has(MockFoodTag.Offal)).ToList(), "offal soup");

        soups = soups.Where(p => !p.Tags.Has(MockFoodTag.Offal)).ToList();

        if (scenario.Focus == MockMainFocus.Vegetarian)
            return Require(soups.Where(p => p.Tags.Has(MockFoodTag.Vegetarian)).ToList(), "vegetarian soup");

        if (scenario.Focus == MockMainFocus.Budget || scenario.Style.HasFlag(MockScenarioStyle.BudgetDrinks))
            return Require(soups.Where(p => p.Tags.Has(MockFoodTag.Budget) || p.Tags.Has(MockFoodTag.Vegetarian)).Select(BoostBudgetSoup).ToList(), "budget soup");

        if (scenario.Focus == MockMainFocus.Mild || scenario.Style.HasFlag(MockScenarioStyle.MildMenu))
            return Require(soups.Where(p => p.Tags.Has(MockFoodTag.Mild)).ToList(), "mild soup");

        if (scenario.Focus == MockMainFocus.Regional)
            return soups.Select(p => p.Tags.Has(MockFoodTag.Regional) ? p with { Weight = 5 } : p with { Weight = 2 }).ToList();

        return soups;
    }

    private static MockProduct BoostBudgetSoup(MockProduct soup) =>
        soup.Tags.Has(MockFoodTag.Budget) ? soup with { Weight = 4 } : soup with { Weight = 1 };

    private static List<MockProduct> SidePool(MockScenario scenario, HashSet<string> used)
    {
        var sides = ByCourse(MockCourse.Side).Where(p => !used.Contains(p.Name)).ToList();

        if (scenario.Focus is MockMainFocus.Budget or MockMainFocus.LegumeBeans or MockMainFocus.Nohut or MockMainFocus.Pot
            || scenario.Style.HasFlag(MockScenarioStyle.BudgetDrinks))
        {
            return Require(sides.Where(p => p.Name is "Pirinç Pilavı" or "Bulgur Pilavı" or "Şehriyeli Pirinç Pilavı").ToList(), "budget side");
        }

        if (scenario.Focus == MockMainFocus.Mild || scenario.Style.HasFlag(MockScenarioStyle.MildMenu))
        {
            return Require(sides.Where(p => p.Name is "Pirinç Pilavı" or "Bulgur Pilavı" or "Makarna" or "Patates Püresi" or "Patates Kızartması" or "Sade Erişte")
                .Select(p => p.Name == "Makarna" ? p with { Weight = 4 } : p)
                .ToList(), "mild side");
        }

        if (scenario.Focus == MockMainFocus.Regional)
        {
            return sides.Select(p => p.Tags.Has(MockFoodTag.Regional) ? p with { Weight = p.Weight + 4 } : p).ToList();
        }

        if (scenario.Focus == MockMainFocus.Grill)
        {
            return sides.Select(p => p.Name is "Patates Kızartması" or "Pirinç Pilavı" ? p with { Weight = p.Weight + 3 } : p).ToList();
        }

        return sides;
    }

    private static List<MockProduct> SaladPool(MockScenario scenario, HashSet<string> used)
    {
        var salads = ByCourse(MockCourse.Salad).Where(p => !used.Contains(p.Name)).ToList();

        if (scenario.Focus is MockMainFocus.Budget or MockMainFocus.Pot || scenario.Style.HasFlag(MockScenarioStyle.BudgetDrinks))
            return Require(salads.Where(p => p.Name is "Cacık" or "Yoğurt" or "Piyaz" or "Turşu").ToList(), "budget salad");

        if (scenario.Focus == MockMainFocus.Mild || scenario.Style.HasFlag(MockScenarioStyle.MildMenu))
            return Require(salads.Where(p => p.Tags.Has(MockFoodTag.Mild)).ToList(), "mild salad");

        return salads;
    }

    private static List<MockProduct> DessertPool(MockScenario scenario, HashSet<string> used)
    {
        var desserts = ByCourse(MockCourse.Dessert).Where(p => !used.Contains(p.Name)).ToList();

        if (scenario.Style.HasFlag(MockScenarioStyle.RegionalDessert) || scenario.Focus == MockMainFocus.Regional)
        {
            return desserts.Select(p => p.Tags.Has(MockFoodTag.Regional) ? p with { Weight = 5 } : p with { Weight = 1 }).ToList();
        }

        if (scenario.Style.HasFlag(MockScenarioStyle.MildMenu) || scenario.Focus == MockMainFocus.Mild)
        {
            return Require(desserts.Where(p => p.Tags.Has(MockFoodTag.Mild)).ToList(), "mild dessert");
        }

        return desserts;
    }

    private static List<MockProduct> PickDrinks(MockScenario scenario, int count, Random rng)
    {
        var drinks = ByCourse(MockCourse.Drink);
        var picked = new List<MockProduct>(count);
        for (var i = 0; i < count; i++)
            picked.Add(PickWeighted(drinks, rng, drink => DrinkWeight(drink, scenario)));

        return picked;
    }

    private static int DrinkWeight(MockProduct drink, MockScenario scenario)
    {
        var weight = drink.Name switch
        {
            "Ayran" => 8,
            "Su" => 5,
            "Soda" => 2,
            "Şalgam" => 3,
            "Kola" => 3,
            "Kola Zero" => 2,
            "Gazoz" => 2,
            _ => 1
        };

        if (scenario.Style.HasFlag(MockScenarioStyle.BudgetDrinks))
        {
            if (drink.Name is "Ayran" or "Su")
                weight += 6;
            if (drink.Name is "Kola" or "Kola Zero" or "Şalgam")
                weight = 1;
        }

        if (scenario.Style.HasFlag(MockScenarioStyle.MildMenu))
        {
            if (drink.Name == "Şalgam")
                return 0;
            if (drink.Name is "Ayran" or "Su" or "Gazoz")
                weight += 4;
        }

        if (scenario.Style.HasFlag(MockScenarioStyle.OffalDrinks))
        {
            if (drink.Name == "Şalgam")
                weight = 8;
            if (drink.Name == "Ayran")
                weight = 5;
        }

        return weight;
    }

    private static List<(MockProduct Product, int Quantity)> AggregateDrinks(List<MockProduct> picked)
    {
        var lines = new List<(MockProduct Product, int Quantity)>();
        foreach (var drink in picked)
        {
            var index = lines.FindIndex(line => line.Product.Name == drink.Name);
            if (index >= 0)
                lines[index] = (lines[index].Product, lines[index].Quantity + 1);
            else
                lines.Add((drink, 1));
        }

        return lines;
    }

    private static void StripExtraRiceWhenRiceIsAlreadyOrdered(List<Line> lines)
    {
        var hasRiceDish = false;
        foreach (var line in lines)
        {
            if (IsRiceDish(line.Product.Name))
            {
                hasRiceDish = true;
                break;
            }
        }

        if (!hasRiceDish)
            return;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsRiceDish(line.Product.Name))
                continue;

            var kept = new List<MockModifier>(line.Modifiers.Count);
            foreach (var modifier in line.Modifiers)
            {
                if (!modifier.Name.Contains("pilav", StringComparison.OrdinalIgnoreCase))
                    kept.Add(modifier);
            }

            var note = line.Note;
            if (note == "Pilavı az olsun.")
                note = null;

            if (kept.Count != line.Modifiers.Count || note != line.Note)
                lines[i] = line with { Modifiers = kept, Note = note };
        }
    }

    private static bool IsRiceDish(string name) =>
        name is "Pirinç Pilavı" or "Bulgur Pilavı" or "Şehriyeli Pirinç Pilavı" or "Mengen Pilavı" or "Paşa Pilavı" or "Köfte ve Pilav";

    private static int SideQuantity(MockProduct product, MockScenario scenario, Random rng)
    {
        if (scenario.PartyMin < 5)
            return 1;

        if (product.Name is not ("Pirinç Pilavı" or "Bulgur Pilavı" or "Şehriyeli Pirinç Pilavı" or "Makarna"))
            return 1;

        return rng.NextDouble() < 0.40 ? 2 : 1;
    }

    private static void Add(List<Line> lines, HashSet<string> used, MockProduct product, int quantity, MockScenario scenario, Random rng)
    {
        used.Add(product.Name);
        var modifiers = PickModifiers(product, scenario, rng);
        var note = PickNote(product, modifiers, scenario, rng);
        lines.Add(new Line(product, quantity, modifiers, note));
    }

    private static List<MockModifier> PickModifiers(MockProduct product, MockScenario scenario, Random rng)
    {
        var pool = MockLokantaMenu.ModifiersFor(product.Modifiers).ToList();
        if (scenario.Style.HasFlag(MockScenarioStyle.MildMenu) || scenario.Focus == MockMainFocus.Mild)
            pool.RemoveAll(modifier => modifier.Name.Contains("Pul biber", StringComparison.Ordinal));

        if (pool.Count == 0)
            return new List<MockModifier>();

        var chance = product.Course switch
        {
            MockCourse.Drink or MockCourse.Dessert => 0.12,
            _ => scenario.Focus == MockMainFocus.Offal ? 0.72 : 0.40
        };

        if (rng.NextDouble() >= chance)
            return new List<MockModifier>();

        var firstIndex = rng.Next(pool.Count);
        var picked = new List<MockModifier> { pool[firstIndex] };
        if (pool.Count > 1 && rng.NextDouble() < 0.28)
        {
            var secondIndex = rng.Next(pool.Count - 1);
            if (secondIndex >= firstIndex)
                secondIndex++;
            picked.Add(pool[secondIndex]);
        }

        return picked;
    }

    private static string? PickNote(
        MockProduct product,
        IReadOnlyList<MockModifier> modifiers,
        MockScenario scenario,
        Random rng)
    {
        var notes = MockLokantaMenu.NotesFor(product.Notes);
        if (notes.Count == 0)
            return null;

        var chance = product.Course switch
        {
            MockCourse.Drink => 0.08,
            MockCourse.Dessert => 0.12,
            MockCourse.Side => 0.16,
            MockCourse.Salad => 0.20,
            _ => 0.26
        };

        if (scenario.PartyMin >= 6)
            chance *= 0.75;

        if (rng.NextDouble() >= chance)
            return null;

        var fitting = new List<string>();
        foreach (var note in notes)
        {
            if (!Conflicts(note, modifiers))
                fitting.Add(note);
        }

        if (fitting.Count == 0)
            return null;

        return fitting[rng.Next(fitting.Count)];
    }

    private static bool Conflicts(string note, IReadOnlyList<MockModifier> modifiers)
    {
        foreach (var modifier in modifiers)
        {
            if (modifier.Name.Contains("sarımsak", StringComparison.OrdinalIgnoreCase)
                && note.Contains("Sarımsak koymayın", StringComparison.Ordinal))
                return true;

            if (modifier.Name.Contains("Pul biber", StringComparison.Ordinal)
                && note.Contains("Acısız", StringComparison.Ordinal))
                return true;

            if (modifier.Name.Contains("limon", StringComparison.OrdinalIgnoreCase)
                && note.Contains("Limonu ayrı", StringComparison.Ordinal))
                return true;

            if (modifier.Name.Contains("pilav", StringComparison.OrdinalIgnoreCase)
                && note.Contains("Pilavı az", StringComparison.Ordinal))
                return true;

            if (modifier.Name.Contains("ceviz", StringComparison.OrdinalIgnoreCase)
                && note.Contains("Ceviz ayrı", StringComparison.Ordinal))
                return true;

            if (modifier.Name.Contains("Kaymak", StringComparison.Ordinal)
                && note.Contains("Kaymak ayrı", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static List<ExternalOrderItemDto> Materialize(string externalOrderId, List<Line> lines)
    {
        var items = new List<ExternalOrderItemDto>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var optionTotal = 0m;
            var options = new List<ExternalOrderItemOptionDto>(line.Modifiers.Count);
            foreach (var modifier in line.Modifiers)
            {
                options.Add(new ExternalOrderItemOptionDto(modifier.Name, modifier.Price));
                optionTotal += modifier.Price;
            }

            var total = (line.Product.Price + optionTotal) * line.Quantity;
            items.Add(new ExternalOrderItemDto(
                ExternalItemId: $"{externalOrderId}-item-{i + 1}",
                ProductName: line.Product.Name,
                Quantity: line.Quantity,
                UnitPrice: line.Product.Price,
                TotalPrice: total,
                Notes: line.Note,
                Options: options));
        }

        return items;
    }

    private static List<MockProduct> ByCourse(MockCourse course)
    {
        var pool = new List<MockProduct>();
        foreach (var product in MockLokantaMenu.Products)
        {
            if (product.Course == course)
                pool.Add(product);
        }

        return pool;
    }

    private static List<MockProduct> Require(List<MockProduct> pool, string label)
    {
        if (pool.Count == 0)
            throw new InvalidOperationException($"Mock pool '{label}' is empty.");

        return pool;
    }

    private static int Roll(Random rng, double chance, int min, int max)
    {
        if (chance <= 0 || max <= 0)
            return 0;

        if (rng.NextDouble() >= chance)
            return 0;

        if (min < 1)
            min = 1;
        if (max < min)
            max = min;

        return rng.Next(min, max + 1);
    }

    private static List<MockProduct> PickDistinct(IReadOnlyList<MockProduct> pool, int count, Random rng)
    {
        if (count <= 0)
            return new List<MockProduct>();

        if (pool.Count == 0)
            throw new InvalidOperationException("Cannot pick from an empty mock product pool.");

        var remaining = new List<MockProduct>(pool);
        var picked = new List<MockProduct>(Math.Min(count, remaining.Count));
        for (var i = 0; i < count && remaining.Count > 0; i++)
        {
            var choice = PickWeighted(remaining, rng);
            picked.Add(choice);
            remaining.Remove(choice);
        }

        return picked;
    }

    private static MockProduct PickWeighted(IReadOnlyList<MockProduct> pool, Random rng, Func<MockProduct, int>? weightOf = null)
    {
        var total = 0;
        var weights = new int[pool.Count];
        for (var i = 0; i < pool.Count; i++)
        {
            var weight = weightOf is null ? Math.Max(1, pool[i].Weight) : weightOf(pool[i]);
            weights[i] = weight;
            if (weight > 0)
                total += weight;
        }

        if (total <= 0)
            throw new InvalidOperationException("Mock weighted pool has no positive weights.");

        var roll = rng.Next(total);
        for (var i = 0; i < pool.Count; i++)
        {
            if (weights[i] <= 0)
                continue;
            if (roll < weights[i])
                return pool[i];
            roll -= weights[i];
        }

        return pool[^1];
    }

    private sealed record Line(MockProduct Product, int Quantity, IReadOnlyList<MockModifier> Modifiers, string? Note);
}

internal sealed record MockBuiltOrder(
    IReadOnlyList<ExternalOrderItemDto> Items,
    MockScenario Scenario);
