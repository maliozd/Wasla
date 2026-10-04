using System.Text.RegularExpressions;

namespace Wasla.Application.GuidedSetup;

/// <summary>
/// Stable keys stored with a user's guided-setup position. Keys never change meaning once shipped;
/// retire a key instead of reusing it. Screens and routes are chosen by the Web layer, not here.
/// </summary>
public static partial class GuidedSetupSections
{
    public const string PlatformConnections = "platform-connections";
    public const string PrintBridge = "print-bridge";
    public const string LiveScreenDemo = "live-screen-demo";

    public const int MaxKeyLength = 64;

    /// <summary>All sections in journey order.</summary>
    public static IReadOnlyList<string> All { get; } = [PlatformConnections, PrintBridge, LiveScreenDemo];

    public static bool IsKnown(string? sectionKey) =>
        sectionKey is not null && All.Contains(sectionKey, StringComparer.Ordinal);

    /// <summary>
    /// Step keys belong to later phases, so only their shape is checked: lowercase words joined by
    /// hyphens, at most <see cref="MaxKeyLength"/> characters. A missing step is allowed.
    /// </summary>
    public static bool IsValidStepKey(string? stepKey) =>
        stepKey is null || (stepKey.Length <= MaxKeyLength && StepKeyPattern().IsMatch(stepKey));

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StepKeyPattern();
}

/// <summary>
/// What the current user may do, as decided by the Web layer's existing authorization policies.
/// The application layer never inspects role names.
/// </summary>
public sealed record GuidedSetupCapabilities(
    bool CanManagePlatformConnections,
    bool CanManagePrintBridge,
    bool CanManageOrders);

public static class GuidedSetupPlan
{
    /// <summary>
    /// The sections this user can complete, in journey order. Sections the user may not act on are
    /// left out instead of shown as broken steps. An empty list means guided setup does not apply.
    /// </summary>
    public static IReadOnlyList<string> SectionsFor(GuidedSetupCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var sections = new List<string>(GuidedSetupSections.All.Count);
        if (capabilities.CanManagePlatformConnections)
            sections.Add(GuidedSetupSections.PlatformConnections);
        if (capabilities.CanManagePrintBridge)
            sections.Add(GuidedSetupSections.PrintBridge);
        if (capabilities.CanManageOrders)
            sections.Add(GuidedSetupSections.LiveScreenDemo);
        return sections;
    }
}
