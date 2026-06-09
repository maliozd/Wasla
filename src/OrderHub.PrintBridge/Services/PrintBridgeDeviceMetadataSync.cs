using Microsoft.Extensions.Logging;
using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeDeviceMetadataSync
{
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly ILogger<PrintBridgeDeviceMetadataSync> _logger;
    private readonly object _sync = new();

    public PrintBridgeDeviceMetadataSync(
        PrintBridgeSettingsStore store,
        PrintBridgeSettingsHolder holder,
        ILogger<PrintBridgeDeviceMetadataSync> logger)
    {
        _store = store;
        _holder = holder;
        _logger = logger;
    }

    public bool ApplyFromHealth(OrderHubPrintBridgeClient.PrintBridgeHealthResult health)
    {
        _logger.LogInformation(
            "Device metadata received. CustomerName={CustomerName}, DeviceName={DeviceName}, ServerTimeUtc={ServerTimeUtc}",
            string.IsNullOrWhiteSpace(health.CustomerName) ? "(empty)" : health.CustomerName,
            string.IsNullOrWhiteSpace(health.DeviceName) ? "(empty)" : health.DeviceName,
            health.ServerTimeUtc);

        var deviceName = health.DeviceName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        lock (_sync)
        {
            var (hub, bridge, ui) = _holder.Snapshot();
            var changed = !string.Equals(bridge.DisplayName, deviceName, StringComparison.Ordinal)
                || !bridge.ServerDeviceNameResolved;

            bridge.DisplayName = deviceName;
            bridge.ServerDeviceNameResolved = true;
            _holder.Replace(hub, bridge, ui);

            if (changed)
            {
                Persist(hub, bridge, ui);
                _logger.LogInformation("Device display name updated: {DisplayName}", deviceName);
            }

            return changed;
        }
    }

    public void MarkUnresolved()
    {
        lock (_sync)
        {
            var (hub, bridge, ui) = _holder.Snapshot();
            if (!bridge.ServerDeviceNameResolved)
                return;

            bridge.ServerDeviceNameResolved = false;
            _holder.Replace(hub, bridge, ui);
            Persist(hub, bridge, ui);
            _logger.LogInformation("Device display name marked unresolved after authentication failure.");
        }
    }

    public void ClearOnTokenChange()
    {
        lock (_sync)
        {
            var (hub, bridge, ui) = _holder.Snapshot();
            if (string.IsNullOrWhiteSpace(bridge.DisplayName) && !bridge.ServerDeviceNameResolved)
                return;

            bridge.DisplayName = string.Empty;
            bridge.ServerDeviceNameResolved = false;
            _holder.Replace(hub, bridge, ui);
            Persist(hub, bridge, ui);
            _logger.LogInformation("Device display name cleared after token change.");
        }
    }

    private void Persist(OrderHubOptions hub, PrintBridgeOptions bridge, UiOptions ui)
    {
        try
        {
            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = hub,
                PrintBridge = bridge,
                Ui = ui
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist device display name to settings.");
        }
    }
}
