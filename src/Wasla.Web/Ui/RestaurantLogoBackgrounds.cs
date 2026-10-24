using Wasla.Domain.Enums;

namespace Wasla.Web.Ui;

/// <summary>
/// The restaurant logo background styles as the Web shows and posts them: a fixed form token per style, the CSS class
/// that draws it (wasla-theme.css) and its localized texts. Parsing accepts exactly these tokens, so an enum name in
/// another case, a number or anything else posted is refused.
/// </summary>
public static class RestaurantLogoBackgrounds
{
    public sealed record Option(RestaurantLogoBackground Value, string Token, string LabelKey, string DescriptionKey, bool Recommended);

    /// <summary>In the order the settings page lists them; the recommended default first.</summary>
    public static readonly IReadOnlyList<Option> All =
    [
        new(RestaurantLogoBackground.ThemeAware, "theme-aware", "Settings.RestaurantLogo.Background.ThemeAware", "Settings.RestaurantLogo.Background.ThemeAwareDescription", true),
        new(RestaurantLogoBackground.Transparent, "transparent", "Settings.RestaurantLogo.Background.Transparent", "Settings.RestaurantLogo.Background.TransparentDescription", false),
        new(RestaurantLogoBackground.Light, "light", "Settings.RestaurantLogo.Background.Light", "Settings.RestaurantLogo.Background.LightDescription", false),
        new(RestaurantLogoBackground.Dark, "dark", "Settings.RestaurantLogo.Background.Dark", "Settings.RestaurantLogo.Background.DarkDescription", false)
    ];

    public const string CssClassPrefix = "wasla-logo-bg--";

    public static string Token(RestaurantLogoBackground background) =>
        All.FirstOrDefault(option => option.Value == background)?.Token ?? All[0].Token;

    public static string CssClass(RestaurantLogoBackground background) => CssClassPrefix + Token(background);

    public static bool TryParse(string? token, out RestaurantLogoBackground background)
    {
        var option = All.FirstOrDefault(candidate => string.Equals(candidate.Token, token, StringComparison.Ordinal));
        background = option?.Value ?? RestaurantLogoBackground.ThemeAware;
        return option is not null;
    }
}
