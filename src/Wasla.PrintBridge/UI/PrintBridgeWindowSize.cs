namespace Wasla.PrintBridge.UI;

/// <summary>
/// Stable desktop window client-area defaults and clamping for Print Bridge.
/// Pure logic (no WinForms) so it can be unit-tested independently of form chrome.
/// </summary>
internal static class PrintBridgeWindowSize
{
    /// <summary>Stable default usable content width.</summary>
    public const int DefaultClientWidth = 1220;

    /// <summary>Stable default usable content height.</summary>
    public const int DefaultClientHeight = 740;

    /// <summary>Safe minimum usable content width so Settings side actions stay visible.</summary>
    public const int MinimumClientWidth = 1160;

    /// <summary>Safe minimum usable content height so Status/Settings/History do not clip.</summary>
    public const int MinimumClientHeight = 680;

    /// <summary>
    /// Conservative non-client width allowance (left+right borders) used when chrome
    /// cannot be measured yet, or for pure clamp math against saved outer sizes.
    /// </summary>
    public const int EstimatedNonClientWidth = 16;

    /// <summary>
    /// Conservative non-client height allowance (title bar + top/bottom borders).
    /// Sized for typical WinForms caption chrome on 100–150% DPI.
    /// </summary>
    public const int EstimatedNonClientHeight = 48;

    private const int MaximumOuterWidth = 10000;
    private const int MaximumOuterHeight = 10000;

    /// <summary>Outer width that is large enough to preserve <see cref="MinimumClientWidth"/>.</summary>
    public static int MinimumOuterWidth => MinimumClientWidth + EstimatedNonClientWidth;

    /// <summary>Outer height that is large enough to preserve <see cref="MinimumClientHeight"/>.</summary>
    public static int MinimumOuterHeight => MinimumClientHeight + EstimatedNonClientHeight;

    /// <summary>Outer width used for first launch when ClientSize cannot be applied yet.</summary>
    public static int DefaultOuterWidth => DefaultClientWidth + EstimatedNonClientWidth;

    /// <summary>Outer height used for first launch when ClientSize cannot be applied yet.</summary>
    public static int DefaultOuterHeight => DefaultClientHeight + EstimatedNonClientHeight;

    /// <summary>
    /// Resolves outer window size for persistence/restore paths. Saved too-small outer
    /// bounds are clamped up so the resulting client area stays at/above the minimum.
    /// </summary>
    public static (int Width, int Height) ResolveStartupOuterSize(int? savedOuterWidth, int? savedOuterHeight) =>
        (
            ClampOuterWidth(savedOuterWidth ?? DefaultOuterWidth),
            ClampOuterHeight(savedOuterHeight ?? DefaultOuterHeight));

    public static int ClampOuterWidth(int outerWidth) =>
        Math.Clamp(outerWidth, MinimumOuterWidth, MaximumOuterWidth);

    public static int ClampOuterHeight(int outerHeight) =>
        Math.Clamp(outerHeight, MinimumOuterHeight, MaximumOuterHeight);

    public static int OuterWidthForClient(int clientWidth, int nonClientWidth) =>
        Math.Max(clientWidth + Math.Max(0, nonClientWidth), MinimumOuterWidth);

    public static int OuterHeightForClient(int clientHeight, int nonClientHeight) =>
        Math.Max(clientHeight + Math.Max(0, nonClientHeight), MinimumOuterHeight);
}
