using System.Globalization;
using System.Text;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>
/// Name and street-type rules for the Türkiye neighbourhood and street catalog (WAS-214). Names keep the source's
/// spelling and case; only whitespace is tidied, because SQL Server ignores trailing spaces when it compares names.
/// </summary>
public static class AddressCatalogText
{
    /// <summary>The <c>Neighborhoods.Name</c> column length.</summary>
    public const int NeighborhoodNameMaxLength = 150;

    /// <summary>The <c>Streets.Name</c> column length.</summary>
    public const int StreetNameMaxLength = 200;

    /// <summary>
    /// The source's UAVT street type codes and labels (turkey-geo-api <c>src/data/streetTypes.ts</c>). The label is
    /// stored as <c>Streets.StreetType</c> as the source writes it; every street's official name must end with it in
    /// parentheses, which is how each record's code is checked against its label.
    /// </summary>
    public static IReadOnlyDictionary<int, string> StreetTypes { get; } = new Dictionary<int, string>
    {
        [0] = "Köy Sokağı",
        [1] = "Meydan",
        [2] = "Bulvar",
        [3] = "Cadde",
        [4] = "Sokak",
        [5] = "Küme Evler"
    };

    /// <summary>Canonical composition, every run of whitespace (tabs and line breaks too) as one space, trimmed.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var composed = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        var space = false;
        foreach (var ch in composed)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Null when a normalized name can be stored; otherwise why it cannot.</summary>
    public static string? Problem(string name, int maxLength)
    {
        if (name.Length == 0)
            return "no name";
        if (name.Length > maxLength)
            return $"longer than {maxLength} characters";

        var letterOrDigit = false;
        foreach (var ch in name)
        {
            if (ch is '?' or '�')
                return "damaged text (a character was replaced by '?')";
            if (char.IsSurrogate(ch) || char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return "unsupported character";
            if (char.IsLetter(ch))
            {
                // Basic Latin through Latin Extended-B: Turkish letters and the circumflex vowels.
                if (ch > 'ɏ')
                    return "letter outside the Latin script";
                letterOrDigit = true;
            }
            else if (char.IsDigit(ch))
            {
                letterOrDigit = true;
            }
        }

        return letterOrDigit ? null : "no letter or digit";
    }

    /// <summary>
    /// Equal for names that differ only in case under Turkish rules (i/İ and ı/I are pairs). Culture-independent.
    /// </summary>
    public static string TurkishKey(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(ch switch { 'i' => 'İ', 'ı' => 'I', _ => char.ToUpperInvariant(ch) });
        return builder.ToString();
    }

    /// <summary>
    /// The coarsest form under which any case- and accent-insensitive SQL Server collation could find two names equal:
    /// Turkish upper case, the four I letters as one, and no accents. Every uniqueness check uses it, so no collation
    /// refuses a row the plan accepted, whichever collation CentralDb has.
    /// </summary>
    public static string MatchKey(string name)
    {
        var decomposed = TurkishKey(name).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Null when the code is known and the official name ends with its label; otherwise why not.</summary>
    public static string? StreetTypeProblem(int? typeCode, string officialName, out string? label)
    {
        label = null;
        if (typeCode is not { } code || !StreetTypes.TryGetValue(code, out var known))
            return typeCode is null ? "no street type" : $"unknown street type code {typeCode}";

        if (!officialName.EndsWith("(" + known + ")", StringComparison.Ordinal))
            return $"street type code {code} ({known}) does not match the official name";

        label = known;
        return null;
    }
}
