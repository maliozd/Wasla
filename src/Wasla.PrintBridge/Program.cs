using System.Globalization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge;

/// <summary>
/// <c>Wasla.PrintBridge.exe</c>: the tray and WebView2 app of one Windows user in one session (WAS-61). It is a client of
/// the Print Bridge service, which runs the only engine; it never polls, prints or reads the service's data folder.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The saved language lives in the service's settings; until the service answers, the default (Turkish) is used.
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
        var startedAtSignIn = PrintBridgeSignInStart.IsRequestedBy(args);

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstance = SingleInstanceGuard.TryAcquire(out _);
        if (singleInstance is null)
        {
            // This user's app in this session is running: forward the setup link to it, and only to a pipe this user
            // owns (WAS-92). A refused, missing or broken pipe ends here.
            if (!string.IsNullOrWhiteSpace(setupUri) &&
                SetupInstanceChannel.TrySend(setupUri!, TimeSpan.FromSeconds(3)) == SetupForwardResult.Delivered)
            {
                return;
            }

            if (startedAtSignIn)
                return;

            MessageBox.Show(
                PrintBridgeLocalizer.GetStringForCulture("Message.AlreadyRunning", startupCulture, Environment.NewLine),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var services = PrintBridgeUiServices.Build(startupCulture);

        // The installed app uses the setup's machine-wide protocol handler and removes a per-user copy an earlier Print
        // Bridge wrote; a development or support copy registers or repairs its per-user handler (WAS-62).
        // An isolated Debug instance (WASLA_PRINTBRIDGE_DATA_ROOT) must not take over the real install's
        // protocol handler or setup pipe.
        var startupLogger = services.Loggers.CreateLogger("Wasla.PrintBridge.Startup");
        var isolated = PrintBridgePaths.IsIsolatedDevelopmentRoot;
        if (isolated)
            startupLogger.LogWarning("Isolated development data root in use; protocol handler and setup IPC are disabled.");
        else
            PrintBridgeProtocolRegistrar.RegisterOrRepair(startupLogger);

        var context = new TrayApplicationContext(services);

        // Receive setup URIs forwarded from future second instances.
        using var channel = new SetupInstanceChannel(services.Loggers.CreateLogger("Wasla.PrintBridge.SetupIpc"));
        channel.UriReceived += uri => context.HandleSetupUri(uri);
        if (!isolated)
            channel.StartListening();

        // Process a setup URI supplied on this launch once the message loop is running.
        if (!string.IsNullOrWhiteSpace(setupUri))
        {
            var startupTimer = new System.Windows.Forms.Timer { Interval = 250 };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                startupTimer.Dispose();
                context.HandleSetupUri(setupUri!);
            };
            startupTimer.Start();
        }

        System.Windows.Forms.Application.Run(context);
    }

    private static string? ExtractSetupUri(string[] args) =>
        args?.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) &&
            a.StartsWith(PrintBridgeProtocolUri.Scheme + "://", StringComparison.OrdinalIgnoreCase));

    private static string InitializeStartupCulture()
    {
        var cultureName = SupportedCultures.ResolveStartupCulture(null);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        return cultureName;
    }
}
