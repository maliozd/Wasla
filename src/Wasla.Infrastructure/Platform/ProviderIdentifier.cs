namespace Wasla.Infrastructure.Platform;

/// <summary>
/// The one comparison of provider identifiers (seller, supplier and store ids). Ingestion uses it to check an order
/// against its connection, and the Trendyol GO webhook pilot uses it to recognize its seller and store (WAS-232).
/// It holds no state.
/// </summary>
internal static class ProviderIdentifier
{
    /// <summary>
    /// Whether two provider identifiers name the same thing: trimmed and exact, except that two all-digit values compare
    /// as numbers, because a provider id may arrive as a JSON number (123) while the connection stores the text typed in
    /// ("00123").
    /// </summary>
    public static bool Same(string first, string second) =>
        string.Equals(Canonical(first), Canonical(second), StringComparison.Ordinal);

    private static string Canonical(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit))
            return trimmed;
        var digits = trimmed.TrimStart('0');
        return digits.Length == 0 ? "0" : digits;
    }
}
