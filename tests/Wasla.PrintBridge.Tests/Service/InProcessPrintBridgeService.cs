using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Tests.Updates;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// The service's engine and pipe inside the test process (WAS-61): the production composition
/// (<see cref="PrintBridgeAppServices"/>), the engine host, the real pipe server with the production client rule, on a
/// pipe of its own. Clients are the production <see cref="PrintBridgeServiceClient"/>, verified as the same user (the
/// Service Control Manager check is exercised by <c>PrintBridgeWindowsServiceTests</c>). This is not a Windows Service and
/// not another process; those boundaries are tested separately.
/// </summary>
internal sealed class InProcessPrintBridgeService : IAsyncDisposable
{
    private int _disposed;

    private InProcessPrintBridgeService(
        ServiceProvider services,
        PrintBridgeEngineHost engine,
        PrintBridgeServiceBackend backend,
        PrintBridgeIpcServer server,
        string pipeName)
    {
        Services = services;
        Engine = engine;
        Backend = backend;
        Server = server;
        PipeName = pipeName;
    }

    public ServiceProvider Services { get; }

    public PrintBridgeEngineHost Engine { get; }

    public PrintBridgeRuntime Runtime => Engine.Runtime;

    public PrintBridgeServiceBackend Backend { get; }

    public PrintBridgeIpcServer Server { get; }

    public string PipeName { get; }

    /// <param name="activate">False keeps the service as it is before it took the engine lock.</param>
    /// <param name="updates">The update coordinator (WAS-63); by default an unsigned installation's, as every test build is.</param>
    public static async Task<InProcessPrintBridgeService> StartAsync(
        Action<IServiceCollection>? configure = null,
        bool activate = true,
        IPrintBridgeIpcClientAuthorizer? authorizer = null,
        PrintBridgeUpdateCoordinator? updates = null)
    {
        var services = PrintBridgeAppServices.Build(configure);
        var engine = services.GetRequiredService<PrintBridgeEngineHost>();
        var backend = new PrintBridgeServiceBackend(updates ?? TestUpdates.Coordinator());
        var pipeName = "WaslaPbTest." + Guid.NewGuid().ToString("N");
        var server = new PrintBridgeIpcServer(
            pipeName,
            backend,
            authorizer ?? new WindowsTokenClientAuthorizer(),
            services.GetRequiredService<ILoggerFactory>(),
            "v-test");
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(30));
        var service = new InProcessPrintBridgeService(services, engine, backend, server, pipeName);
        if (activate)
            service.Activate();
        return service;
    }

    /// <summary>
    /// What the service does once it holds the engine lock: the engine serves clients, an update attempt left by a previous
    /// run is reconciled (WAS-63), and printing starts, or waits for an update's commit.
    /// </summary>
    public void Activate()
    {
        Backend.Activate(Engine);
        if (Backend.Updates.Reconcile(Engine, Services.GetRequiredService<WaslaPrintBridgeClient>(), Engine.Activate))
            Engine.Activate();
    }

    /// <summary>A started production client of this service. The caller disposes it.</summary>
    public PrintBridgeServiceClient CreateClient(ILogger? logger = null)
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(PipeName, new CurrentUserServerVerifier()),
            logger ?? NullLogger.Instance);
        client.Start();
        return client;
    }

    /// <summary>As the service stops: no engine for clients, the engine stops (WAS-56 grace), the pipe closes.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Backend.Updates.DisposeAsync();
        Backend.Deactivate();
        await Engine.ShutdownAsync();
        try
        {
            await Server.StopAsync();
        }
        catch (TimeoutException)
        {
        }

        await Services.DisposeAsync();
    }
}
