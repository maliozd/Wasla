namespace Wasla.Application.Abstractions.Printing;

public static class ReceiptLanguageCodes
{
    public const string Turkish = "tr";
    public const string English = "en";
    public const string Arabic = "ar";
    public const string Russian = "ru";

    public static readonly IReadOnlyList<string> Supported = [Turkish, English, Arabic, Russian];

    public static string Normalize(string? value, string? fallback = Turkish)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Normalize(fallback);

        var code = value.Trim().ToLowerInvariant();
        return code switch
        {
            Turkish or "tr-tr" => Turkish,
            English or "en-us" or "en-gb" => English,
            Arabic or "ar-sa" => Arabic,
            Russian or "ru-ru" => Russian,
            _ => Normalize(fallback)
        };
    }

    public static bool IsSupported(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Supported.Contains(Normalize(value), StringComparer.Ordinal);

    public static bool IsRightToLeft(string? value) =>
        Normalize(value) == Arabic;
}
