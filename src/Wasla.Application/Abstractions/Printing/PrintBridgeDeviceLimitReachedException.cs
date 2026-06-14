namespace Wasla.Application.Abstractions.Printing;

public sealed class PrintBridgeDeviceLimitReachedException : Exception
{
    public PrintBridgeDeviceLimitReachedException()
        : base("Print Bridge active device limit reached.")
    {
    }
}
