using Wasla.Domain.Enums;

namespace Wasla.Application.Signup;

public sealed record BusinessSubtypeDefinition(
    BusinessSubtype Subtype,
    BusinessCategory Category,
    string Code,
    string FallbackDisplayName,
    int SortOrder);

public static class BusinessSubtypeCatalog
{
    private static readonly BusinessSubtypeDefinition[] Definitions =
    [
        Def(BusinessSubtype.Kebab, BusinessCategory.Restaurant, "kebab", "Kebap"),
        Def(BusinessSubtype.HomeCooking, BusinessCategory.Restaurant, "home-cooking", "Ev Yemekleri"),
        Def(BusinessSubtype.Steakhouse, BusinessCategory.Restaurant, "steakhouse", "Et Restoranı"),
        Def(BusinessSubtype.Seafood, BusinessCategory.Restaurant, "seafood", "Deniz Ürünleri"),
        Def(BusinessSubtype.Manti, BusinessCategory.Restaurant, "manti", "Mantı"),
        Def(BusinessSubtype.Soup, BusinessCategory.Restaurant, "soup", "Çorbacı"),
        Def(BusinessSubtype.Burger, BusinessCategory.FastFood, "burger", "Burger"),
        Def(BusinessSubtype.Pizza, BusinessCategory.FastFood, "pizza", "Pizza"),
        Def(BusinessSubtype.Doner, BusinessCategory.FastFood, "doner", "Döner"),
        Def(BusinessSubtype.Chicken, BusinessCategory.FastFood, "chicken", "Tavuk"),
        Def(BusinessSubtype.CigKofte, BusinessCategory.FastFood, "cig-kofte", "Çiğ Köfte"),
        Def(BusinessSubtype.SandwichToast, BusinessCategory.FastFood, "sandwich-toast", "Sandviç / Tost"),
        Def(BusinessSubtype.Cafe, BusinessCategory.Cafe, "cafe", "Kafe"),
        Def(BusinessSubtype.Breakfast, BusinessCategory.Cafe, "breakfast", "Kahvaltı"),
        Def(BusinessSubtype.Patisserie, BusinessCategory.Bakery, "patisserie", "Pastane"),
        Def(BusinessSubtype.PideLahmacun, BusinessCategory.Bakery, "pide-lahmacun", "Pide / Lahmacun"),
        Def(BusinessSubtype.BorekGozleme, BusinessCategory.Bakery, "borek-gozleme", "Börek / Gözleme"),
        Def(BusinessSubtype.DessertShop, BusinessCategory.Dessert, "dessert-shop", "Tatlıcı"),
        Def(BusinessSubtype.IceCream, BusinessCategory.Dessert, "ice-cream", "Dondurma"),
        Def(BusinessSubtype.WaffleCrepe, BusinessCategory.Dessert, "waffle-crepe", "Waffle / Krep"),
        Def(BusinessSubtype.Sushi, BusinessCategory.WorldCuisine, "sushi", "Sushi"),
        Def(BusinessSubtype.Asian, BusinessCategory.WorldCuisine, "asian", "Uzak Doğu"),
        Def(BusinessSubtype.Italian, BusinessCategory.WorldCuisine, "italian", "Makarna / İtalyan"),
        Def(BusinessSubtype.WorldCuisine, BusinessCategory.WorldCuisine, "world-cuisine", "Dünya Mutfağı"),
        Def(BusinessSubtype.VeganVegetarian, BusinessCategory.SpecialDiet, "vegan-vegetarian", "Vegan / Vejetaryen"),
        Def(BusinessSubtype.Healthy, BusinessCategory.SpecialDiet, "healthy", "Sağlıklı Yemek / Fit Menü"),
        Def(BusinessSubtype.Other, BusinessCategory.Other, "other", "Diğer")
    ];

    private static readonly Dictionary<string, BusinessSubtypeDefinition> ByCode = Definitions
        .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<BusinessSubtypeDefinition> All { get; } = Definitions;

    public static IReadOnlyList<BusinessCategory> Categories { get; } = Enum.GetValues<BusinessCategory>();

    public static IReadOnlyList<BusinessSubtypeDefinition> SubtypesOf(BusinessCategory category) =>
        Definitions.Where(x => x.Category == category).ToArray();

    public static bool IsSubtypeCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ByCode.ContainsKey(code.Trim());

    public static bool TryGet(string? code, out BusinessSubtypeDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        return ByCode.TryGetValue(code.Trim(), out definition!);
    }

    public static string ResourceKey(BusinessCategory category) =>
        "Signup.BusinessCategory." + category;

    public static string ResourceKey(BusinessSubtype subtype) =>
        "Signup.BusinessSubtype." + subtype;

    public static string? ResourceKeyForCode(string? code) =>
        TryGet(code, out var definition) ? ResourceKey(definition.Subtype) : null;

    private static BusinessSubtypeDefinition Def(
        BusinessSubtype subtype,
        BusinessCategory category,
        string code,
        string fallbackDisplayName) =>
        new(subtype, category, code, fallbackDisplayName, (int)subtype);
}
