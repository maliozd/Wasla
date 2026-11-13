using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// What the pipe serves (WAS-61): the service's availability and, once it took the engine lock, the one engine. Clients
/// can connect before the engine exists, so the app can show why the service is not printing.
/// </summary>
internal sealed class PrintBridgeServiceBackend
{
    private readonly object _sync = new();
    private IpcAvailability _availability = IpcAvailability.Starting;
    private IPrintBridgeEngine? _engine;

    /// <summary>Raised from any thread when the availability or the engine's state may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the engine finished a pending move in the background.</summary>
    public event EventHandler<PrintBridgeAutoSetupOutcome>? BackgroundSetupCompleted;

    public IpcAvailability Availability
    {
        get { lock (_sync) return _availability; }
    }

    /// <summary>The engine while <see cref="Availability"/> is <see cref="IpcAvailability.Active"/>; otherwise null.</summary>
    public IPrintBridgeEngine? Engine
    {
        get { lock (_sync) return _availability == IpcAvailability.Active ? _engine : null; }
    }

    public void SetUnavailable(IpcAvailability availability)
    {
        if (availability == IpcAvailability.Active)
            throw new ArgumentOutOfRangeException(nameof(availability));

        lock (_sync)
        {
            if (_availability == availability)
                return;
            _availability = availability;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Activate(IPrintBridgeEngine engine)
    {
        lock (_sync)
        {
            if (_engine is not null)
                throw new InvalidOperationException("The engine is already active.");
            _engine = engine;
            _availability = IpcAvailability.Active;
        }

        engine.StatusChanged += OnEngineStatusChanged;
        engine.BackgroundSetupCompleted += OnBackgroundSetupCompleted;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The service is stopping: clients get no engine from now on.</summary>
    public void Deactivate()
    {
        IPrintBridgeEngine? engine;
        lock (_sync)
        {
            engine = _engine;
            _engine = null;
            _availability = IpcAvailability.Starting;
        }

        if (engine is not null)
        {
            engine.StatusChanged -= OnEngineStatusChanged;
            engine.BackgroundSetupCompleted -= OnBackgroundSetupCompleted;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IpcState Snapshot()
    {
        IpcAvailability availability;
        IPrintBridgeEngine? engine;
        lock (_sync)
        {
            availability = _availability;
            engine = _availability == IpcAvailability.Active ? _engine : null;
        }

        return engine is null
            ? new IpcState(availability, null, null)
            : new IpcState(availability, PrintBridgeIpcMapping.ToWire(engine.GetStatus()), PrintBridgeIpcMapping.ToWire(engine.Settings));
    }

    /// <summary>The issue a client shows while the service cannot run its engine.</summary>
    public static PrintBridgeRuntimeIssueCode IssueFor(IpcAvailability availability) => availability switch
    {
        IpcAvailability.EngineHeldElsewhere => PrintBridgeRuntimeIssueCode.EngineHeldElsewhere,
        IpcAvailability.DataFolderNotSecured => PrintBridgeRuntimeIssueCode.DataFolderNotSecured,
        _ => PrintBridgeRuntimeIssueCode.ServiceUnavailable
    };

    private void OnEngineStatusChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnBackgroundSetupCompleted(object? sender, PrintBridgeAutoSetupOutcome outcome) =>
        BackgroundSetupCompleted?.Invoke(this, outcome);
}
