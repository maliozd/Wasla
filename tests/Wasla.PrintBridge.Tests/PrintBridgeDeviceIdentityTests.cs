using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeDeviceIdentityTests
{
    [Fact]
    public void DeviceIdentityValidation_DoesNotAlterConnectionPrinterOrLanguageValues()
    {
        var connection = new WaslaOptions
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
        Assert.Equal("https://sushi-m.wasla.local:7200", connection.ServerUrl);
        Assert.Equal("token-value", connection.AgentToken);
        Assert.Equal("Kitchen printer", bridge.PrinterName);
        Assert.Equal("Mali Vivobook", bridge.BridgeName);
        Assert.Equal("Server Device", bridge.DisplayName);
        Assert.Equal("DESKTOP-CTFGHRE", bridge.MachineName);
        Assert.Equal("ru-RU", ui.Language);
    }
}
