using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
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

        // A wasla-printbridge://setup?... URI may be passed by the OS when the browser launches us.
        var setupUri = ExtractSetupUri(args);

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance is null)
        {
            // Already running: forward the setup URI to the primary instance instead of starting a second tray app.
            if (!string.IsNullOrWhiteSpace(setupUri) &&
                SetupInstanceChannel.TrySend(setupUri!, TimeSpan.FromSeconds(3)))
            {
                return;
            }

            MessageBox.Show(
                PrintBridgeLocalizer.GetStringForCulture("Message.AlreadyRunning", startupCulture, Environment.NewLine),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var services = PrintBridgeAppServices.Build();

        // Register/repair the per-user protocol handler on each launch (no admin rights required).
        // An isolated Debug instance (WASLA_PRINTBRIDGE_DATA_ROOT) must not take over the real install's
        // protocol handler or setup pipe.
        var startupLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.PrintBridge.Startup");
        var isolated = PrintBridgePaths.IsIsolatedDevelopmentRoot;
        if (isolated)
            startupLogger.LogWarning("Isolated development data root in use; protocol handler and setup IPC are disabled.");
        else
            PrintBridgeProtocolRegistrar.RegisterOrRepair(startupLogger);

        var context = new TrayApplicationContext(services);

        // Receive setup URIs forwarded from future second instances.
        using var channel = new SetupInstanceChannel(
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.PrintBridge.SetupIpc"));
        channel.UriReceived += uri => context.HandleSetupUri(uri);
        if (!isolated)
            channel.StartListening();

        // Once the message loop is running: a setup URI supplied on this launch is processed; without one, a first run
        // (or a start without a usable token) opens the connection setup. A configured start stays in the tray.
        var startupTimer = new System.Windows.Forms.Timer { Interval = 250 };
        startupTimer.Tick += (_, _) =>
        {
            startupTimer.Stop();
            startupTimer.Dispose();
            if (!string.IsNullOrWhiteSpace(setupUri))
                context.HandleSetupUri(setupUri!);
            else
                context.ShowSetupIfNotConnected();
        };
        startupTimer.Start();

        System.Windows.Forms.Application.Run(context);
    }

    private static string? ExtractSetupUri(string[] args) =>
        args?.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) &&
            a.StartsWith(PrintBridgeProtocolUri.Scheme + "://", StringComparison.OrdinalIgnoreCase));

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
