namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>
/// The Trendyol GO Meal webhook event types, from "Event Types and Event Payloads" (developers.tgoapps.com, English
/// "Webhook Meal Integration", checked 2026-10-26). The payloads carry no event-type field: the type is the path segment
/// of the integrator's <c>destinationUrl</c> for it. Types not listed there (for example <c>storeChanged</c> or
/// <c>sellerChanged</c>, named in older notes) are stored for inspection and never processed.
/// </summary>
public static class TrendyolGoMealWebhookEvents
{
    public const string Created = "created";
    public const string PickupEtaCalculated = "pickupEtaCalculated";
    public const string CourierNearby = "courierNearby";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Cancelled = "cancelled";
    public const string Unsupplied = "unsupplied";

    public static readonly IReadOnlyList<string> Documented =
        [Created, PickupEtaCalculated, CourierNearby, Shipped, Delivered, Cancelled, Unsupplied];

    /// <summary>The documented type a path segment names (case-insensitive), or null for any other segment.</summary>
    public static string? DocumentedType(string? pathSegment) =>
        Documented.FirstOrDefault(t => string.Equals(t, pathSegment?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Events that report something about a package but no status change: stored, never acted on.</summary>
    public static bool IsInformational(string eventType) => eventType is PickupEtaCalculated or CourierNearby;

    /// <summary>
    /// The package status a cancellation event's own <c>status</c> field documents (<c>Cancelled</c> for
    /// <c>cancelled</c>, <c>UnSupplied</c> for <c>unsupplied</c>), or null for an event without one. These are package
    /// statuses of the polling model, so the existing package-status mapping applies to them; no event name is mapped.
    /// </summary>
    public static string? DocumentedCancellationStatus(string eventType) => eventType switch
    {
        Cancelled => "Cancelled",
        Unsupplied => "UnSupplied",
        _ => null
    };
}
