using System.Collections.Frozen;
using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// The reason codes a provider documents reporting about a cancellation (WAS-200). This is what Wasla may record from a
/// provider, not what an operator may send: <see cref="OrderRejectReasons"/> is that list, and nothing here is selectable.
/// A code says why an order ended as the provider describes it; it does not say who pressed cancel.
/// </summary>
public static class ProviderCancellationReasons
{
    /// <summary>
    /// Trendyol GO Meal "Paket Modelleri" (checked 2026-11-09): "Restaurant Kaynaklı İptal Nedenleri", and "Diğer İptal
    /// Nedenleri", the reasons the platform may return. Not the Hızlı Market (Grocery) contract: its <c>reasonType</c> and
    /// its sample code 664 are not part of Meal.
    /// </summary>
    private static readonly FrozenSet<int> TrendyolGoMeal = new[]
    {
        621, 622, 623, 624, 626, 627,
        69, 601, 602, 603, 604, 605, 606, 607, 608, 609, 610, 611, 625,
        641, 642, 643, 644, 645, 647, 648,
        661, 662, 663, 681, 682
    }.ToFrozenSet();

    /// <summary>Whether <paramref name="code"/> is one <paramref name="platform"/> documents. A platform without a verified list has none.</summary>
    public static bool IsDocumented(FoodPlatform platform, int code) =>
        platform == FoodPlatform.TrendyolYemek && TrendyolGoMeal.Contains(code);
}
