using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// The reasons an operator chooses from when rejecting an order, per platform: the platform's own reason ids, sent to
/// the provider unchanged. A platform with a list requires one of its reasons; a platform without one takes none, and
/// its reject keeps its existing behaviour. Only Trendyol GO has a verified list.
/// </summary>
public static class OrderRejectReasons
{
    public const string ReasonRequiredKey = "Orders.RejectReasonRequired";
    public const string ReasonNotSupportedKey = "Orders.RejectReasonNotSupported";

    /// <summary>
    /// Trendyol GO's documented restaurant reasons ("Paket Modelleri", checked 2026-11-03) for sellers whose orders the
    /// platform's couriers deliver: 621 supply problem, 622 store closed, 623 store cannot prepare the order, 627 order
    /// confusion. 624 and 626 are only for sellers with their own couriers, which Wasla does not support.
    /// </summary>
    private static readonly int[] TrendyolGo = [621, 622, 623, 627];

    public static IReadOnlyList<int> For(FoodPlatform platform) =>
        platform == FoodPlatform.TrendyolYemek ? TrendyolGo : [];

    /// <summary>
    /// Null when <paramref name="reasonId"/> is acceptable for <paramref name="platform"/>; otherwise the message key of
    /// the refusal: a missing reason where the platform requires one, or a reason the platform does not offer.
    /// </summary>
    public static string? Refusal(FoodPlatform platform, int? reasonId)
    {
        var reasons = For(platform);
        if (reasons.Count == 0)
            return reasonId is null ? null : ReasonNotSupportedKey;
        if (reasonId is not { } chosen)
            return ReasonRequiredKey;
        return reasons.Contains(chosen) ? null : ReasonNotSupportedKey;
    }

    /// <summary>The localization key of a reason's label, for example <c>Orders.RejectReason.TrendyolYemek.621</c>.</summary>
    public static string LabelKey(FoodPlatform platform, int reasonId) => $"Orders.RejectReason.{platform}.{reasonId}";
}
