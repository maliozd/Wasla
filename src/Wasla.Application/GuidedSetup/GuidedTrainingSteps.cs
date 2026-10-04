using Wasla.Application.Demos;
using Wasla.Domain.Enums;

namespace Wasla.Application.GuidedSetup;

/// <summary>
/// Stable step keys of the order-training section (<see cref="GuidedSetupSections.LiveScreenDemo"/>).
/// Each practice step is named after the practice order's lifecycle status, so the step shown is
/// derived from the authoritative demo session instead of a page counter. Keys never change meaning.
/// </summary>
public static class GuidedTrainingSteps
{
    /// <summary>Introduces the Live Screen; the user starts the practice order from here.</summary>
    public const string Intro = "intro";

    public const string PracticeNew = "practice-new";
    public const string PracticeAccepted = "practice-accepted";
    public const string PracticePreparing = "practice-preparing";

    /// <summary>The restaurant's last action is done; the platform courier collects the order.</summary>
    public const string PracticeReady = "practice-ready";

    public const string PracticeOnTheWay = "practice-on-the-way";

    /// <summary>Explains the delivered window; training can be completed from here.</summary>
    public const string PracticeDelivered = "practice-delivered";

    /// <summary>All steps in journey order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Intro, PracticeNew, PracticeAccepted, PracticePreparing, PracticeReady, PracticeOnTheWay, PracticeDelivered
    ];

    public static int IndexOf(string? stepKey)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], stepKey, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>True once the practice order has been started in this journey.</summary>
    public static bool IsPracticeStep(string? stepKey) => IndexOf(stepKey) > 0;

    /// <summary>The step for a practice order in <paramref name="status"/>, or null for a status training does not use.</summary>
    public static string? ForDemoStatus(OrderStatus status) => status switch
    {
        OrderStatus.New => PracticeNew,
        OrderStatus.Accepted => PracticeAccepted,
        OrderStatus.Preparing => PracticePreparing,
        OrderStatus.ReadyForPickup => PracticeReady,
        OrderStatus.OnTheWay => PracticeOnTheWay,
        OrderStatus.Delivered => PracticeDelivered,
        _ => null
    };

    /// <summary>
    /// The step to show, from the saved step and the user's most recent practice order.
    /// <list type="bullet">
    /// <item>An open practice order always decides the step.</item>
    /// <item>Before the practice started (saved step intro or missing), a closed order is ignored,
    /// so an earlier delivered demo can never let the user skip the practice.</item>
    /// <item>After the practice started, a delivered order means the explanation step; an order that
    /// closed without delivery (expired or finished) or is missing means a new practice order is needed.</item>
    /// </list>
    /// </summary>
    public static string Derive(string? savedStepKey, GuidedDemoSummary? latest)
    {
        if (latest is { IsOpen: true } && ForDemoStatus(latest.Status) is { } openStep)
            return openStep;

        if (!IsPracticeStep(savedStepKey))
            return Intro;

        return latest is { IsOpen: false, Status: OrderStatus.Delivered } ? PracticeDelivered : Intro;
    }

    /// <summary>True when <paramref name="next"/> is later in the journey than <paramref name="current"/>.</summary>
    public static bool IsAfter(string next, string? current) => IndexOf(next) > IndexOf(current);
}
