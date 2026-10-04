using Wasla.Domain.Enums;

namespace Wasla.Application.Demos;

/// <summary>
/// The one timing rule for a practice order's automatic steps. Wasla.Worker moves it (ReadyForPickup → OnTheWay →
/// Delivered) and the Live Screen stops showing a delivered practice order exactly when this rule says so, and the
/// Live Screen's countdown shows the same deadline. Each automatic stage starts when the session entered its status
/// (its UpdatedAt, which every status change sets; for Delivered also its CompletedAtUtc) and lasts
/// <see cref="StageDuration"/>. Real orders never use it: their delivered window stays
/// <c>LiveScreenVisibility.RecentDeliveredWindow</c>.
/// </summary>
public static class GuidedDemoTiming
{
    /// <summary>How long each automatic practice stage lasts: picked up, delivered, then gone from the Live Screen.</summary>
    public static readonly TimeSpan StageDuration = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How soon Wasla.Worker's demo scheduler looks again at a tenant whose practice order still waits for its user's
    /// own steps (New, Accepted, Preparing), so a new Ready stage is found long before its deadline.
    /// </summary>
    public static readonly TimeSpan UserStepCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Ready → the platform courier picks it up.</summary>
    public const string PickUp = "PickUp";

    /// <summary>OnTheWay → it is delivered.</summary>
    public const string Deliver = "Deliver";

    /// <summary>Delivered → it leaves the Live Screen (training itself goes on).</summary>
    public const string Leave = "Leave";

    /// <summary>
    /// The automatic step waiting on a practice order in <paramref name="status"/>, which it entered at
    /// <paramref name="enteredAtUtc"/>; null for statuses the restaurant moves itself.
    /// </summary>
    public static GuidedDemoAutomaticStep? StepFor(OrderStatus status, DateTime enteredAtUtc) =>
        status switch
        {
            OrderStatus.ReadyForPickup => new GuidedDemoAutomaticStep(PickUp, DueAt(enteredAtUtc)),
            OrderStatus.OnTheWay => new GuidedDemoAutomaticStep(Deliver, DueAt(enteredAtUtc)),
            OrderStatus.Delivered => new GuidedDemoAutomaticStep(Leave, DueAt(enteredAtUtc)),
            _ => null
        };

    public static DateTime DueAt(DateTime enteredAtUtc) => enteredAtUtc + StageDuration;

    /// <summary>
    /// A stage that started at or before this moment is due at <paramref name="nowUtc"/>; one that started after it
    /// is not. The Worker moves sessions with <c>enteredAt &lt;= DueFrom(now)</c>; the Live Screen keeps a delivered
    /// practice order only while <c>deliveredAt &gt; DueFrom(now)</c>.
    /// </summary>
    public static DateTime DueFrom(DateTime nowUtc) => nowUtc - StageDuration;
}

/// <summary>What happens to a practice order automatically, and when (UTC).</summary>
public sealed record GuidedDemoAutomaticStep(string Action, DateTime DueAtUtc);
