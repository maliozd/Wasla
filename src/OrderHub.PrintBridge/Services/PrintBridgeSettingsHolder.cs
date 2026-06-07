using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeSettingsHolder
{
    private readonly object _sync = new();
    private OrderHubOptions _orderHub = new();
    private PrintBridgeOptions _bridge = new();

    public OrderHubOptions OrderHub
    {
        get { lock (_sync) return _orderHub; }
    }

    public PrintBridgeOptions Bridge
    {
        get { lock (_sync) return _bridge; }
    }

    public void Replace(OrderHubOptions orderHub, PrintBridgeOptions bridge)
    {
        lock (_sync)
        {
            _orderHub = orderHub;
            _bridge = bridge;
        }
    }

    public (OrderHubOptions OrderHub, PrintBridgeOptions Bridge) Snapshot()
    {
        lock (_sync)
            return (_orderHub, _bridge);
    }
}
