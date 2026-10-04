using System.Text.Json;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mock;

namespace Wasla.UnitTests.Platform;

public sealed class MockSubtypeOrderTests
{
    [Fact]
    public void Catalog_CoversEverySubtype_AndResolvesTrEnArText()
    {
        foreach (var subtype in Enum.GetValues<BusinessSubtype>())
            Assert.Contains(subtype.ToString(), MockSubtypeOrders.FamilyNames);
        Assert.Contains("Generic", MockSubtypeOrders.FamilyNames);

        foreach (var key in MockSubtypeOrders.CatalogKeys().Distinct(StringComparer.Ordinal))
        {
            foreach (var culture in new[] { "tr-TR", "en-US", "ar-SA" })
            {
                var text = MockOrderText.Resolve(culture, key);
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("Mock.", text, StringComparison.Ordinal);
            }
        }

        Assert.Equal("Soğansız", MockOrderText.Resolve("tr-TR", "Mock.Modifier.NoOnion"));
        Assert.Equal("No onion", MockOrderText.Resolve("en", "Mock.Modifier.NoOnion"));
        Assert.Equal("بدون بصل", MockOrderText.Resolve("ar-SA", "Mock.Modifier.NoOnion"));
        Assert.Equal("Zile basmayın, lütfen arayın.", MockOrderText.Resolve("tr", "Mock.OrderNote.DoNotRingBell"));
        Assert.Equal("Please don't ring the bell. Call instead.", MockOrderText.Resolve("en-US", "Mock.OrderNote.DoNotRingBell"));
        Assert.Equal("يرجى عدم قرع الجرس، اتصلوا بدلاً من ذلك.", MockOrderText.Resolve("ar", "Mock.OrderNote.DoNotRingBell"));
    }

    [Fact]
    public void BurgerTenant_UsesBurgerProducts_AndStoresResolvedTurkishText()
    {
        var orders = Create("tr", ["burger"], 48, 11);
        var names = ProductNames("Burger", "tr");
        var options = orders.SelectMany(order => order.Items).SelectMany(item => item.Options).Select(option => option.Name).ToList();

        Assert.All(orders, order => Assert.Equal("Burger", Scenario(order.RawPayloadJson)));
        Assert.All(orders.SelectMany(order => order.Items), item => Assert.Contains(item.ProductName, names));
        Assert.Contains("Soğansız", options);
        Assert.DoesNotContain(options, name => name == "No onion" || name == "Mock.Modifier.NoOnion");
        Assert.All(orders, order =>
        {
            Assert.Equal(5.00m, order.ServiceFee);
            Assert.True(order.DeliveryFee is 0m or 24.90m);
            Assert.Contains(order.ExternalStatus, new[] { "RECEIVED", "DELIVERED", "CANCELLED" });
        });
    }

    [Fact]
    public void PizzaTenant_UsesPizzaProductsInEnglish()
    {
        var orders = Create("en-US", ["pizza"], 30, 19);
        var names = ProductNames("Pizza", "en");

        Assert.All(orders, order => Assert.Equal("Pizza", Scenario(order.RawPayloadJson)));
        Assert.All(orders.SelectMany(order => order.Items), item => Assert.Contains(item.ProductName, names));
        Assert.Contains(
            orders.SelectMany(order => order.Items).SelectMany(item => item.Options),
            option => option.Name == "No onion" || option.Name == "Thin crust" || option.Name == "Extra cheese");
    }

    [Fact]
    public void MultiSubtypeTenant_ChoosesOnlyConfiguredFamilies()
    {
        var orders = Create("tr", ["burger", "pizza", "waffle-crepe"], 90, 23);
        var scenarios = orders.Select(order => Scenario(order.RawPayloadJson)).Distinct().OrderBy(name => name).ToArray();

        Assert.Equal(["Burger", "Pizza", "WaffleCrepe"], scenarios);
    }

    [Fact]
    public void MissingSubtypeSelection_UsesGenericFallback_AndDefaultPathStaysLokanta()
    {
        var generic = Create("en", [], 16, 29);
        var genericNames = ProductNames("Generic", "en");
        Assert.All(generic, order => Assert.Equal("Generic", Scenario(order.RawPayloadJson)));
        Assert.All(generic.SelectMany(order => order.Items), item => Assert.Contains(item.ProductName, genericNames));

        var legacy = Create("tr", ["restaurant", "not-a-subtype"], 8, 31);
        Assert.All(legacy, order => Assert.Equal("Generic", Scenario(order.RawPayloadJson)));

        var lokanta = MockOrders.CreateOrders(FoodPlatform.Yemeksepeti, Connection(), 12, new Random(41));
        Assert.All(lokanta, order =>
        {
            using var document = JsonDocument.Parse(order.RawPayloadJson);
            Assert.Equal("Mengen Lokantası", document.RootElement.GetProperty("restaurant").GetString());
        });
        Assert.All(lokanta.SelectMany(order => order.Items), item =>
            Assert.NotNull(MockLokantaMenu.FindByName(item.ProductName)));
    }

    [Fact]
    public void AmbientContext_SelectsTenantFamilies_AndResolvedTextDoesNotChangeLater()
    {
        using (MockOrderGenerationContext.Begin(["pizza"], "ar"))
        {
            var orders = MockOrders.CreateOrders(FoodPlatform.GetirYemek, Connection(), 12, new Random(43));
            var stored = orders.SelectMany(order => order.Items).Select(item => item.ProductName).ToArray();
            Assert.All(orders, order => Assert.Equal("Pizza", Scenario(order.RawPayloadJson)));
            var arabicNames = ProductNames("Pizza", "ar");
            var englishNames = ProductNames("Pizza", "en");
            Assert.All(stored, name => Assert.Contains(name, arabicNames));
            Assert.Contains(stored, name => !englishNames.Contains(name, StringComparer.Ordinal));
        }

        var after = MockOrders.CreateOrders(FoodPlatform.Yemeksepeti, Connection(), 4, new Random(44));
        Assert.All(after, order =>
        {
            using var document = JsonDocument.Parse(order.RawPayloadJson);
            Assert.Equal("Mengen Lokantası", document.RootElement.GetProperty("restaurant").GetString());
        });
    }

    [Fact]
    public void ComplexityProfiles_CoverSimpleNormalGroupAndNoteHeavyOrders()
    {
        var simple = MockSubtypeOrders.Build(["cafe"], "en", "simple-1", new Random(3), MockOrderComplexity.Simple);
        Assert.Single(simple.Items);
        Assert.True(simple.Items.Single().Options.Count <= 1);
        Assert.Equal(1, simple.Items.Single().Quantity);

        var normal = MockSubtypeOrders.Build(["kebab"], "tr", "normal-1", new Random(5), MockOrderComplexity.Normal);
        Assert.InRange(normal.Items.Count, 2, 4);

        var group = MockSubtypeOrders.Build(["doner"], "tr", "group-1", new Random(7), MockOrderComplexity.Group);
        Assert.InRange(group.Items.Count, 4, 8);
        Assert.Contains(group.Items, item => item.Quantity is >= 2 and <= 4);

        var noted = MockSubtypeOrders.Build(["burger"], "en", "notes-1", new Random(9), MockOrderComplexity.NoteHeavy);
        Assert.False(string.IsNullOrWhiteSpace(noted.CustomerNote));
        Assert.Contains(noted.Items, item => !string.IsNullOrWhiteSpace(item.Notes));
        Assert.Contains(noted.Items, item => item.Options.Count >= 2);
        Assert.Contains(noted.Items, item => item.ProductName.Length > 40);
        Assert.Contains(noted.Items, item => item.Options.Any(option => option.Name.Length > 20));
        Assert.True(noted.CustomerNote!.Length > 40);
        Assert.DoesNotContain("Mock.", noted.CustomerNote, StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<Wasla.Application.Platform.Dtos.ExternalOrderDto> Create(
        string culture,
        IReadOnlyList<string> codes,
        int count,
        int seed) =>
        MockOrders.CreateOrders(
            FoodPlatform.Yemeksepeti,
            Connection(),
            count,
            new Random(seed),
            codes,
            culture);

    private static string[] ProductNames(string family, string culture) =>
        MockSubtypeOrders.ProductKeys(family)
            .Select(key => MockOrderText.Resolve(culture, key))
            .Append(MockOrderText.Resolve(culture, "Mock.Stress.Product.LongName"))
            .ToArray();

    private static string Scenario(string rawPayload)
    {
        using var document = JsonDocument.Parse(rawPayload);
        return document.RootElement.GetProperty("scenario").GetString()!;
    }

    private static PlatformConnection Connection() => new()
    {
        Platform = FoodPlatform.Yemeksepeti,
        StoreId = "mock-store"
    };
}
