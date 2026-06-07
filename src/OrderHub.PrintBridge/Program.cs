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
                "OrderHub Print Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var services = PrintBridgeAppServices.Build();
        Application.Run(new TrayApplicationContext(services));
    }
}
