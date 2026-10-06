using System.Text.RegularExpressions;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Keeps theme-preference.js the only theme implementation: every themed layout loads it before its stylesheets and
/// names its app, and no other script or view sets the theme attribute or touches the theme storage keys. Behavior is
/// covered by theme-preference.test.js and <see cref="ThemePreferenceBrowserTests"/>.
/// </summary>
public sealed class ThemePreferenceContractTests
{
    public static TheoryData<string, string> ThemedLayouts() => new()
    {
        { "Areas/Tenant/Views/Shared/_TenantLayout.cshtml", "tenant" },
        { "Areas/Tenant/Views/Shared/_OrdersDisplayLayout.cshtml", "tenant" },
        { "Areas/Admin/Views/Shared/_AdminLayout.cshtml", "admin" }
    };

    [Theory]
    [MemberData(nameof(ThemedLayouts))]
    public void ThemedLayouts_LoadTheThemeScriptBeforeAnyStylesheet_AndNameTheirApp(string layout, string scope)
    {
        var source = Web(layout);
        Assert.Matches(new Regex($"<html [^>]*data-wasla-theme-scope=\"{scope}\""), source);

        var script = source.IndexOf("<script src=\"~/js/theme-preference.js\" asp-append-version=\"true\"></script>", StringComparison.Ordinal);
        var head = source.IndexOf("<head>", StringComparison.Ordinal);
        var firstStylesheet = Regex.Match(source, "<link [^>]*stylesheet").Index;
        Assert.True(head >= 0 && script > head && script < firstStylesheet, $"{layout}: theme-preference.js must load in <head> before the stylesheets");

        // The body takes the resolved theme from the script; a server-rendered value would be wrong for dark users.
        Assert.DoesNotMatch(new Regex("<body [^>]*data-bs-theme"), source);
        Assert.DoesNotContain("localStorage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyThemePreferenceJs_SetsTheThemeOrTouchesItsStorageKeys()
    {
        var root = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web");
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith(Path.Combine("js", "theme-preference.js"), StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"setAttribute\(\s*[""']data-bs-theme|[""']Wasla\.(tenant\.)?theme[""']|prefers-color-scheme"))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();
        Assert.Empty(offenders);
    }

    private static string Web(string relative) =>
        File.ReadAllText(TenantOperationsRulesTests.RepoFile(["src", "Wasla.Web", .. relative.Split('/')]));
}
