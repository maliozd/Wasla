using System.Text.RegularExpressions;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The native title bar and the page's top band (header and tabs) share one surface, so they read as one band
/// instead of a Windows accent-coloured bar above the app. What DWM draws cannot be read back in a test; these
/// tests pin the colours the window hands it and the page tokens they must match.
/// </summary>
public sealed partial class ShellWindowThemeTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));
    private static readonly string Css = File.ReadAllText(Path.Combine(AssetsDirectory(), "shell.css"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TitleBar_UsesTheTopBandSurface_AndDimsItsTextWhenInactive(bool dark)
    {
        var palette = ShellPalette.For(dark);

        var active = ShellWindowTheme.TitleBarColorsFor(dark, active: true, highContrast: false);
        var inactive = ShellWindowTheme.TitleBarColorsFor(dark, active: false, highContrast: false);

        Assert.Equal(new TitleBarColors(palette.Surface, palette.Text, palette.BorderStrong), active);
        Assert.Equal(new TitleBarColors(palette.Surface, palette.TextSecondary, palette.Border), inactive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TitleBar_LeavesEveryColourToWindows_UnderHighContrast(bool dark)
    {
        Assert.Null(ShellWindowTheme.TitleBarColorsFor(dark, active: true, highContrast: true));
        Assert.Equal(unchecked((int)0xFFFFFFFF), ShellWindowTheme.ToColorRef(null));
    }

    [Fact]
    public void ColorRef_IsBlueGreenRed()
    {
        Assert.Equal(0x004273E8, ShellWindowTheme.ToColorRef(Color.FromArgb(0xE8, 0x73, 0x42)));
    }

    [Fact]
    public void TopBand_IsTheSurfaceTheTitleBarGets_InBothThemes()
    {
        Assert.Matches(@"--pb-chrome:\s*var\(--wasla-surface\);", Css);
        Assert.Matches(@"\.pb-header\s*\{[^}]*background:\s*var\(--pb-chrome\);", Css);
        Assert.Matches(@"\.pb-tabs\s*\{[^}]*background:\s*var\(--pb-chrome\);", Css);

        // The first --wasla-surface is the light :root value, the second the prefers-color-scheme: dark one.
        var surfaces = SurfaceTokenRegex().Matches(Css).Select(m => ColorTranslator.FromHtml(m.Groups[1].Value).ToArgb()).ToArray();
        Assert.Equal([ShellPalette.Light.Surface.ToArgb(), ShellPalette.Dark.Surface.ToArgb()], surfaces);
    }

    [Fact]
    public void Brand_IsTheWaslaLogoAlone_NamedByTheFullAppTitle()
    {
        var title = TitleRegex().Match(Html);

        Assert.True(title.Success);
        Assert.Contains("data-i18n-label=\"Common.AppTitle\"", title.Groups[1].Value, StringComparison.Ordinal);
        var content = title.Groups[2].Value.Trim();
        Assert.Matches(@"^<img class=""pb-brand-logo"" src=""wasla-logo\.svg"" alt=""""[^>]*>$", content);
    }

    [GeneratedRegex(@"--wasla-surface:\s*(#[0-9A-Fa-f]{6});")]
    private static partial Regex SurfaceTokenRegex();

    [GeneratedRegex(@"<h1 class=""pb-title""([^>]*)>(.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}
