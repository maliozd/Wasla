using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeCoreBoundaryTests
{
    [Fact]
    public void Core_DoesNotDependOnAnyDesktopPresentationTechnology()
    {
        var references = typeof(PrintBridgeRuntime).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.Equal("Wasla.PrintBridge.Core", typeof(PrintBridgeRuntime).Assembly.GetName().Name);
        Assert.DoesNotContain(references, name => name.StartsWith("System.Windows.Forms", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("PresentationFramework", StringComparison.Ordinal));
        Assert.DoesNotContain("Wasla.PrintBridge", references);
    }

    [Fact]
    public void EngineTypes_LiveInCore_AndKeepTheirNamespaces()
    {
        var core = typeof(PrintBridgeRuntime).Assembly;

        foreach (var type in new[]
                 {
                     typeof(PrintBridgeRuntime),
                     typeof(WaslaPrintBridgeClient),
                     typeof(PrintBridgeSettingsStore),
                     typeof(PrintBridgeSettingsHolder),
                     typeof(PrintBridgeSettingsValidator),
                     typeof(LocalPrintJobHistoryStore),
                     typeof(Printing.WindowsReceiptPrinter),
                     typeof(Setup.PrintBridgeProtocolUri),
                     typeof(PrintBridgePaths)
                 })
        {
            Assert.Same(core, type.Assembly);
            Assert.StartsWith("Wasla.PrintBridge.", type.FullName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppVersion_IsTheDesktopProductVersion_NotTheCoreLibraryVersion()
    {
        var hostAssembly = typeof(UI.TrayApplicationContext).Assembly;

        var version = new AppVersionInfo(hostAssembly);

        Assert.Equal(hostAssembly.GetName().Version!.ToString(), version.HeaderValue);
        Assert.Equal("v1.0.0", version.Display);
    }
}
