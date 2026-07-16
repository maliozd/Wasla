using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeTokenPasteGuardTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("plain-device-token-value", false)]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_=", false)]
    [InlineData("wasla-printbridge://setup?server=https://sushim.wasla.local&code=abc", true)]
    [InlineData("WASLA-PRINTBRIDGE://setup?server=https://x&code=y", true)]
    [InlineData("server=https://sushim.wasla.local&code=ABCDEFGHIJKLMNOP", true)]
    public void LooksLikeSetupOrProtocolValue_DetectsProtocolAndQueryPastes(string? value, bool expected)
    {
        Assert.Equal(expected, PrintBridgeTokenPasteGuard.LooksLikeSetupOrProtocolValue(value));
    }

    [Fact]
    public void PasteGuardResource_ExistsInAllCultures()
    {
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();
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
            Assert.Contains("Settings.AgentTokenPasteGuard", content);
        }
    }
}
