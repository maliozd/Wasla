using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// The printers this Windows user's session sees (WAS-61). The service runs as its own account in session 0 and sees
/// only printers installed for the machine; a printer connection a user added in their own profile (for example a shared
/// network printer added from Settings) lives in that user's registry hive and is invisible to the service. When the
/// service reports the saved printer as missing but this user's Windows lists it, the app says exactly that, so the fix
/// (install it for all users) is clear.
/// </summary>
internal static class UserPrinterProbe
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly object Sync = new();
    private static IReadOnlyList<string> _cached = Array.Empty<string>();
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    /// <summary>Whether this user's Windows lists <paramref name="printerName"/> (the caller knows the service does not).</summary>
    public static bool IsInstalledForThisUser(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || !OperatingSystem.IsWindows())
            return false;

        IReadOnlyList<string> installed;
        lock (Sync)
        {
            if (DateTime.UtcNow - _cachedAtUtc > CacheLifetime)
            {
                try
                {
                    _cached = RawPrinterHelper.ListInstalledPrinters();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _cached = Array.Empty<string>();
                }

                _cachedAtUtc = DateTime.UtcNow;
            }

            installed = _cached;
        }

        return installed.Any(p => string.Equals(p, printerName.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
