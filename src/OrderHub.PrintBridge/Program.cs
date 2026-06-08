using System.Globalization;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Localization;
using OrderHub.PrintBridge.Services;
using OrderHub.PrintBridge.UI;

namespace OrderHub.PrintBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var startupCulture = InitializeStartupCulture();

        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show(
                PrintBridgeLocalizer.GetStringForCulture("Message.RequiresWindows", startupCulture),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance is null)
        {
            MessageBox.Show(
                PrintBridgeLocalizer.GetStringForCulture("Message.AlreadyRunning", startupCulture, Environment.NewLine),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var services = PrintBridgeAppServices.Build();
        Application.Run(new TrayApplicationContext(services));
    }

    private static string InitializeStartupCulture()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        var store = new PrintBridgeSettingsStore();
        store.SeedProgramDataConfigIfMissing();
        var document = store.Load();
        var cultureName = SupportedCultures.ResolveStartupCulture(document.Ui.Language);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        return cultureName;
    }
}
