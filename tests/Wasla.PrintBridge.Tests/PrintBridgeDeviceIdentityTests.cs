using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeDeviceIdentityTests
{
    [Fact]
    public void LocalDeviceLabel_PrefersLocalDeviceName()
    {
        var label = PrintBridgeRuntimeStatus.ResolveLocalDeviceLabel("  Mali Vivobook  ", "DESKTOP-CTFGHRE");

        Assert.Equal("Mali Vivobook", label);
    }

    [Fact]
    public void LocalDeviceLabel_FallsBackToMachineName()
    {
        var label = PrintBridgeRuntimeStatus.ResolveLocalDeviceLabel(" ", "DESKTOP-CTFGHRE");

        Assert.Equal("DESKTOP-CTFGHRE", label);
    }

    [Fact]
    public void DeviceIdentityValidation_DoesNotAlterConnectionPrinterOrLanguageValues()
    {
        var hub = new WaslaOptions
        {
            ServerUrl = "https://sushi-m.wasla.local:7200",
            AgentToken = "token-value"
        };
        var bridge = new PrintBridgeOptions
        {
            PrinterName = "Kitchen printer",
            BridgeName = "  Mali Vivobook  ",
            DisplayName = "Server Device",
            MachineName = " DESKTOP-CTFGHRE "
        };
        var ui = new UiOptions { Language = "ru-RU" };

        var ok = PrintBridgeSettingsValidator.TryValidateDeviceIdentitySettings(bridge, out var errorKey);

        Assert.True(ok);
        Assert.Null(errorKey);
        Assert.Equal("https://sushi-m.wasla.local:7200", hub.ServerUrl);
        Assert.Equal("token-value", hub.AgentToken);
        Assert.Equal("Kitchen printer", bridge.PrinterName);
        Assert.Equal("Mali Vivobook", bridge.BridgeName);
        Assert.Equal("Server Device", bridge.DisplayName);
        Assert.Equal("DESKTOP-CTFGHRE", bridge.MachineName);
        Assert.Equal("ru-RU", ui.Language);
    }
}
