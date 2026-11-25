
namespace Wasla.Integrations.Platform;

/// <summary>
/// The reject reason ids each provider accepts from a restaurant, as the provider documents them: the one list of those
/// ids on the provider side (WAS-232). A client checks a reason against it before it sends one. Orders keeps the
/// operator's list with its label keys and checks (<c>OrderRejectReasons</c>), and a parity test requires that list to equal
/// this one for every platform (WAS-233). A platform without a verified list has none.
/// </summary>
public static class ProviderRejectReasons
{
    /// <summary>
    /// Trendyol GO's documented restaurant reasons ("Paket Modelleri", checked 2026-11-03) for sellers whose orders the
    /// platform's couriers deliver: 621 supply problem, 622 store closed, 623 store cannot prepare the order, 627 order
    /// confusion. 624 and 626 are only for sellers with their own couriers, which Wasla does not support.
    /// </summary>
    public static IReadOnlyList<int> TrendyolGo { get; } = [621, 622, 623, 627];

    /// <summary>The reason ids <paramref name="platform"/> accepts, in the provider's order; empty for a platform without a verified list.</summary>
    public static IReadOnlyList<int> For(ProviderPlatform platform) =>
        platform == ProviderPlatform.TrendyolYemek ? TrendyolGo : [];
}
