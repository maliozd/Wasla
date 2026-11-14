using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Wasla.PrintBridge.Setup;

/// <summary>The Wasla Print Bridge setup that installed this computer's Print Bridge (WAS-62), when there is one.</summary>
public interface IPrintBridgeInstalledSetup
{
    /// <summary>True when the setup that installed Print Bridge is registered in Installed Apps and its cached copy exists.</summary>
    bool CanRepair { get; }

    /// <summary>
    /// Starts that setup's repair (<c>/repair</c>). It installs a missing or too old WebView2 Runtime and repairs Print
    /// Bridge; Windows asks for administrator permission. False when it could not be started.
    /// </summary>
    bool StartRepair();
}

/// <summary>
/// Finds the installed setup in Installed Apps by the setup's upgrade code (the registration Burn writes under
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c>, with <c>BundleUpgradeCode</c> and
/// <c>BundleCachePath</c>). Administrators own that key and the package cache, so the path is not a user's to change.
/// Nothing from the page or the service selects what is started.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintBridgeInstalledSetup : IPrintBridgeInstalledSetup
{
    /// <summary>The setup EXE's UpgradeCode (installer/print-bridge/Bundle/Bundle.wxs; checked by a test).</summary>
    public const string SetupUpgradeCode = "{0CAF4165-E129-4DB9-A1DF-C719CFE65AF5}";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly ILogger _logger;
    private readonly Func<string?> _findCachedSetup;
    private readonly Func<string, bool> _start;

    public PrintBridgeInstalledSetup(ILogger logger)
        : this(logger, FindCachedSetupInRegistry, StartProcess)
    {
    }

    internal PrintBridgeInstalledSetup(ILogger logger, Func<string?> findCachedSetup, Func<string, bool> start)
    {
        _logger = logger;
        _findCachedSetup = findCachedSetup;
        _start = start;
    }

    /// <summary>For tests and copies without the setup: nothing to repair with.</summary>
    internal static IPrintBridgeInstalledSetup None { get; } =
        new PrintBridgeInstalledSetup(NullLogger.Instance, () => null, _ => false);

    public bool CanRepair => _findCachedSetup() is not null;

    public bool StartRepair()
    {
        var setup = _findCachedSetup();
        if (setup is null)
        {
            _logger.LogWarning("Repair was requested, but no installed Wasla Print Bridge setup was found.");
            return false;
        }

        var started = _start(setup);
        if (started)
            _logger.LogInformation("Started the installed Wasla Print Bridge setup to repair the installation.");
        else
            _logger.LogWarning("The installed Wasla Print Bridge setup could not be started.");
        return started;
    }

    /// <summary>The cached setup EXE of the registration with the setup's upgrade code, in either registry view.</summary>
    internal static string? FindCachedSetup(IEnumerable<RegistryKey?> uninstallRoots)
    {
        foreach (var root in uninstallRoots)
        {
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                if (entry?.GetValue("BundleUpgradeCode") is not string[] codes ||
                    !codes.Any(c => string.Equals(c, SetupUpgradeCode, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (entry.GetValue("BundleCachePath") is string path && Path.IsPathFullyQualified(path) && File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static string? FindCachedSetupInRegistry()
    {
        try
        {
            using var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var uninstall64 = machine64.OpenSubKey(UninstallKey);
            using var uninstall32 = machine32.OpenSubKey(UninstallKey);
            return FindCachedSetup([uninstall64, uninstall32]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool StartProcess(string setup)
    {
        try
        {
            // The setup asks Windows for administrator rights itself; its own window runs as this user.
            using var process = Process.Start(new ProcessStartInfo(setup, "/repair") { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
