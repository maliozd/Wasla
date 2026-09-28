namespace Wasla.Application.Demos;

/// <summary>
/// Plays the platform courier for guided demos, the way a real order's courier steps arrive through
/// provider sync: a ReadyForPickup demo is picked up (OnTheWay), then delivered (Delivered), each
/// after a short delay. Touches only GuidedDemoSessions: never Orders, provider clients, PrintJobs
/// or notifications.
/// </summary>
public interface IGuidedDemoDeliverySimulator
{
    /// <summary>Returns the number of demo steps advanced. Safe to run repeatedly and concurrently.</summary>
    Task<int> AdvanceDueAsync(Guid tenantId, CancellationToken ct);
}
