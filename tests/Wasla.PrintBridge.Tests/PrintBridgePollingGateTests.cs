using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgePollingGateTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("token", true)]
    public void RequiresReconnectBeforeStart_ReturnsTrueWhenTokenMissing(string? token, bool canStartWithoutIssue)
    {
        Assert.Equal(!canStartWithoutIssue, PrintBridgePollingGate.RequiresReconnectBeforeStart(token, null));
    }

    [Fact]
    public void RequiresReconnectBeforeStart_ReturnsTrueForReconnectRequiredIssue()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ReconnectRequired);

        Assert.True(PrintBridgePollingGate.RequiresReconnectBeforeStart("valid-token", issue));
    }

    [Fact]
    public void RequiresReconnectBeforeStart_AllowsStartWhenTokenPresentAndNoReconnectIssue()
    {
        Assert.False(PrintBridgePollingGate.RequiresReconnectBeforeStart(
            "valid-token",
            new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)));
    }

    [Fact]
    public void ResetConnection_LocalizationKeysExistInAllCultures()
    {
        var root = FindRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.PrintBridge", "Resources");
        foreach (var culture in new[]
                 {
                     "PrintBridgeResources.resx",
                     "PrintBridgeResources.tr-TR.resx",
                     "PrintBridgeResources.en-US.resx",
                     "PrintBridgeResources.ar-SA.resx",
                     "PrintBridgeResources.ru-RU.resx"
                 })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("Settings.ConnectionTroubleshootingTitle", content);
            Assert.Contains("Settings.ResetConnectionHelp", content);
            Assert.Contains("Message.ReconnectFromWebToContinue", content);
            Assert.Contains("Button.ResetConnection", content);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "src", "Wasla.PrintBridge", "Resources")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
