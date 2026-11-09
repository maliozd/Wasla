using System.Text.Json;

namespace Wasla.Infrastructure.Platform.TrendyolGo;

/// <summary>
/// Reads Trendyol GO Meal's <c>cancelInfo</c>, for the packages response and the webhook payloads alike. Only the numeric
/// code is read: <c>reason</c> is provider free text and is never taken. Any other shape reads as no code, so one odd
/// value cannot fail a page or an event.
/// </summary>
internal static class TrendyolGoCancelInfo
{
    internal static int? ReasonCode(JsonElement cancelInfo) =>
        cancelInfo.ValueKind == JsonValueKind.Object
        && cancelInfo.TryGetProperty("reasonCode", out var code)
        && code.ValueKind == JsonValueKind.Number
        && code.TryGetInt32(out var value)
            ? value
            : null;
}
