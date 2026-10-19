using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeSettingsHolder
{
    private readonly object _sync = new();
    private WaslaOptions _connection = new();
    private PrintBridgeOptions _bridge = new();
    private UiOptions _ui = new();

    public WaslaOptions Connection
    {
        get { lock (_sync) return _connection; }
    }

    public PrintBridgeOptions Bridge
    {
        get { lock (_sync) return _bridge; }
    }

    public UiOptions Ui
    {
        get { lock (_sync) return _ui; }
    }

    public void Replace(WaslaOptions connection, PrintBridgeOptions bridge, UiOptions? ui = null)
    {
        lock (_sync)
        {
            _connection = connection;
            _bridge = bridge;
            if (ui is not null)
                _ui = ui;
        }
    }

    public void ReplaceUi(UiOptions ui)
    {
        lock (_sync)
            _ui = ui;
    }

    public (WaslaOptions Connection, PrintBridgeOptions Bridge, UiOptions Ui) Snapshot()
    {
        lock (_sync)
            return (_connection, _bridge, _ui);
    }
}
