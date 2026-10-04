using Wasla.Application.GuidedSetup;

namespace Wasla.Web.Models.GuidedSetup;

public enum GuidedSetupCardKind
{
    /// <summary>NotStarted: the one-time Start-or-Skip decision.</summary>
    FirstUse = 0,

    /// <summary>InProgress at a setup section: resume or end.</summary>
    Resume = 1,

    /// <summary>InProgress and order training is the current section; it continues on the Live Screen.</summary>
    OrderTrainingNext = 2
}

/// <summary>
/// The Live Screen's order training, shown while the Owner is InProgress at the order-training section; null means
/// nothing at all. The first-use Start or Skip decision is only on the Dashboard: guided setup is Owner-only and an
/// Owner always has the Dashboard.
/// </summary>
/// <param name="StepKey">A <see cref="GuidedTrainingSteps"/> key derived from the practice order.</param>
/// <param name="DeliveredWindowMinutes">From <c>LiveScreenVisibility.RecentDeliveredWindow</c>; never written elsewhere.</param>
public sealed record GuidedTrainingViewModel(
    string StepKey,
    int SectionNumber,
    int SectionCount,
    int DeliveredWindowMinutes)
{
    public int StepNumber => GuidedTrainingSteps.IndexOf(StepKey) + 1;

    public static int StepCount => GuidedTrainingSteps.All.Count;
}

/// <summary>
/// The Dashboard's guided-setup card. Guided setup is Owner-only and the Owner may act on every section, so the card
/// always describes the whole journey; there is no order-training-only variant.
/// </summary>
/// <param name="Sections">The sections this user may complete, in journey order.</param>
/// <param name="CurrentSectionKey">The permitted current section while InProgress.</param>
public sealed record GuidedSetupDashboardViewModel(
    GuidedSetupCardKind Kind,
    IReadOnlyList<string> Sections,
    string? CurrentSectionKey);

/// <param name="IsReady">From persisted setup facts: an active usable connection, or a connected Print Bridge device.</param>
public sealed record GuidedSetupSectionPanelViewModel(
    string SectionKey,
    bool IsReady,
    int SectionNumber,
    int SectionCount,
    string? NextSectionKey);

/// <summary>
/// The connected Print Bridge section's device guide. Informational only: its single action is the guided section's
/// Continue POST. <see cref="ForOneDevice"/> is set on a device's own page, so the copy can say "this device".
/// </summary>
public sealed record GuidedDeviceGuideViewModel(int SectionNumber, int SectionCount, string? NextSectionKey)
{
    public bool ForOneDevice { get; init; }
}

public static class GuidedSetupCopy
{
    public static string SectionNameKey(string? sectionKey) => sectionKey switch
    {
        GuidedSetupSections.PlatformConnections => "GuidedSetup.Section.PlatformConnections",
        GuidedSetupSections.PrintBridge => "GuidedSetup.Section.PrintBridge",
        _ => "GuidedSetup.Section.LiveScreenDemo"
    };

    /// <summary>
    /// The primary button after a section was set up successfully, naming where it leads
    /// (e.g. "Continue to order training"). Separate keys, because languages inflect the section name.
    /// </summary>
    public static string ContinueToKey(string? nextSectionKey) => nextSectionKey switch
    {
        GuidedSetupSections.PrintBridge => "GuidedSetup.Panel.ContinueToPrintBridge",
        GuidedSetupSections.LiveScreenDemo => "GuidedSetup.Panel.ContinueToOrderTraining",
        _ => "GuidedSetup.Panel.Continue"
    };

    /// <summary>Resource key prefix for a training step; append <c>.Title</c> or <c>.Body</c>.</summary>
    public static string TrainingStepKey(string stepKey) => stepKey switch
    {
        GuidedTrainingSteps.PracticeNew => "GuidedSetup.Training.Step.New",
        GuidedTrainingSteps.PracticeAccepted => "GuidedSetup.Training.Step.Accepted",
        GuidedTrainingSteps.PracticePreparing => "GuidedSetup.Training.Step.Preparing",
        GuidedTrainingSteps.PracticeReady => "GuidedSetup.Training.Step.Ready",
        GuidedTrainingSteps.PracticeOnTheWay => "GuidedSetup.Training.Step.OnTheWay",
        GuidedTrainingSteps.PracticeDelivered => "GuidedSetup.Training.Step.Delivered",
        _ => "GuidedSetup.Training.Step.Intro"
    };

    /// <summary>
    /// The restaurant action a step asks for, as the Live Screen's <c>data-order-action</c> value.
    /// Steps after Mark ready have none: the platform courier moves the order on its own.
    /// </summary>
    public static string? TrainingStepAction(string stepKey) => stepKey switch
    {
        GuidedTrainingSteps.PracticeNew => Wasla.Application.Demos.GuidedDemoTransitions.Approve,
        GuidedTrainingSteps.PracticeAccepted => Wasla.Application.Demos.GuidedDemoTransitions.StartPreparing,
        GuidedTrainingSteps.PracticePreparing => Wasla.Application.Demos.GuidedDemoTransitions.MarkReady,
        _ => null
    };
}
