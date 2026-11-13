using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// The engine's composition root (WAS-61: moved from the app to the service). Everything that polls, claims, prints,
/// reports, holds the device token or writes the data folder is registered here and nowhere in the app.
/// </summary>
public static class PrintBridgeAppServices
{
    /// <summary>The named <see cref="HttpClient"/> of <see cref="WaslaPrintBridgeClient"/> for device requests.</summary>
    internal const string DeviceApiClientName = "PrintBridgeApi";

    private static readonly object LoggingSync = new();
    private static string? _loggingDirectory;

    /// <summary>Serilog adds the date before the extension: one <c>wasla-print-bridge-yyyyMMdd.log</c> per day.</summary>
    internal static string LogFilePath =>
        Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "wasla-print-bridge-.log");

    /// <summary>
    /// Points the process-wide Serilog logger at the data folder's log file. Called once by the service before anything
    /// else logs; again only when the data folder changed (tests).
    /// </summary>
    public static void ConfigureLogging()
    {
        lock (LoggingSync)
        {
            var directory = PrintBridgePaths.ProgramDataLogDirectory;
            if (string.Equals(_loggingDirectory, directory, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                PrintBridgePaths.EnsureProgramDataDirectories();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A folder the service may not write (not secured yet): the service still starts and reports it; the file
                // sink simply writes nothing until the folder is fixed.
            }

            const string logTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
            var previous = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day, outputTemplate: logTemplate, shared: true)
                .CreateLogger();
            (previous as IDisposable)?.Dispose();
            _loggingDirectory = directory;
        }
    }

    public static ServiceProvider Build() => Build(configureForTests: null);

    /// <summary>For tests: <paramref name="configureForTests"/> registers replacements after the engine's own services.</summary>
    internal static ServiceProvider Build(Action<IServiceCollection>? configureForTests)
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        ConfigureLogging();

        var store = new PrintBridgeSettingsStore();
        var document = store.Load();

        if (string.IsNullOrWhiteSpace(document.PrintBridge.MachineName))
            document.PrintBridge.MachineName = Environment.MachineName;

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.Wasla, document.PrintBridge, document.Ui);

        var appVersion = new AppVersionInfo(typeof(PrintBridgeAppServices).Assembly);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));
        services.AddSingleton(store);
        services.AddSingleton(holder);
        services.AddSingleton(appVersion);
        services.AddSingleton<LocalPrintJobHistoryStore>();
        services.AddSingleton<PrintBridgeJobJournal>();
        services.AddSingleton<ReceiptFormatter>();

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Wasla Print Bridge requires Windows.");

        services.AddSingleton<IReceiptPrinter, WindowsReceiptPrinter>();
        AddWaslaApiClient(services, appVersion);
        services.AddSingleton<PrintBridgeDeviceMetadataSync>();
        services.AddSingleton(sp => new PrintBridgeRuntime(
            sp.GetRequiredService<WaslaPrintBridgeClient>(),
            sp.GetRequiredService<ReceiptFormatter>(),
            sp.GetRequiredService<IReceiptPrinter>(),
            sp.GetRequiredService<PrintBridgeSettingsHolder>(),
            sp.GetRequiredService<PrintBridgeSettingsStore>(),
            sp.GetRequiredService<PrintBridgeDeviceMetadataSync>(),
            sp.GetRequiredService<LocalPrintJobHistoryStore>(),
            sp.GetRequiredService<AppVersionInfo>(),
            sp.GetRequiredService<ILogger<PrintBridgeRuntime>>(),
            sp.GetRequiredService<PrintBridgeJobJournal>()));
        services.AddSingleton<IPrintBridgeConnectionGuard>(sp => sp.GetRequiredService<PrintBridgeRuntime>());
        // Release builds trust only HTTPS Wasla hosts for setup links; Debug builds also the local development hosts.
        services.AddSingleton(Wasla.PrintBridge.Setup.PrintBridgeServerTrustPolicy.ForCurrentBuild());
        services.AddSingleton<Wasla.PrintBridge.Setup.PrintBridgeAutoSetupCoordinator>();
        services.AddSingleton<PrintBridgeEngineHost>();
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
            // The factory logs request headers at Trace, values included. The service's logger filters that category to
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
            logger.LogInformation("Printers this engine can use ({Count}):", installedPrinters.Count);
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
            "Effective config: ServerUrl={ServerUrl}, DryRun={DryRun}, PrinterMode={PrinterMode}, PrinterName={PrinterName}, DisplayName={DisplayName}, MachineName={MachineName}, Language={Language}, IdlePoll={IdlePoll}s, BusyPoll={BusyPoll}s, ErrorPoll={ErrorPoll}s, MaxJobsPerPoll={MaxJobsPerPoll}, StoppedByUser={StoppedByUser}, ConfigPath={ConfigPath}, LogPath={LogPath}",
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
            bridge.PrintingStoppedByUser,
            PrintBridgePaths.ProgramDataConfigPath,
            PrintBridgePaths.ProgramDataLogDirectory);
    }
}
