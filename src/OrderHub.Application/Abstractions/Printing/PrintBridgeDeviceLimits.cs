namespace OrderHub.Application.Abstractions.Printing;

public static class PrintBridgeDeviceLimits
{
    // TODO: Derive from customer membership plan (OrderHubPlanCatalog.IncludedPrintBridgeDevices) when plan-based enforcement is implemented.
    public const int AllowedActiveDeviceCount = 3;
}
