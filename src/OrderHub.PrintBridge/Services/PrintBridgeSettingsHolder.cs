using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeSettingsHolder
{
    private readonly object _sync = new();
    private OrderHubOptions _orderHub = new();
    private PrintBridgeOptions _bridge = new();
    private UiOptions _ui = new();

    public OrderHubOptions OrderHub
    {
        get { lock (_sync) return _orderHub; }
    }

    public PrintBridgeOptions Bridge
    {
        get { lock (_sync) return _bridge; }
    }

    public UiOptions Ui
    {
        get { lock (_sync) return _ui; }
    }

    public void Replace(OrderHubOptions orderHub, PrintBridgeOptions bridge, UiOptions? ui = null)
    {
        lock (_sync)
        {
            _orderHub = orderHub;
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

    public (OrderHubOptions OrderHub, PrintBridgeOptions Bridge, UiOptions Ui) Snapshot()
    {
        lock (_sync)
            return (_orderHub, _bridge, _ui);
    }
}
