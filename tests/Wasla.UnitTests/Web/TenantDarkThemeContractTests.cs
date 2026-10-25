using System.Text.RegularExpressions;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Source contracts behind the tenant dark mode. The painted result is checked in a real browser by
/// <see cref="TenantDarkModeBrowserTests"/>; these keep the token structure that makes it work from drifting.
/// </summary>
public sealed class TenantDarkThemeContractTests
{
    private const string LightShell = "body.wasla-tenant-shell {";
    private const string DarkShell = "[data-bs-theme=\"dark\"] body.wasla-tenant-shell {";

    [Fact]
    public void EveryLiteralColourTokenOfTheTenantShell_HasADarkValue()
    {
        var foundation = Css("wasla-foundation.css");
        var light = Tokens(Block(foundation, LightShell));
        var dark = Tokens(Block(foundation, DarkShell));

        // Tokens with a literal colour (hex or rgb/rgba, shadows included); tokens that only alias another token
        // follow it automatically.
        var literal = light.Where(t => Regex.IsMatch(t.Value, "#[0-9a-fA-F]{3,8}\\b|rgba?\\(")).Select(t => t.Key).ToList();
        Assert.NotEmpty(literal);
        var missing = literal.Where(name => !dark.ContainsKey(name)).ToList();
        Assert.True(missing.Count == 0, "Tenant shell colour tokens without a dark value: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheDarkTokens_AreScopedToTheTenantShell_AndLiveScreenAndCentralAdminKeepTheSharedPalette()
    {
        var foundation = Css("wasla-foundation.css");
        var theme = Css("wasla-theme.css");

        Assert.Single(Regex.Matches(foundation, Regex.Escape(DarkShell)));
        Assert.DoesNotContain(":root {", foundation, StringComparison.Ordinal);

        // The shared dark palette that Central Admin uses is unchanged; the Live Screen raises its muted text on its own body.
        var sharedDark = Tokens(Block(theme, "[data-bs-theme=\"dark\"] {"));
        Assert.Equal("#737b89", sharedDark["--color-text-muted"]);
        Assert.Equal("#ff7d4f", sharedDark["--color-accent-hover"]);
        var liveDark = Tokens(Block(theme, "[data-bs-theme=\"dark\"] body.wasla-orders-live-display {"));
        Assert.Equal("#8b939f", liveDark["--color-text-muted"]);
        Assert.Equal("#8b939f", liveDark["--wasla-muted"]);
    }

    [Fact]
    public void TheDarkLinkColour_LeavesWaslaButtonsRenderedAsLinksAlone()
    {
        var theme = Css("wasla-theme.css");

        var linkRules = Regex.Matches(theme, @"\[data-bs-theme=""dark""\] body\.wasla-tenant\.wasla-app a:not\([^{]*\{").Select(m => m.Value).ToList();
        Assert.Equal(2, linkRules.Count);
        Assert.All(linkRules, rule => Assert.Contains(":not(.wasla-btn)", rule, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Tokens(string block) =>
        Regex.Matches(block, @"(--[\w-]+)\s*:\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);

    // The declarations of the first rule that starts with exactly this selector (rules here do not nest).
    private static string Block(string css, string selector)
    {
        var match = Regex.Match(css, "(^|\\n)" + Regex.Escape(selector));
        Assert.True(match.Success, $"No rule starts with {selector}");
        var start = match.Index + match.Length;
        return css[start..css.IndexOf('}', start)];
    }

    private static string Css(string name) =>
        File.ReadAllText(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot", "css", name));
}
