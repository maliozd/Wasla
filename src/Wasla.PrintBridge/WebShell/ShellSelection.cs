using Microsoft.Web.WebView2.Core;

namespace Wasla.PrintBridge.WebShell;

public enum PrintBridgeShellMode
{
    /// <summary>
    /// The classic WinForms window: only the explicit <c>Ui.Shell = "WinForms"</c> rollback, or a fallback when the
    /// WebView2 app cannot run.
    /// </summary>
    WinForms,

    /// <summary>The WebView2 app, the default.</summary>
    WebView2
}

public enum ShellFallbackReason
{
    None,

    /// <summary><c>Ui.Shell = "WinForms"</c>: the explicit rollback to the classic window.</summary>
    ExplicitWinForms,

    /// <summary>The WebView2 Runtime is missing or older than the supported minimum.</summary>
    RuntimeUnavailable
}

/// <param name="IgnoredSetting">An unrecognized <c>Ui.Shell</c> value was ignored and the default was used.</param>
public sealed record ShellDecision(PrintBridgeShellMode Mode, ShellFallbackReason FallbackReason, bool IgnoredSetting = false);

public sealed record WebView2RuntimeAvailability(bool IsAvailable, string? Version);

public interface IWebView2RuntimeProbe
{
    WebView2RuntimeAvailability Probe();
}

public static class ShellSelection
{
    public const string WinFormsValue = "WinForms";
    public const string WebView2Value = "WebView2";

    /// <summary>
    /// Chooses the window opened from the tray and for a setup link. The WebView2 app is the default: a missing or
    /// empty <c>Ui.Shell</c> selects it, and so does an unrecognized value (ignored, so a typo never switches to the
    /// rollback). Only <c>WinForms</c> selects the classic window explicitly, and then the runtime is not probed at
    /// all. When the runtime is missing or too old, the classic window is the fallback.
    /// </summary>
    public static ShellDecision Decide(string? configuredShell, IWebView2RuntimeProbe runtimeProbe)
    {
        var value = configuredShell?.Trim();
        if (string.Equals(value, WinFormsValue, StringComparison.OrdinalIgnoreCase))
            return new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.ExplicitWinForms);

        var ignored = !string.IsNullOrEmpty(value) && !string.Equals(value, WebView2Value, StringComparison.OrdinalIgnoreCase);
        return runtimeProbe.Probe().IsAvailable
            ? new ShellDecision(PrintBridgeShellMode.WebView2, ShellFallbackReason.None, ignored)
            : new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable, ignored);
    }
}

/// <summary>
/// Detects an installed Evergreen WebView2 Runtime. WAS-53 only detects and falls back; delivering the
/// runtime with the installer belongs to WAS-55.
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
