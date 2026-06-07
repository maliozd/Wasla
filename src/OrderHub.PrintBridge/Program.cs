using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Options;
using OrderHub.PrintBridge.Printing;
using OrderHub.PrintBridge.Services;
using Serilog;

PrintBridgePaths.EnsureProgramDataDirectories();

var builder = Host.CreateApplicationBuilder(args);

var programDataConfig = PrintBridgePaths.ProgramDataConfigPath;
if (File.Exists(programDataConfig))
{
    builder.Configuration.AddJsonFile(programDataConfig, optional: true, reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();

var logPath = Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "orderhub-print-bridge-.log");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

builder.Services.AddSerilog((services, cfg) =>
{
    cfg.ReadFrom.Services(services)
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
});

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = PrintBridgePaths.ServiceName;
    });
}

var orderHub = builder.Configuration.GetSection(OrderHubOptions.SectionName).Get<OrderHubOptions>() ?? new OrderHubOptions();
var bridge = builder.Configuration.GetSection(PrintBridgeOptions.SectionName).Get<PrintBridgeOptions>() ?? new PrintBridgeOptions();

ValidateOptions(orderHub, bridge);

if (string.IsNullOrWhiteSpace(bridge.BridgeName))
    bridge.BridgeName = Environment.MachineName;

builder.Services.AddSingleton(orderHub);
builder.Services.AddSingleton(bridge);
builder.Services.AddSingleton<ReceiptFormatter>();
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<IReceiptPrinter, WindowsReceiptPrinter>();
else
    throw new PlatformNotSupportedException("OrderHub Print Bridge requires Windows.");

var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

builder.Services.AddHttpClient("PrintBridgeApi", (sp, client) =>
{
    var hub = sp.GetRequiredService<OrderHubOptions>();
    client.BaseAddress = new Uri(hub.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("PrintBridgeApi");
    return new OrderHubPrintBridgeClient(
        http,
        sp.GetRequiredService<OrderHubOptions>(),
        sp.GetRequiredService<PrintBridgeOptions>(),
        appVersion);
});

builder.Services.AddHostedService<PrintBridgeWorker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OrderHub.PrintBridge.Startup");

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
    orderHub.BaseUrl,
    bridge.DryRun,
    bridge.PrinterMode,
    string.IsNullOrWhiteSpace(bridge.PrinterName) ? "(not set)" : bridge.PrinterName,
    bridge.BridgeName,
    bridge.IdlePollIntervalSeconds,
    bridge.BusyPollIntervalSeconds,
    bridge.ErrorPollIntervalSeconds,
    bridge.MaxJobsPerPoll,
    File.Exists(programDataConfig) ? programDataConfig : "appsettings.json",
    PrintBridgePaths.ProgramDataLogDirectory);

await host.RunAsync();

static void ValidateOptions(OrderHubOptions orderHub, PrintBridgeOptions bridge)
{
    if (string.IsNullOrWhiteSpace(orderHub.BaseUrl))
        throw new InvalidOperationException("OrderHub:BaseUrl is required.");

    if (string.IsNullOrWhiteSpace(orderHub.AgentToken))
        throw new InvalidOperationException("OrderHub:AgentToken is required.");

    if (!bridge.DryRun && string.IsNullOrWhiteSpace(bridge.PrinterName))
        throw new InvalidOperationException("PrintBridge:PrinterName is required when DryRun is false.");
}
