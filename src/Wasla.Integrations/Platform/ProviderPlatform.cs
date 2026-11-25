namespace Wasla.Integrations.Platform;

/// <summary>
/// The food-delivery platform a provider client serves and an external order comes from. The library owns it, so it
/// needs no host assembly (WAS-233). Its members, names and numeric values mirror the Orders <c>FoodPlatform</c>
/// exactly; Orders converts between the two at its boundary, and a parity test keeps them equal.
/// </summary>
public enum ProviderPlatform
{
    Yemeksepeti = 1,
    GetirYemek = 2,
    TrendyolYemek = 3
}
