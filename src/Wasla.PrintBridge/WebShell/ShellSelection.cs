using Microsoft.Web.WebView2.Core;

namespace Wasla.PrintBridge.WebShell;

public enum PrintBridgeShellMode
{
    /// <summary>The classic WinForms window. Default until WAS-54 proves feature parity.</summary>
    WinForms,

    /// <summary>The WebView2 status shell. Opt-in through <c>Ui.Shell = "WebView2"</c>.</summary>
    WebView2
}

public enum ShellFallbackReason
{
    None,
    NotRequested,
    UnrecognizedSetting,
    RuntimeUnavailable
}

public sealed record ShellDecision(PrintBridgeShellMode Mode, ShellFallbackReason FallbackReason);

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
    /// Chooses the window opened from the tray. The WebView2 runtime is probed only when the WebView2
    /// shell was explicitly requested, so the default configuration never loads WebView2 at all.
    /// </summary>
    public static ShellDecision Decide(string? configuredShell, IWebView2RuntimeProbe runtimeProbe)
    {
        var value = configuredShell?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, WinFormsValue, StringComparison.OrdinalIgnoreCase))
            return new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.NotRequested);

        if (!string.Equals(value, WebView2Value, StringComparison.OrdinalIgnoreCase))
            return new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.UnrecognizedSetting);

        return runtimeProbe.Probe().IsAvailable
            ? new ShellDecision(PrintBridgeShellMode.WebView2, ShellFallbackReason.None)
            : new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable);
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
