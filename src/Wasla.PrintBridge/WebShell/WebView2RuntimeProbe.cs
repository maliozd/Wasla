using Microsoft.Web.WebView2.Core;

namespace Wasla.PrintBridge.WebShell;

public sealed record WebView2RuntimeAvailability(bool IsAvailable, string? Version);

public interface IWebView2RuntimeProbe
{
    WebView2RuntimeAvailability Probe();
}

/// <summary>
/// Detects an installed Evergreen WebView2 Runtime. The app window needs it; without it the tray shows
/// <see cref="WebView2RuntimeNotice"/> and keeps printing. Delivering the runtime with the installer belongs to WAS-55.
/// </summary>
public sealed class WebView2RuntimeProbe : IWebView2RuntimeProbe
{
    /// <summary>Oldest runtime the shell is allowed to use (December 2023 stable channel).</summary>
    public const string MinimumRuntimeVersion = "120.0.2210.55";

    public WebView2RuntimeAvailability Probe()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
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
