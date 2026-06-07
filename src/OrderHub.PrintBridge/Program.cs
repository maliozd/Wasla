using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Services;
using OrderHub.PrintBridge.UI;

namespace OrderHub.PrintBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show(
                "OrderHub Print Bridge requires Windows.",
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
                "OrderHub Print Bridge is already running.\n\nOpen the existing app from the system tray icon.",
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var services = PrintBridgeAppServices.Build();
        Application.Run(new TrayApplicationContext(services));
    }
}
