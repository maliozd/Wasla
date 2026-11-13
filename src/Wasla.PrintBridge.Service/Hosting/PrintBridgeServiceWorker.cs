using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// The service's lifetime (WAS-61), in this order:
/// <list type="number">
/// <item>Check the data folder's access rules (when running as a Windows Service). If they are unsafe, the engine never
/// starts; the pipe still answers, so the app can say why.</item>
/// <item>Start the pipe.</item>
/// <item>Take the machine-wide engine lock, retrying every <see cref="EngineLockRetry"/> while another engine holds it.</item>
/// <item>Build the engine, then start printing (unless the user stopped it) and finish a pending move.</item>
/// <item>On stop: refuse new commands, stop the engine (a claimed job gets the WAS-56 grace period; what it still lacked
/// is in the journal), close the pipe, release the lock.</item>
/// </list>
/// </summary>
internal sealed class PrintBridgeServiceWorker : BackgroundService
{
    private readonly PrintBridgeServiceOptions _options;
    private readonly PrintBridgeServiceBackend _backend;
    private readonly PrintBridgeIpcServer _server;
    private readonly ILogger<PrintBridgeServiceWorker> _logger;
    private readonly bool _verifyDataFolder;
    private readonly Func<ServiceProvider> _buildEngine;

    public PrintBridgeServiceWorker(
        PrintBridgeServiceOptions options,
        PrintBridgeServiceBackend backend,
        PrintBridgeIpcServer server,
        ILogger<PrintBridgeServiceWorker> logger,
        bool verifyDataFolder,
        Func<ServiceProvider> buildEngine)
    {
        _options = options;
        _backend = backend;
        _server = server;
        _logger = logger;
        _verifyDataFolder = verifyDataFolder;
        _buildEngine = buildEngine;
    }

    internal static TimeSpan EngineLockRetry { get; set; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = _options.Endpoint;
        _logger.LogInformation(
            "Wasla Print Bridge service starting. Service={ServiceName}, Pipe={PipeName}, Account={Account}",
            endpoint.ServiceName,
            endpoint.PipeName,
            CurrentAccountSid());

        if (_verifyDataFolder)
        {
            using var self = WindowsIdentity.GetCurrent();
            var problem = PrintBridgeDataFolderSecurity.Verify(_options.EffectiveDataRoot, self.User!);
            if (problem is not null)
            {
                _logger.LogError("The engine does not start: {Problem}. Run \"Wasla.PrintBridge.Service.exe secure-data\" as an administrator.", problem);
                _backend.SetUnavailable(Ipc.IpcAvailability.DataFolderNotSecured);
                _server.Start();
                await WaitForStopAsync(stoppingToken).ConfigureAwait(false);
                await _server.StopAsync().ConfigureAwait(false);
                return;
            }
        }
        else
        {
            _logger.LogWarning("Not running as a Windows Service: the data folder's access rules are not checked.");
        }

        _server.Start();

        PrintBridgeEngineLock? engineLock = null;
        ServiceProvider? engineServices = null;
        PrintBridgeEngineHost? engine = null;
        try
        {
            var waitingLogged = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                engineLock = PrintBridgeEngineLock.TryAcquire(endpoint.EngineLockName);
                if (engineLock is not null)
                    break;

                if (!waitingLogged)
                {
                    _logger.LogWarning(
                        "Another Print Bridge engine is running on this computer (for example an older Print Bridge app). The service does not print until it exits and checks again every {Seconds} s.",
                        EngineLockRetry.TotalSeconds);
                    waitingLogged = true;
                }

                _backend.SetUnavailable(Ipc.IpcAvailability.EngineHeldElsewhere);
                if (!await DelayAsync(EngineLockRetry, stoppingToken).ConfigureAwait(false))
                    return;
            }

            if (engineLock is null)
                return;

            _logger.LogInformation("Engine lock taken; this service is the only Print Bridge engine on the computer.");
            engineServices = _buildEngine();
            engine = engineServices.GetRequiredService<PrintBridgeEngineHost>();
            _backend.Activate(engine);
            engine.Activate();

            await WaitForStopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recovery (sc failureflag) restarts a service that ends with a non-zero exit code.
            _logger.LogCritical(ex, "The Print Bridge service failed.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            _logger.LogInformation("Wasla Print Bridge service stopping.");
            _backend.Deactivate();
            if (engine is not null)
                await engine.ShutdownAsync().ConfigureAwait(false);
            if (engineServices is not null)
                await engineServices.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _server.StopAsync().ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            engineLock?.Dispose();
            _logger.LogInformation("Wasla Print Bridge service stopped.");
        }
    }

    private static string CurrentAccountSid()
    {
        using var self = WindowsIdentity.GetCurrent();
        return self.User?.Value ?? "unknown";
    }

    private static async Task WaitForStopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
