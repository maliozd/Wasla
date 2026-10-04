using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Contracts for the shared central Admin layout: the RTL-safe AdminLTE corrections and the Admin title strings.
/// The real-browser measurements are in <see cref="AdminRtlLayoutBrowserTests"/>.
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
