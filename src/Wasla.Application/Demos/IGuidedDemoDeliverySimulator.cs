namespace Wasla.Application.Demos;

/// <summary>
/// Plays the platform courier for guided demos, the way a real order's courier steps arrive through
/// provider sync: a ReadyForPickup demo is picked up (OnTheWay), then delivered (Delivered), each
/// once its stage is due (<see cref="GuidedDemoTiming"/>). Touches only GuidedDemoSessions: never Orders,
/// provider clients, PrintJobs or notifications.
/// </summary>
public interface IGuidedDemoDeliverySimulator
{
    /// <summary>Returns the number of demo steps advanced. Safe to run repeatedly and concurrently.</summary>
    Task<int> AdvanceDueAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Advances what is due, as <see cref="AdvanceDueAsync"/>, and says when this tenant next needs a look: the
    /// earliest automatic deadline, or <see cref="GuidedDemoTiming.UserStepCheckInterval"/> from now while a practice
    /// order still waits for its user's steps; null when the tenant has no open practice order.
    /// </summary>
    Task<GuidedDemoAdvanceResult> AdvanceDueAndPlanAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>How many demo steps moved, and when (UTC) the tenant's practice orders next need the Worker.</summary>
public sealed record GuidedDemoAdvanceResult(int Advanced, DateTime? NextCheckAtUtc);
