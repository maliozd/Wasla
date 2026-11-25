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
    /// Trendyol GO: every reason the provider accepts from a restaurant whose orders the platform's couriers deliver.
    /// The provider's list, which its client checks a reason against before sending it, is <c>ProviderRejectReasons</c>
    /// in <c>Wasla.Integrations</c>. Application does not reference that library (Print Bridge builds on Application), so
    /// Orders keeps the operator's list here and <c>OrderRejectReasonParityTests</c> requires it to equal the provider's
    /// for every platform (WAS-233).
    /// </summary>
    private static readonly IReadOnlyList<int> TrendyolGo = [621, 622, 623, 627];

    /// <summary>
    /// The reasons an operator may choose for <paramref name="platform"/>: every reason id the provider accepts, in the
    /// provider's order; empty for a platform without a verified list.
    /// </summary>
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
