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
    public static ServiceProvider Build()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();

        var store = new PrintBridgeSettingsStore();
        var document = store.Load();

        if (string.IsNullOrWhiteSpace(document.PrintBridge.MachineName))
            document.PrintBridge.MachineName = Environment.MachineName;

        var cultureService = new PrintBridgeCultureService();
        cultureService.Initialize(document.Ui.Language);

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);

        var appVersion = new AppVersionInfo();

        var uiLogBuffer = new UiLogBuffer();
        var logPath = Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "orderhub-print-bridge-.log");
        const string logTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new UiLogBufferSink(uiLogBuffer))
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, outputTemplate: logTemplate)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(uiLogBuffer);
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

        services.AddHttpClient("PrintBridgeApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("PrintBridgeApi");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<WaslaPrintBridgeClient>();
            return new WaslaPrintBridgeClient(
                http,
                sp.GetRequiredService<PrintBridgeSettingsHolder>(),
                logger,
                appVersion.HeaderValue);
        });

        services.AddSingleton<PrintBridgeDeviceMetadataSync>();
        services.AddSingleton<PrintBridgeRuntime>();

        var provider = services.BuildServiceProvider();
        LogStartup(provider, holder);
        return provider;
    }

    private static void LogStartup(ServiceProvider provider, PrintBridgeSettingsHolder holder)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.PrintBridge.Startup");
        var (hub, bridge, ui) = holder.Snapshot();

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
            hub.ServerUrl,
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
