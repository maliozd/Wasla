using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.ServiceClient;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// The app's composition root (WAS-61). It holds what a user's session needs: its own log, the UI culture and texts, and
/// the engine, which is the Print Bridge service reached through <see cref="PrintBridgeServiceClient"/>. Nothing here
/// polls, prints, holds the device token or writes the service's data folder; <c>PrintBridgeUiCompositionTests</c>
/// checks it.
/// </summary>
public sealed class PrintBridgeUiServices : IAsyncDisposable
{
    private PrintBridgeUiServices(ILoggerFactory loggers, PrintBridgeCultureService culture, IPrintBridgeEngine engine)
    {
        Loggers = loggers;
        Culture = culture;
        Localizer = new PrintBridgeLocalizer(culture);
        Engine = engine;
    }

    /// <summary>The app's own log, per Windows user: <c>%LOCALAPPDATA%\Wasla\PrintBridge\logs</c>.</summary>
    internal static string LogFilePath =>
        Path.Combine(PrintBridgePaths.AppLogDirectory, "wasla-print-bridge-app-.log");

    public ILoggerFactory Loggers { get; }

    public PrintBridgeCultureService Culture { get; }

    public PrintBridgeLocalizer Localizer { get; }

    public IPrintBridgeEngine Engine { get; }

    /// <summary>The production app: the client of the installed service (or, in a Debug build with a development root, of that root's console service).</summary>
    public static PrintBridgeUiServices Build(string startupCulture)
    {
        var loggers = CreateLoggers();
        var culture = new PrintBridgeCultureService();
        culture.Initialize(startupCulture);
        var client = new PrintBridgeServiceClient(
            PrintBridgeServiceClientOptions.ForCurrentBuild(),
            loggers.CreateLogger("Wasla.PrintBridge.ServiceClient"));
        client.Start();
        return new PrintBridgeUiServices(loggers, culture, client);
    }

    /// <summary>For tests: the app over a given engine (a client of a test service, or a fake).</summary>
    internal static PrintBridgeUiServices ForEngine(IPrintBridgeEngine engine, string culture = SupportedCultures.Turkish, ILoggerFactory? loggers = null)
    {
        var cultureService = new PrintBridgeCultureService();
        cultureService.Initialize(culture);
        return new PrintBridgeUiServices(loggers ?? CreateLoggers(), cultureService, engine);
    }

    public async ValueTask DisposeAsync()
    {
        if (Engine is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
        Loggers.Dispose();
    }

    private static ILoggerFactory CreateLoggers()
    {
        Directory.CreateDirectory(PrintBridgePaths.AppLogDirectory);
        const string logTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day, outputTemplate: logTemplate, shared: true)
            .CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }
}
