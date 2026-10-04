namespace Wasla.Web.Ui;

/// <summary>
/// Display rules for the platform-connections list. Does not change sync or circuit-breaker behavior.
/// </summary>
public static class PlatformConnectionListPresentation
{
    public static bool ShowConsecutiveErrors(int consecutiveFailures) => consecutiveFailures > 0;

    public static bool ShowCircuitOpen(DateTime? circuitOpenUntilUtc, DateTime utcNow) =>
        circuitOpenUntilUtc is DateTime until && until > utcNow;
}
