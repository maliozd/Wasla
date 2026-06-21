using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Wasla.PrintBridge.Setup;

/// <summary>
/// Registers (or repairs) the per-user <c>wasla-printbridge://</c> protocol handler under
/// <c>HKCU\Software\Classes</c> so the browser can launch this app. Per-user registration does
/// not require administrator rights. Never registers any legacy OrderHub scheme.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrintBridgeProtocolRegistrar
{
    private const string FriendlyName = "URL:Wasla Print Bridge Protocol";

    public static void RegisterOrRepair(ILogger logger)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                logger.LogWarning("Skipping protocol registration: executable path could not be resolved.");
                return;
            }

            var command = $"\"{exePath}\" \"%1\"";

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes")
                ?? throw new InvalidOperationException("Unable to open HKCU\\Software\\Classes.");
            using var schemeKey = classes.CreateSubKey(PrintBridgeProtocolUri.Scheme);

            schemeKey.SetValue(string.Empty, FriendlyName);
            schemeKey.SetValue("URL Protocol", string.Empty);

            using (var iconKey = schemeKey.CreateSubKey("DefaultIcon"))
                iconKey.SetValue(string.Empty, $"\"{exePath}\",0");

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            var existing = commandKey.GetValue(string.Empty) as string;
            if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
            {
                commandKey.SetValue(string.Empty, command);
                logger.LogInformation("Registered wasla-printbridge protocol handler for the current user.");
            }
        }
        catch (Exception ex)
        {
            // Registration is best-effort; manual setup remains available.
            logger.LogWarning(ex, "Failed to register the wasla-printbridge protocol handler.");
        }
    }
}
