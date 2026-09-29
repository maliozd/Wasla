using Wasla.Application.GuidedSetup;

namespace Wasla.Web.Models.GuidedSetup;

public enum GuidedSetupCardKind
{
    /// <summary>NotStarted: the one-time Start-or-Skip decision.</summary>
    FirstUse = 0,

    /// <summary>InProgress at a setup section: resume or end.</summary>
    Resume = 1,

    /// <summary>InProgress and order training is next. The training itself arrives in the next phase.</summary>
    OrderTrainingNext = 2
}

/// <param name="Sections">The sections this user may complete, in journey order.</param>
/// <param name="CurrentSectionKey">The permitted current section while InProgress.</param>
public sealed record GuidedSetupDashboardViewModel(
    GuidedSetupCardKind Kind,
    IReadOnlyList<string> Sections,
    string? CurrentSectionKey)
{
    /// <summary>Users without administrative setup permissions only get order training.</summary>
    public bool IsOrderTrainingOnly =>
        Sections.Count == 1 && Sections[0] == GuidedSetupSections.LiveScreenDemo;
}

/// <param name="IsReady">From persisted setup facts: an active usable connection, or a connected Print Bridge device.</param>
public sealed record GuidedSetupSectionPanelViewModel(
    string SectionKey,
    bool IsReady,
    int SectionNumber,
    int SectionCount,
    string? NextSectionKey);

public static class GuidedSetupCopy
{
    public static string SectionNameKey(string? sectionKey) => sectionKey switch
    {
        GuidedSetupSections.PlatformConnections => "GuidedSetup.Section.PlatformConnections",
        GuidedSetupSections.PrintBridge => "GuidedSetup.Section.PrintBridge",
        _ => "GuidedSetup.Section.LiveScreenDemo"
    };
}
