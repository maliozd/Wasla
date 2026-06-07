using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Options;
using OrderHub.PrintBridge.Printing;
using Serilog;

namespace OrderHub.PrintBridge.Services;

public static class PrintBridgeAppServices
{
    public static ServiceProvider Build()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();

        var store = new PrintBridgeSettingsStore();
        var document = store.Load();

        if (string.IsNullOrWhiteSpace(document.PrintBridge.BridgeName))
            document.PrintBridge.BridgeName = Environment.MachineName;

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge);

        var logPath = Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "orderhub-print-bridge-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        services.AddSingleton(store);
        services.AddSingleton(holder);
        services.AddSingleton<ReceiptFormatter>();

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OrderHub Print Bridge requires Windows.");

        services.AddSingleton<IReceiptPrinter, WindowsReceiptPrinter>();

        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        services.AddHttpClient("PrintBridgeApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("PrintBridgeApi");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OrderHubPrintBridgeClient>();
            return new OrderHubPrintBridgeClient(
                http,
                sp.GetRequiredService<PrintBridgeSettingsHolder>(),
                logger,
                appVersion);
        });

        services.AddSingleton<PrintBridgeRuntime>();

        var provider = services.BuildServiceProvider();
        LogStartup(provider, holder);
        return provider;
    }

    private static void LogStartup(ServiceProvider provider, PrintBridgeSettingsHolder holder)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("OrderHub.PrintBridge.Startup");
        var (hub, bridge) = holder.Snapshot();

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
            "Effective config: BaseUrl={BaseUrl}, DryRun={DryRun}, PrinterMode={PrinterMode}, PrinterName={PrinterName}, BridgeName={BridgeName}, IdlePoll={IdlePoll}s, BusyPoll={BusyPoll}s, ErrorPoll={ErrorPoll}s, MaxJobsPerPoll={MaxJobsPerPoll}, ConfigPath={ConfigPath}, LogPath={LogPath}",
            hub.BaseUrl,
            bridge.DryRun,
            bridge.PrinterMode,
            string.IsNullOrWhiteSpace(bridge.PrinterName) ? "(not set)" : bridge.PrinterName,
            bridge.BridgeName,
            bridge.IdlePollIntervalSeconds,
            bridge.BusyPollIntervalSeconds,
            bridge.ErrorPollIntervalSeconds,
            bridge.MaxJobsPerPoll,
            PrintBridgePaths.ProgramDataConfigPath,
            PrintBridgePaths.ProgramDataLogDirectory);
    }
}
