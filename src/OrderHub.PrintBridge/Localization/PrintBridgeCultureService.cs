using System.Globalization;

namespace OrderHub.PrintBridge.Localization;

public sealed class PrintBridgeCultureService
{
    public CultureInfo CurrentCulture { get; private set; } =
        CultureInfo.GetCultureInfo(SupportedCultures.Default);

    public bool IsRightToLeft => CurrentCulture.TextInfo.IsRightToLeft;

    public void Initialize(string? userLanguage)
    {
        var resolved = SupportedCultures.ResolveStartupCulture(userLanguage);
        CurrentCulture = CultureInfo.GetCultureInfo(resolved);
        ApplyToCurrentThread(CurrentCulture);
    }

    private static void ApplyToCurrentThread(CultureInfo culture)
    {
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }
}
