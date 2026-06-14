namespace Wasla.Application.Abstractions.Printing;

public static class PrintBridgeDeviceLimits
{
    // TODO: Derive from customer membership plan (WaslaPlanCatalog.IncludedPrintBridgeDevices) when plan-based enforcement is implemented.
    public const int AllowedActiveDeviceCount = 3;
}
