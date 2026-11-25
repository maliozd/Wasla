using Wasla.Application.Abstractions.Platform;
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
    /// The reasons an operator may choose for <paramref name="platform"/>: every reason id the provider accepts
    /// (<see cref="ProviderRejectReasons"/>, the one list of those ids), in the provider's order.
    /// </summary>
    public static IReadOnlyList<int> For(FoodPlatform platform) => ProviderRejectReasons.For(platform);

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
