using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeConnectionResetTests
{
    [Fact]
    public void ApplyToSettings_ClearsPersistedTokenAndDisplayName()
    {
        var hub = new WaslaOptions { ServerUrl = "https://sushim.wasla.local", AgentToken = "secret-token" };
        var bridge = new PrintBridgeOptions
        {
            InstallationId = "11111111-1111-1111-1111-111111111111",
            DisplayName = "Kitchen POS",
            ServerDeviceNameResolved = true,
            MachineName = "DESKTOP-CTFGHRE",
            PrinterName = "POS-58"
        };

        Assert.True(PrintBridgeConnectionReset.ApplyToSettings(hub, bridge));
        Assert.Equal(string.Empty, hub.AgentToken);
        Assert.Equal(string.Empty, bridge.DisplayName);
        Assert.False(bridge.ServerDeviceNameResolved);
    }

    [Fact]
    public void ApplyToSettings_PreservesServerUrlInstallationIdLanguageAndMachineName()
    {
        var hub = new WaslaOptions { ServerUrl = "https://sushim.wasla.local", AgentToken = "secret-token" };
        var bridge = new PrintBridgeOptions
        {
            InstallationId = "22222222-2222-2222-2222-222222222222",
            MachineName = "DESKTOP-CTFGHRE",
            PrinterName = "POS-58",
            IdlePollIntervalSeconds = 7
        };
        var ui = new UiOptions { Language = "tr-TR" };

        PrintBridgeConnectionReset.ApplyToSettings(hub, bridge);

        Assert.Equal("https://sushim.wasla.local", hub.ServerUrl);
        Assert.Equal("22222222-2222-2222-2222-222222222222", bridge.InstallationId);
        Assert.Equal("DESKTOP-CTFGHRE", bridge.MachineName);
        Assert.Equal("POS-58", bridge.PrinterName);
        Assert.Equal(7, bridge.IdlePollIntervalSeconds);
        Assert.Equal("tr-TR", ui.Language);
    }

    [Fact]
    public void ApplyToSettings_FallsBackToMachineNameWhenDisplayNameWasServerResolved()
    {
        var hub = new WaslaOptions { ServerUrl = "https://sushim.wasla.local", AgentToken = "token" };
        var bridge = new PrintBridgeOptions
        {
            DisplayName = "POS-58",
            ServerDeviceNameResolved = true,
            MachineName = "DESKTOP-CTFGHRE"
        };

        PrintBridgeConnectionReset.ApplyToSettings(hub, bridge);

        Assert.Equal(string.Empty, bridge.DisplayName);
        Assert.Equal("DESKTOP-CTFGHRE", bridge.MachineName);
    }

    [Fact]
    public void CreateReconnectRequiredIssue_UsesManualResetDetailAndStopsPolling()
    {
        var issue = PrintBridgeConnectionReset.CreateReconnectRequiredIssue();

        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, issue.Code);
        Assert.Equal("RuntimeIssue.ManualReset.Detail", issue.EffectiveDetailResourceKey);
        Assert.True(issue.ShouldClearToken);
        Assert.True(issue.ShouldStopPolling);
        Assert.True(issue.IsBlockingLifecycleIssue);
    }

    [Fact]
    public void TokenFieldSync_AllowsEmptyTokenAfterResetWithoutTreatingAsUserEdit()
    {
        Assert.False(PrintBridgeConnectionFieldSync.ShouldPreserveAgentTokenInput(
            isSavingConnection: false,
            isTokenFieldFocused: false,
            userEditedAgentToken: false));
    }
}
