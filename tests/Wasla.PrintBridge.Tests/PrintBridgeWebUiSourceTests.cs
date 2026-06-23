namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeWebUiSourceTests
{
    [Fact]
    public void ConnectedDevicesPage_DoesNotExposeDirectCreateDeviceTokenControls()
    {
        var root = LocateRepositoryRoot();
        var devicesView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "Devices.cshtml"));
        var devicesScript = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "wwwroot",
            "js",
            "print-bridge",
            "print-bridge-page.js"));

        Assert.DoesNotContain("printBridgeDeviceName", devicesView);
        Assert.DoesNotContain("printBridgeCreateDeviceBtn", devicesView);
        Assert.DoesNotContain("printBridgeCreateDeviceBtn", devicesScript);
        Assert.DoesNotContain("devices/create", devicesView);
        Assert.DoesNotContain("devices/create", devicesScript);
        Assert.Contains("PrintBridge.SetupNewDeviceAction", devicesView);
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
