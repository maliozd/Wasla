using System.Globalization;

namespace Wasla.Web.Localization;

internal static class SupportedCultures
{
    public const string Turkish = "tr-TR";
    public const string English = "en-US";
    public const string Arabic = "ar-SA";
    public const string Russian = "ru-RU";

    public static readonly string Default = Turkish;

    public static readonly IReadOnlyList<string> All = new[]
    {
        Turkish,
        English,
        Arabic,
        Russian
    };

    public static readonly IReadOnlyList<CultureInfo> AllCultureInfos =
        All.Select(c => new CultureInfo(c)).ToList();

    public static bool IsSupported(string? culture) =>
        !string.IsNullOrWhiteSpace(culture) &&
        All.Contains(culture.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeOrDefault(string? culture) =>
        IsSupported(culture) ? culture!.Trim() : Default;
}

