using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Serilog;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeAppServices
{
    /// <summary>The named <see cref="HttpClient"/> of <see cref="WaslaPrintBridgeClient"/> for device requests.</summary>
    internal const string DeviceApiClientName = "PrintBridgeApi";

    /// <summary>Serilog adds the date before the extension: one <c>wasla-print-bridge-yyyyMMdd.log</c> per day.</summary>
    internal static string LogFilePath =>
        Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "wasla-print-bridge-.log");

    public static ServiceProvider Build() => Build(configureForTests: null);

    /// <summary>For tests: <paramref name="configureForTests"/> registers replacements after the application's own services.</summary>
    internal static ServiceProvider Build(Action<IServiceCollection>? configureForTests)
    {
        PrintBridgePaths.EnsureProgramDataDirectories();

        var store = new PrintBridgeSettingsStore();
        var document = store.Load();

        if (string.IsNullOrWhiteSpace(document.PrintBridge.MachineName))
            document.PrintBridge.MachineName = Environment.MachineName;

        var cultureService = new PrintBridgeCultureService();
        cultureService.Initialize(document.Ui.Language);

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.Wasla, document.PrintBridge, document.Ui);

        var appVersion = new AppVersionInfo(typeof(PrintBridgeAppServices).Assembly);

        const string logTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day, outputTemplate: logTemplate)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        services.AddSingleton(store);
        services.AddSingleton(cultureService);
        services.AddSingleton<PrintBridgeLocalizer>();
        services.AddSingleton(holder);
        services.AddSingleton(appVersion);
        services.AddSingleton<LocalPrintJobHistoryStore>();
        services.AddSingleton<ReceiptFormatter>();

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Wasla Print Bridge requires Windows.");

        services.AddSingleton<IReceiptPrinter, WindowsReceiptPrinter>();
        AddWaslaApiClient(services, appVersion);
        services.AddSingleton<PrintBridgeDeviceMetadataSync>();
        services.AddSingleton<PrintBridgeRuntime>();
        services.AddSingleton<IPrintBridgeConnectionGuard>(sp => sp.GetRequiredService<PrintBridgeRuntime>());
        // Release builds trust only HTTPS Wasla hosts for setup links; Debug builds also the local development hosts.
        services.AddSingleton(Wasla.PrintBridge.Setup.PrintBridgeServerTrustPolicy.ForCurrentBuild());
        services.AddSingleton<Wasla.PrintBridge.Setup.PrintBridgeAutoSetupCoordinator>();
        configureForTests?.Invoke(services);

        var provider = services.BuildServiceProvider();
        LogStartup(provider, holder);
        return provider;
    }

    /// <summary>
    /// Registers <see cref="WaslaPrintBridgeClient"/> with the device API client (health, polling, job reports, reprint,
    /// device name and manual-setup checks) and the setup client factory. Needs the
    /// <see cref="PrintBridgeSettingsHolder"/> and logging. Tests use it to get the production client.
    /// </summary>
    internal static void AddWaslaApiClient(IServiceCollection services, AppVersionInfo appVersion)
    {
        services.AddHttpClient(DeviceApiClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // Every device request carries X-PrintBridge-Token, and some a body. HttpClient keeps custom headers on a
            // redirect, to any origin, and 307/308 also send the body again. So a redirect is returned to the client as
            // an answer and never followed (WAS-191), as for setup requests (WAS-91).
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            // The factory logs request headers at Trace, values included. The app's logger filters that category to
            // Warning, but a lowered level must still never write the device token.
            .RedactLoggedHeaders(static _ => true);
        services.AddSingleton<PrintBridgeSetupHttpClientFactory>();

        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(DeviceApiClientName);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<WaslaPrintBridgeClient>();
            return new WaslaPrintBridgeClient(
                http,
                sp.GetRequiredService<PrintBridgeSettingsHolder>(),
                sp.GetRequiredService<PrintBridgeSetupHttpClientFactory>(),
                logger,
                appVersion.HeaderValue);
        });
    }

    private static void LogStartup(ServiceProvider provider, PrintBridgeSettingsHolder holder)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.PrintBridge.Startup");
        var (connection, bridge, ui) = holder.Snapshot();

        if (OperatingSystem.IsWindows())
        {
            var installedPrinters = RawPrinterHelper.ListInstalledPrinters();
            logger.LogInformation("Installed Windows printers ({Count}):", installedPrinters.Count);
            foreach (var printerName in installedPrinters)
                logger.LogInformation("  Printer: {PrinterName}", printerName);

            if (!string.IsNullOrWhiteSpace(bridge.PrinterName))
            {
                var configuredFound = installedPrinters.Any(p =>
                    string.Equals(p, bridge.PrinterName, StringComparison.OrdinalIgnoreCase));
                logger.LogInformation(
                    "Configured PrinterName={PrinterName}, FoundInWindows={Found}",
                    bridge.PrinterName,
                    configuredFound);
            }
        }

        logger.LogInformation(
            "Effective config: ServerUrl={ServerUrl}, DryRun={DryRun}, PrinterMode={PrinterMode}, PrinterName={PrinterName}, DisplayName={DisplayName}, MachineName={MachineName}, Language={Language}, IdlePoll={IdlePoll}s, BusyPoll={BusyPoll}s, ErrorPoll={ErrorPoll}s, MaxJobsPerPoll={MaxJobsPerPoll}, ConfigPath={ConfigPath}, LogPath={LogPath}",
            connection.ServerUrl,
            bridge.DryRun,
            bridge.PrinterMode,
            string.IsNullOrWhiteSpace(bridge.PrinterName) ? "(not set)" : bridge.PrinterName,
            string.IsNullOrWhiteSpace(bridge.DisplayName) ? "(not set)" : bridge.DisplayName,
            bridge.MachineName,
            ui.Language,
            bridge.IdlePollIntervalSeconds,
            bridge.BusyPollIntervalSeconds,
            bridge.ErrorPollIntervalSeconds,
            bridge.MaxJobsPerPoll,
            PrintBridgePaths.ProgramDataConfigPath,
            PrintBridgePaths.ProgramDataLogDirectory);
    }
}
