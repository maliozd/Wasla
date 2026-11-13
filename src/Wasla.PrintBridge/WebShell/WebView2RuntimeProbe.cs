using Microsoft.Web.WebView2.Core;

namespace Wasla.PrintBridge.WebShell;

public sealed record WebView2RuntimeAvailability(bool IsAvailable, string? Version);

public interface IWebView2RuntimeProbe
{
    WebView2RuntimeAvailability Probe();
}

/// <summary>
/// Detects an installed Evergreen WebView2 Runtime. The app window needs it; without it the tray shows
/// <see cref="WebView2RuntimeNotice"/> and keeps printing. The setup EXE installs the runtime when it is missing or older
/// than <see cref="MinimumRuntimeVersion"/>, and its repair does again (WAS-62).
/// </summary>
public sealed class WebView2RuntimeProbe : IWebView2RuntimeProbe
{
    /// <summary>
    /// Oldest runtime the shell is allowed to use (December 2023 stable channel). The setup EXE's detection uses the same
    /// version (installer/print-bridge/Bundle, checked by PrintBridgeInstallerSourceTests).
    /// </summary>
    public const string MinimumRuntimeVersion = "120.0.2210.55";

    private readonly string? _browserExecutableFolder;

    public WebView2RuntimeProbe()
    {
    }

    /// <summary>
    /// For tests: look for the runtime in this folder only, as <c>WEBVIEW2_BROWSER_EXECUTABLE_FOLDER</c> makes the app do,
    /// so a missing runtime can be shown on a computer that has one.
    /// </summary>
    internal WebView2RuntimeProbe(string browserExecutableFolder)
    {
        _browserExecutableFolder = browserExecutableFolder;
    }

    public WebView2RuntimeAvailability Probe()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(_browserExecutableFolder);
            if (string.IsNullOrWhiteSpace(version))
                return new WebView2RuntimeAvailability(false, null);

            var supported = CoreWebView2Environment.CompareBrowserVersions(version, MinimumRuntimeVersion) >= 0;
            return new WebView2RuntimeAvailability(supported, version);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException
                                       or DllNotFoundException
                                       or BadImageFormatException
                                       or System.Runtime.InteropServices.COMException
                                       or ArgumentException)
        {
            return new WebView2RuntimeAvailability(false, null);
        }
    }
}
