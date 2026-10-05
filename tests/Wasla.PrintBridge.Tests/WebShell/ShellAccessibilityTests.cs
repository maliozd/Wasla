using System.Text.RegularExpressions;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// Static accessibility guarantees of the app page. Keyboard, focus, theme and reflow behaviour in the real
/// browser are covered by <see cref="ShellWebViewRuntimeTests"/>.
/// </summary>
public sealed partial class ShellAccessibilityTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));
    private static readonly string Css = File.ReadAllText(Path.Combine(AssetsDirectory(), "shell.css"));
    private static readonly string Script = File.ReadAllText(Path.Combine(AssetsDirectory(), "shell.js"));

    [Fact]
    public void Tabs_AreALinkedTablistWithASingleTabStop()
    {
        var tabs = TabRegex().Matches(Html).Select(m => m.Value).ToArray();

        Assert.Equal(4, tabs.Length);
        Assert.Contains("role=\"tablist\"", Html, StringComparison.Ordinal);
        for (var i = 0; i < tabs.Length; i++)
        {
            var id = Attribute(tabs[i], "id");
            var panelId = Attribute(tabs[i], "aria-controls");
            var panel = Assert.Single(PanelRegex().Matches(Html).Select(m => m.Value), p => Attribute(p, "id") == panelId);

            Assert.Equal(id, Attribute(panel, "aria-labelledby"));
            Assert.Equal(i == 0 ? "true" : "false", Attribute(tabs[i], "aria-selected"));
            Assert.Equal(i == 0 ? null : "-1", Attribute(tabs[i], "tabindex"));
            Assert.Equal(i != 0, panel.Contains(" hidden", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void StatusAndErrors_HaveLiveRegions()
    {
        Assert.Matches("id=\"announcer\" role=\"status\" aria-live=\"polite\" aria-atomic=\"true\"", Html);
        Assert.Matches("id=\"alert\" role=\"alert\" aria-atomic=\"true\"", Html);
        Assert.Contains("'alert' : 'announcer'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_AreNeverDisabled_SoKeyboardFocusIsNotLost()
    {
        // Unavailable actions use aria-disabled and stay focusable; the click handlers ignore them.
        Assert.DoesNotMatch(@"<(button|select|input)[^>]*\sdisabled[\s>=]", Html);
        Assert.DoesNotMatch(@"\.disabled\s*=|setAttribute\('disabled'", Script);
        Assert.Contains("getAttribute('aria-disabled') === 'true'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryControl_HasAnAccessibleName()
    {
        foreach (Match button in ButtonRegex().Matches(Html))
        {
            var tag = button.Value;
            var named = tag.Contains("data-i18n=", StringComparison.Ordinal) || tag.Contains("data-i18n-label=", StringComparison.Ordinal)
                        // The engine toggle's label is set from host state on every render.
                        || Attribute(tag, "id") == "action-engine";
            Assert.True(named, tag);
        }

        foreach (Match field in FieldRegex().Matches(Html))
        {
            var id = Attribute(field.Value, "id");
            var labelled = Html.Contains($"for=\"{id}\"", StringComparison.Ordinal) || field.Value.Contains("data-i18n-label=", StringComparison.Ordinal);
            Assert.True(labelled, field.Value);
        }
    }

    [Fact]
    public void ColourIsNeverTheOnlySignal()
    {
        // Status dots are decorative; every state also has a visible text label next to it.
        foreach (Match dot in DotRegex().Matches(Html))
            Assert.Contains("aria-hidden=\"true\"", dot.Value, StringComparison.Ordinal);

        Assert.Contains("id=\"connection-label\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"status-pill-label\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"printer-state\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_FollowTheSystemThemeMotionContrastAndFocusSettings()
    {
        Assert.Contains("@media (prefers-color-scheme: dark)", Css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", Css, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", Css, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", Css, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_UsesLogicalDirections_SoRightToLeftMirrors()
    {
        Assert.DoesNotMatch(@"(margin|padding|border)-(left|right)\b|[\s{;](left|right)\s*:|text-align:\s*(left|right)|float\s*:", Css);
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\s{Regex.Escape(name)}=\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("<button[^>]*role=\"tab\"[^>]*>")]
    private static partial Regex TabRegex();

    [GeneratedRegex("<section[^>]*role=\"tabpanel\"[^>]*>")]
    private static partial Regex PanelRegex();

    [GeneratedRegex("<button[^>]*>")]
    private static partial Regex ButtonRegex();

    [GeneratedRegex("<(?:select|input)[^>]*>")]
    private static partial Regex FieldRegex();

    [GeneratedRegex("<span[^>]*class=\"pb-dot[^\"]*\"[^>]*>")]
    private static partial Regex DotRegex();
}
