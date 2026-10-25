using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Contracts for the shared central Admin layout: the RTL-safe AdminLTE corrections, the phone navigation drawer, the
/// detail definition lists and the Admin title strings. The real-browser measurements are in
/// <see cref="AdminRtlLayoutBrowserTests"/> and <see cref="AdminMobileNavigationBrowserTests"/>.
/// </summary>
public sealed partial class AdminLayoutContractTests
{
    private static readonly string[] AdminTitleKeys = ["Layout.CentralAdmin", "Admin.LoginTitle", "Admin.CentralAdmin"];

    [Fact]
    public void Layout_LoadsTheCorrections_AfterAdminLte()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Shared", "_AdminLayout.cshtml");

        var adminLte = layout.IndexOf("adminlte.min.css", StringComparison.Ordinal);
        var corrections = layout.IndexOf("~/css/wasla-admin-layout.css", StringComparison.Ordinal);
        Assert.True(adminLte >= 0 && corrections > adminLte, "wasla-admin-layout.css must load after adminlte.min.css.");
    }

    [Fact]
    public void LiveRegion_UsesTheVisuallyHiddenTechnique_WithoutALargeOffset()
    {
        var rule = Rule(Css(), ".live-region:not(.live-region-visible)");

        foreach (var declaration in new[]
                 {
                     "position: absolute;", "width: 1px;", "height: 1px;", "overflow: hidden;",
                     "clip: rect(0, 0, 0, 0);", "clip-path: inset(50%);", "white-space: nowrap;",
                     "left: auto;", "right: auto;", "inset-inline-start: 0;"
                 })
            Assert.Contains(declaration, rule, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutCorrections_NeverHideFromAssistiveTechnology_OrUseLargeOffsets()
    {
        // Declarations only: the header comment quotes the AdminLTE rule it replaces.
        var css = Regex.Replace(Css(), @"/\*[\s\S]*?\*/", string.Empty);

        Assert.DoesNotMatch(LargeNegativeOffset(), css);
        Assert.DoesNotContain("display: none", css, StringComparison.Ordinal);
        Assert.DoesNotContain("visibility: hidden", css, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-hidden", css, StringComparison.Ordinal);
    }

    [Fact]
    public void RtlPhoneSidebar_IsMovedOffCanvasOnTheRight()
    {
        var css = Css();

        var media = Regex.Match(css, @"@media \(max-width: 991\.98px\) \{(?<body>[\s\S]*?)\n\}").Groups["body"].Value;
        Assert.Contains("margin-right: calc(var(--lte-sidebar-width) * -1);", Rule(media, "[dir=\"rtl\"] .sidebar-expand-lg .app-sidebar"), StringComparison.Ordinal);
        Assert.Contains("margin-left: 0;", Rule(media, "[dir=\"rtl\"] .sidebar-expand-lg .app-sidebar"), StringComparison.Ordinal);
        Assert.Contains("margin-right: 0;", Rule(media, "[dir=\"rtl\"] .sidebar-expand-lg.sidebar-open .app-sidebar"), StringComparison.Ordinal);
    }

    [Fact]
    public void PhoneNavigation_UsesAdminLtesToggle_WithAnAccessibleNameAndState()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Shared", "_AdminLayout.cshtml");

        var toggle = Element(layout, "id=\"waslaAdminNavToggle\"");
        foreach (var attribute in new[]
                 {
                     "type=\"button\"", "d-lg-none", "data-lte-toggle=\"sidebar\"", "aria-controls=\"waslaAdminSidebar\"",
                     "aria-expanded=\"false\"", "aria-label=\"@L[\"Layout.OpenNavigation\"]\""
                 })
            Assert.Contains(attribute, toggle, StringComparison.Ordinal);
        Assert.True(layout.IndexOf("id=\"waslaAdminNavToggle\"", StringComparison.Ordinal) < layout.IndexOf("wasla-admin-header-brand", StringComparison.Ordinal),
            "The toggle comes first in the bar, before the compact logo.");

        var close = Element(layout, "data-wasla-admin-nav-close");
        Assert.Contains("data-lte-toggle=\"sidebar\"", close, StringComparison.Ordinal);
        Assert.Contains("d-lg-none", close, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@L[\"Layout.CloseNavigation\"]\"", close, StringComparison.Ordinal);
        Assert.Contains("<aside id=\"waslaAdminSidebar\" class=\"app-sidebar\"", layout, StringComparison.Ordinal);

        var adminLte = layout.IndexOf("adminlte.min.js", StringComparison.Ordinal);
        var nav = layout.IndexOf("~/js/wasla-admin-nav.js", StringComparison.Ordinal);
        Assert.True(adminLte >= 0 && nav > adminLte, "wasla-admin-nav.js must load after adminlte.min.js.");

        foreach (var culture in new[] { "", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU" })
        {
            var resources = Load(culture);
            Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault("Layout.OpenNavigation")), $"Layout.OpenNavigation missing in '{culture}'");
            Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault("Layout.CloseNavigation")), $"Layout.CloseNavigation missing in '{culture}'");
        }
    }

    [Fact]
    public void PhoneNavigationScript_FollowsAdminLtesState_InsteadOfKeepingItsOwn()
    {
        var script = Regex.Replace(Read("src", "Wasla.Web", "wwwroot", "js", "wasla-admin-nav.js"), @"/\*[\s\S]*?\*/|//[^\n]*", string.Empty);

        // AdminLTE's [data-lte-toggle] handler is the only toggle handler; this script never toggles on a click itself.
        Assert.DoesNotContain("toggle.addEventListener", script, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.add", script, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.remove", script, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.toggle", script, StringComparison.Ordinal);
        Assert.Contains("new window.adminlte.PushMenu(sidebar, {}).collapse()", script, StringComparison.Ordinal);
        Assert.Contains("new MutationObserver(", script, StringComparison.Ordinal);
        // Initialises once even if the script is included twice.
        Assert.Contains("data-wasla-admin-nav", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminDetailDefinitionLists_UseLogicalMargins_AndWrapLongValues()
    {
        var css = Css();

        // Every rule block that styles these dd elements: the start margin is removed logically, never physically.
        var ddRules = Regex.Matches(css, @"(?m)^\.app-main dl\.row > dd \{[^}]*\}").Select(m => m.Value).ToArray();
        Assert.NotEmpty(ddRules);
        Assert.Contains(ddRules, rule => rule.Contains("margin-inline-start: 0;", StringComparison.Ordinal));
        Assert.All(ddRules, rule => Assert.DoesNotMatch(@"margin-(left|right)\s*:", rule));
        var both = Rule(css, ".app-main dl.row > dt,\n.app-main dl.row > dd");
        Assert.Contains("overflow-wrap: anywhere;", both, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", both, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminTitles_AreArabicInArabic_AndNotTheTurkishText()
    {
        var arabic = Load(".ar-SA");
        var turkish = Load(".tr-TR");

        foreach (var key in AdminTitleKeys)
        {
            Assert.Matches(@"[؀-ۿ]", arabic[key]);
            Assert.DoesNotMatch("[çğıöşüÇĞİÖŞÜ]", arabic[key]);
            Assert.NotEqual(turkish[key], arabic[key]);
            Assert.Contains("Wasla", arabic[key], StringComparison.Ordinal);
        }

        // No Arabic value anywhere in the file carries Turkish-only letters.
        Assert.DoesNotContain(arabic, pair => Regex.IsMatch(pair.Value, "[çğıöşüÇĞİÖŞÜ]"));
    }

    private static string Css() => Read("src", "Wasla.Web", "wwwroot", "css", "wasla-admin-layout.css");

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No rule for {selector}");
        var end = css.IndexOf('}', start);
        return css[start..end];
    }

    /// <summary>The opening tag of the first element whose attributes contain <paramref name="marker"/>.</summary>
    private static string Element(string markup, string marker)
    {
        var at = markup.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"No element with {marker}");
        var start = markup.LastIndexOf('<', at);
        var end = markup.IndexOf('>', at);
        return markup[start..(end + 1)];
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(TenantOperationsRulesTests.RepoFile(segments)).ReplaceLineEndings("\n");

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    // left/right/inset/margin offsets of 100px or more in the negative direction.
    [GeneratedRegex(@"(left|right|inset[a-z-]*|margin[a-z-]*)\s*:\s*-\d{3,}px")]
    private static partial Regex LargeNegativeOffset();
}
