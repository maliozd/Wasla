using System.Text.Json;

namespace OrderHub.PrintBridge.Printing;

internal static class ReceiptPayloadReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string? TryGetOrderDisplay(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (TryGetString(root, "externalOrderCode", out var code) && !string.IsNullOrWhiteSpace(code))
                return code.Trim();
            if (TryGetString(root, "ExternalOrderCode", out code) && !string.IsNullOrWhiteSpace(code))
                return code.Trim();
            if (TryGetString(root, "externalOrderId", out var id) && !string.IsNullOrWhiteSpace(id))
                return id.Trim();
            if (TryGetString(root, "ExternalOrderId", out id) && !string.IsNullOrWhiteSpace(id))
                return id.Trim();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;
        if (prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? string.Empty;
        return true;
    }
}
