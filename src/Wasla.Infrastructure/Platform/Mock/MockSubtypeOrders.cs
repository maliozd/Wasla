using System.Text.Json;
using Wasla.Application.Platform.Dtos;
using Wasla.Application.Signup;

namespace Wasla.Infrastructure.Platform.Mock;

internal enum MockOrderComplexity
{
    Simple,
    Normal,
    Group,
    NoteHeavy
}

internal sealed class MockSubtypeBuiltOrder
{
    public required IReadOnlyList<ExternalOrderItemDto> Items { get; init; }
    public string? CustomerNote { get; init; }
    public required string ScenarioName { get; init; }
}

internal static class MockSubtypeOrders
{
    private static readonly Lazy<MockScenarioLibrary> Library = new(Load);

    internal static MockSubtypeBuiltOrder Build(
        IReadOnlyList<string>? subtypeCodes,
        string? culture,
        string externalOrderId,
        Random rng,
        MockOrderComplexity? forcedProfile = null)
    {
        var library = Library.Value;
        var family = PickFamily(library, subtypeCodes, rng);
        var profile = forcedProfile ?? PickProfile(rng);
        var productCount = profile switch
        {
            MockOrderComplexity.Simple => 1,
            MockOrderComplexity.Normal => rng.Next(2, 5),
            MockOrderComplexity.Group => rng.Next(4, 9),
            MockOrderComplexity.NoteHeavy => rng.Next(2, 4),
            _ => 1
        };

        var chosen = PickProducts(family.Products, productCount, rng);
        if (profile == MockOrderComplexity.NoteHeavy)
            chosen.Add(library.StressProduct);

        var items = new List<ExternalOrderItemDto>(chosen.Count);
        for (var i = 0; i < chosen.Count; i++)
        {
            var product = chosen[i];
            var quantity = profile switch
            {
                MockOrderComplexity.Simple => 1,
                MockOrderComplexity.Group when i % 2 == 0 => rng.Next(2, 5),
                MockOrderComplexity.Normal when rng.Next(0, 4) == 0 => rng.Next(2, 4),
                _ => 1
            };
            var modifierCount = profile switch
            {
                MockOrderComplexity.Simple => rng.Next(0, 2),
                MockOrderComplexity.NoteHeavy => Math.Min(product.Modifiers.Count, rng.Next(2, 5)),
                MockOrderComplexity.Group => rng.Next(0, Math.Min(3, product.Modifiers.Count + 1)),
                _ => rng.Next(0, Math.Min(4, product.Modifiers.Count + 1))
            };
            var options = product.Modifiers
                .OrderBy(_ => rng.Next())
                .Take(modifierCount)
                .Select(key => new ExternalOrderItemOptionDto(MockOrderText.Resolve(culture, key), library.ModifierPrice(key)))
                .ToArray();
            var unit = RoundPrice(rng.Next(product.MinPrice, product.MaxPrice + 1));
            var optionSum = options.Sum(option => option.Price);
            var includeItemNote = profile == MockOrderComplexity.NoteHeavy || rng.Next(0, 3) == 0;
            string? note = null;
            if (includeItemNote && family.ItemNotes.Count > 0)
                note = MockOrderText.Resolve(culture, family.ItemNotes[rng.Next(family.ItemNotes.Count)]);
            if (profile == MockOrderComplexity.NoteHeavy && i == 0)
                note = MockOrderText.Resolve(culture, library.StressItemNote);

            items.Add(new ExternalOrderItemDto(
                $"{externalOrderId}-item-{i + 1}",
                MockOrderText.Resolve(culture, product.Key),
                quantity,
                unit,
                (unit + optionSum) * quantity,
                note,
                options));
        }

        string? customerNote = null;
        if (profile == MockOrderComplexity.NoteHeavy)
            customerNote = MockOrderText.Resolve(culture, library.StressOrderNote);
        else if (rng.Next(0, 100) < 45 && family.OrderNotes.Count > 0)
            customerNote = MockOrderText.Resolve(culture, family.OrderNotes[rng.Next(family.OrderNotes.Count)]);

        return new MockSubtypeBuiltOrder
        {
            Items = items,
            CustomerNote = customerNote,
            ScenarioName = family.BusinessSubtype
        };
    }

    internal static IReadOnlyCollection<string> ProductKeys(string subtype) =>
        Library.Value.Families[subtype].Products.Select(product => product.Key).ToArray();

    internal static IReadOnlyCollection<string> FamilyNames => Library.Value.Families.Keys.ToArray();

    private static MockScenarioFamily PickFamily(MockScenarioLibrary library, IReadOnlyList<string>? subtypeCodes, Random rng)
    {
        var names = new List<string>();
        if (subtypeCodes is not null)
        {
            foreach (var code in subtypeCodes)
            {
                if (!BusinessSubtypeCatalog.TryGet(code, out var definition))
                    continue;
                var name = definition.Subtype.ToString();
                if (library.Families.ContainsKey(name))
                    names.Add(name);
            }
        }

        if (names.Count == 0)
            return library.Families["Generic"];

        return library.Families[names[rng.Next(names.Count)]];
    }

    internal static IEnumerable<string> CatalogKeys()
    {
        var library = Library.Value;
        foreach (var family in library.Families.Values)
        {
            foreach (var product in family.Products)
            {
                yield return product.Key;
                foreach (var modifier in product.Modifiers)
                    yield return modifier;
            }

            foreach (var note in family.ItemNotes)
                yield return note;
            foreach (var note in family.OrderNotes)
                yield return note;
        }

        yield return library.StressProduct.Key;
        foreach (var modifier in library.StressProduct.Modifiers)
            yield return modifier;
        yield return library.StressItemNote;
        yield return library.StressOrderNote;
    }

    private static MockOrderComplexity PickProfile(Random rng)
    {
        var roll = rng.Next(100);
        if (roll < 25) return MockOrderComplexity.Simple;
        if (roll < 65) return MockOrderComplexity.Normal;
        if (roll < 85) return MockOrderComplexity.Group;
        return MockOrderComplexity.NoteHeavy;
    }

    private static List<MockScenarioProduct> PickProducts(IReadOnlyList<MockScenarioProduct> products, int count, Random rng)
    {
        var pool = products.ToList();
        var picked = new List<MockScenarioProduct>();
        while (picked.Count < count)
        {
            if (pool.Count == 0)
                pool = products.ToList();
            var index = rng.Next(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }

        return picked;
    }

    private static decimal RoundPrice(int value)
    {
        var rounded = (int)(Math.Round(value / 5m, MidpointRounding.AwayFromZero) * 5);
        return rounded;
    }

    private static MockScenarioLibrary Load()
    {
        var assembly = typeof(MockSubtypeOrders).Assembly;
        var modifierJson = Read(assembly, "modifiers.json");
        var modifiers = JsonSerializer.Deserialize<Dictionary<string, decimal>>(modifierJson)
            ?? throw new InvalidOperationException("Mock modifier catalog is empty.");

        var families = new Dictionary<string, MockScenarioFamily>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Scenarios.", StringComparison.Ordinal)
                || !name.EndsWith(".json", StringComparison.Ordinal)
                || name.EndsWith("modifiers.json", StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            var family = JsonSerializer.Deserialize<MockScenarioFamily>(reader.ReadToEnd(), JsonOptions);
            if (string.IsNullOrWhiteSpace(family?.BusinessSubtype) || family.Products is null)
                continue;
            families[family.BusinessSubtype] = family;
        }

        if (!families.ContainsKey("Generic"))
            throw new InvalidOperationException("Generic mock scenario family is missing.");

        return new MockScenarioLibrary(
            families,
            modifiers,
            new MockScenarioProduct
            {
                Key = "Mock.Stress.Product.LongName",
                MinPrice = 95,
                MaxPrice = 145,
                Modifiers = ["Mock.Stress.Modifier.LongName", "Mock.Modifier.ExtraSauce", "Mock.Modifier.NoOnion"]
            },
            "Mock.Stress.ItemNote.Long",
            "Mock.Stress.OrderNote.Long");
    }

    private static string Read(System.Reflection.Assembly assembly, string fileName)
    {
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class MockScenarioLibrary(
        Dictionary<string, MockScenarioFamily> families,
        Dictionary<string, decimal> modifiers,
        MockScenarioProduct stressProduct,
        string stressItemNote,
        string stressOrderNote)
    {
        public Dictionary<string, MockScenarioFamily> Families { get; } = families;
        public MockScenarioProduct StressProduct { get; } = stressProduct;
        public string StressItemNote { get; } = stressItemNote;
        public string StressOrderNote { get; } = stressOrderNote;
        public decimal ModifierPrice(string key) => modifiers.TryGetValue(key, out var price) ? price : 0m;
    }

    private sealed class MockScenarioFamily
    {
        public string BusinessSubtype { get; set; } = string.Empty;
        public List<MockScenarioProduct> Products { get; set; } = [];
        public List<string> ItemNotes { get; set; } = [];
        public List<string> OrderNotes { get; set; } = [];
    }

    private sealed class MockScenarioProduct
    {
        public string Key { get; set; } = string.Empty;
        public int MinPrice { get; set; }
        public int MaxPrice { get; set; }
        public List<string> Modifiers { get; set; } = [];
    }
}
