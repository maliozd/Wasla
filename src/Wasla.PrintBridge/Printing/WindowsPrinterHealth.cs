using System.Runtime.Versioning;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Printing;

[SupportedOSPlatform("windows")]
internal static class WindowsPrinterHealth
{
    public static PrinterHealthStatus Resolve(PrintBridgeOptions bridge)
    {
        if (bridge.DryRun)
            return PrinterHealthStatus.DryRun;

        if (string.IsNullOrWhiteSpace(bridge.PrinterName))
            return PrinterHealthStatus.NotConfigured;

        return RawPrinterHelper.PrinterExists(bridge.PrinterName)
            ? PrinterHealthStatus.Ready
            : PrinterHealthStatus.NotFound;
    }
}
