using Wasla.PrintBridge.Configuration;

namespace Wasla.PrintBridge.WebShell;

public static class ShellPaths
{
    /// <summary>Packaged page files. Only this folder is mapped to the shell origin.</summary>
    public static string AssetDirectory =>
        Path.Combine(AppContext.BaseDirectory, ShellNavigationPolicy.AssetFolderName);

    /// <summary>
    /// Per-user WebView2 profile (browser cache only; the shell stores no settings or credentials there).
    /// Kept out of the install folder, which may be read-only, and out of the shared ProgramData folder.
    /// An isolated Debug instance (and a test run) keeps it inside its own data root.
    /// </summary>
    public static string UserDataDirectory =>
        PrintBridgePaths.IsIsolatedDevelopmentRoot || PrintBridgePaths.HasTestRootOverride
            ? Path.Combine(PrintBridgePaths.ProgramDataRoot, "webview2-profile")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wasla",
                "PrintBridge",
                "WebView2");
}
