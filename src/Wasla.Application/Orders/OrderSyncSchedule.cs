namespace Wasla.Application.Orders;

/// <summary>
/// How often the Worker looks at the tenants' platform connections. Every cycle it syncs each connection whose own
/// <c>SyncIntervalSeconds</c> has elapsed, so a due connection waits at most one cycle. Screens that judge whether
/// synchronization is current use the same value (see <see cref="LiveScreenSyncRules"/>).
/// </summary>
public static class OrderSyncSchedule
{
    public const int WorkerCycleSeconds = 15;
}
