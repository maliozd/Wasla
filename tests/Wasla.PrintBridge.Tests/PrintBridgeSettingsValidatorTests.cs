using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeSettingsValidatorTests
{
    [Fact]
    public void ConnectionSettings_ValidUrlAndToken_SucceedWithoutPrinter()
    {
        var connection = new WaslaOptions
        {
            ServerUrl = " https://sushi-m.wasla.local:7200/ ",
            AgentToken = " token-value "
        };

        var ok = PrintBridgeSettingsValidator.TryValidateConnectionSettings(connection, out var errorKey);

        Assert.True(ok);
        Assert.Null(errorKey);
        Assert.Equal("https://sushi-m.wasla.local:7200", connection.ServerUrl);
        Assert.Equal("token-value", connection.AgentToken);
    }

    [Fact]
    public void ConnectionSettings_EmptyToken_FailsOnlyConnectionValidation()
    {
        var connection = new WaslaOptions { ServerUrl = "https://localhost:7200", AgentToken = " " };
        var bridge = new PrintBridgeOptions { PrinterName = "Kitchen printer" };

        var connectionOk = PrintBridgeSettingsValidator.TryValidateConnectionSettings(connection, out var connectionError);
        var printerOk = PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out var printerError);

        Assert.False(connectionOk);
        Assert.Equal("Validation.AgentTokenRequired", connectionError);
        Assert.True(printerOk);
        Assert.Null(printerError);
    }

    [Fact]
    public void PrinterSettings_SaveSucceedsWithoutConnectionValues()
    {
        var bridge = new PrintBridgeOptions { PrinterName = "Kitchen printer" };

        var ok = PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out var errorKey);

        Assert.True(ok);
        Assert.Null(errorKey);
        Assert.Equal("Kitchen printer", bridge.PrinterName);
    }

    [Fact]
    public void PrinterSettings_MissingPrinter_FailsPrinterValidationOnly()
    {
        var connection = new WaslaOptions { ServerUrl = "https://localhost:7200", AgentToken = "token" };
        var bridge = new PrintBridgeOptions { PrinterName = " " };

        var connectionOk = PrintBridgeSettingsValidator.TryValidateConnectionSettings(connection, out var connectionError);
        var printerOk = PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out var printerError);

        Assert.True(connectionOk);
        Assert.Null(connectionError);
        Assert.False(printerOk);
        Assert.Equal("Validation.PrinterRequired", printerError);
    }

    [Fact]
    public void DeviceIdentity_TrimsAndLimitsLocalAliasWithoutConnectionOrPrinter()
    {
        var bridge = new PrintBridgeOptions
        {
            BridgeName = $"  {new string('A', PrintBridgeSettingsValidator.MaxDeviceDisplayNameLength + 20)}  ",
            MachineName = "  POS-01  "
        };

        var ok = PrintBridgeSettingsValidator.TryValidateDeviceIdentitySettings(bridge, out var errorKey);

        Assert.True(ok);
        Assert.Null(errorKey);
        Assert.Equal("POS-01", bridge.MachineName);
        Assert.Equal(PrintBridgeSettingsValidator.MaxDeviceDisplayNameLength, bridge.BridgeName.Length);
    }

    [Fact]
    public void PrintingReadiness_ReportsConnectionBeforePrinterWhenBothMissing()
    {
        var connection = new WaslaOptions { ServerUrl = " ", AgentToken = " " };
        var bridge = new PrintBridgeOptions { PrinterName = " " };

        var ok = PrintBridgeSettingsValidator.TryValidatePrintingReadiness(connection, bridge, out var errorKey);

        Assert.False(ok);
        Assert.Equal("Validation.ServerUrlRequired", errorKey);
    }

    [Fact]
    public void PrintingReadiness_ReportsPrinterWhenConnectionIsConfigured()
    {
        var connection = new WaslaOptions { ServerUrl = "https://localhost:7200", AgentToken = "token" };
        var bridge = new PrintBridgeOptions { PrinterName = " " };

        var ok = PrintBridgeSettingsValidator.TryValidatePrintingReadiness(connection, bridge, out var errorKey);

        Assert.False(ok);
        Assert.Equal("Validation.PrinterRequired", errorKey);
    }
}
