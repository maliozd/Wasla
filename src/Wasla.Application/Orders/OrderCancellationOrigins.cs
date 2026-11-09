using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// Which <see cref="OrderCancellationOrigin"/> a provider's cancellation status proves (WAS-200). Only statuses whose
/// meaning the provider documents are read; everything else, including every platform without a verified contract,
/// stays unknown.
/// </summary>
public static class OrderCancellationOrigins
{
    /// <summary>
    /// The origin <paramref name="providerStatus"/> proves, or null when it proves none. Trendyol GO "Package Models"
    /// (checked 2026-11-09): <c>UnSupplied</c> is a cancellation by the restaurant, <c>Cancelled</c> one not originating
    /// from the restaurant. Other Trendyol GO values that map to Cancelled (<c>Returned</c>, the Turkish mock labels) are
    /// not documented and prove nothing.
    /// </summary>
    public static OrderCancellationOrigin? FromProviderStatus(FoodPlatform platform, string? providerStatus)
    {
        if (platform != FoodPlatform.TrendyolYemek)
            return null;

        return providerStatus?.Trim() switch
        {
            "UnSupplied" => OrderCancellationOrigin.RestaurantOnPlatform,
            "Cancelled" => OrderCancellationOrigin.ProviderOrCustomer,
            _ => null
        };
    }
}
