using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Colours and fonts shared by the WebView2 app window and its native dialogs, so a dialog looks like part of the
/// app. The values mirror the tokens in <c>WebShell/Assets/shell.css</c> (Wasla foundation: paper and ink, one
/// restrained orange accent, borders before shadows). Light or dark follows the Windows app theme, as the page does.
/// </summary>
internal sealed record ShellPalette(
    Color Canvas,
    Color Surface,
    Color SurfaceMuted,
    Color Border,
    Color BorderStrong,
    Color Text,
    Color TextSecondary,
    Color Accent,
    Color AccentHover,
    Color AccentText,
    Color Danger,
    Color DangerSoft,
    Color Success,
    Color Info)
{
    public static readonly ShellPalette Light = new(
        Canvas: Color.FromArgb(0xF7, 0xF4, 0xEE),
        Surface: Color.FromArgb(0xFF, 0xFE, 0xFA),
        SurfaceMuted: Color.FromArgb(0xF4, 0xF0, 0xE9),
        Border: Color.FromArgb(0xE3, 0xDD, 0xD3),
        BorderStrong: Color.FromArgb(0xD3, 0xCA, 0xBD),
        Text: Color.FromArgb(0x28, 0x23, 0x1F),
        TextSecondary: Color.FromArgb(0x74, 0x6D, 0x66),
        Accent: Color.FromArgb(0xE8, 0x73, 0x42),
        AccentHover: Color.FromArgb(0xB9, 0x4F, 0x2A),
        AccentText: Color.White,
        Danger: Color.FromArgb(0xB4, 0x23, 0x2C),
        DangerSoft: Color.FromArgb(0xF6, 0xE1, 0xE0),
        Success: Color.FromArgb(0x2F, 0x6F, 0x4E),
        Info: Color.FromArgb(0x3B, 0x6E, 0xA5));

    public static readonly ShellPalette Dark = new(
        Canvas: Color.FromArgb(0x1B, 0x18, 0x16),
        Surface: Color.FromArgb(0x24, 0x20, 0x1D),
        SurfaceMuted: Color.FromArgb(0x2C, 0x27, 0x24),
        Border: Color.FromArgb(0x3A, 0x33, 0x2E),
        BorderStrong: Color.FromArgb(0x4A, 0x42, 0x3B),
        Text: Color.FromArgb(0xF2, 0xED, 0xE6),
        TextSecondary: Color.FromArgb(0xB8, 0xAF, 0xA5),
        Accent: Color.FromArgb(0xE8, 0x73, 0x42),
        AccentHover: Color.FromArgb(0xF0, 0x91, 0x5F),
        AccentText: Color.White,
        Danger: Color.FromArgb(0xF2, 0x87, 0x8C),
        DangerSoft: Color.FromArgb(0x3E, 0x26, 0x27),
        Success: Color.FromArgb(0x7C, 0xC4, 0x9B),
        Info: Color.FromArgb(0x93, 0xB9, 0xE6));

    public static ShellPalette For(bool dark) => dark ? Dark : Light;
}

internal static class ShellWindowTheme
{
    private static readonly string FontFamilyName = ResolveFontFamily();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float Size, FontStyle Style), Font> Fonts = new();

    // Fonts are shared for the life of the process, so dialogs opened repeatedly create no new GDI fonts.
    public static Font BodyFont(float size = 10.5F) => Font(size, FontStyle.Regular);

    public static Font StrongFont(float size = 10.5F) => Font(size, FontStyle.Bold);

    private static Font Font(float size, FontStyle style) =>
        Fonts.GetOrAdd((size, style), key => new Font(FontFamilyName, key.Size, key.Style, GraphicsUnit.Point));

    /// <summary>Whether Windows asks apps to use the dark theme (Settings → Personalization → Colors).</summary>
    public static bool IsWindowsAppDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Matches the title bar to the palette (Windows 10 2004 and later; ignored elsewhere).</summary>
    public static void TrySetDarkTitleBar(IntPtr handle, bool dark)
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        var enabled = dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    // The page asks for "Segoe UI Variable Text" first; Windows 10 has only Segoe UI.
    private static string ResolveFontFamily()
    {
        foreach (var family in FontFamily.Families)
        {
            if (string.Equals(family.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
                return family.Name;
        }

        return "Segoe UI";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
