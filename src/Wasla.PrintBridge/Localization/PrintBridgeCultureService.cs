using System.Globalization;

namespace Wasla.PrintBridge.Localization;

public sealed class PrintBridgeCultureService
{
    public CultureInfo CurrentCulture { get; private set; } =
        CultureInfo.GetCultureInfo(SupportedCultures.Default);

    public bool IsRightToLeft => CurrentCulture.TextInfo.IsRightToLeft;

    /// <summary>
    /// Raised on the calling thread after <see cref="Initialize"/> switches to a different culture, so every
    /// open window (classic or WebView2 shell) and the tray menu can re-localize.
    /// </summary>
    public event EventHandler? CultureChanged;

    public void Initialize(string? userLanguage)
    {
        var resolved = SupportedCultures.ResolveStartupCulture(userLanguage);
        var previous = CurrentCulture;
        CurrentCulture = CultureInfo.GetCultureInfo(resolved);
        ApplyToCurrentThread(CurrentCulture);

        if (!string.Equals(previous.Name, CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
            CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyToCurrentThread(CultureInfo culture)
    {
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }
}
