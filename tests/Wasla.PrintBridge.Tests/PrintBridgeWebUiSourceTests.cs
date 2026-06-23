using System.Runtime.CompilerServices;

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

    [Fact]
    public void ConnectedDevicesPage_HasOnlyCompactToggleAndDetailsRowActions()
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

        Assert.Contains("pb-device-active-toggle", devicesScript);
        Assert.Contains("messages.details", devicesScript);
        Assert.DoesNotContain("pb-regenerate-token-btn", devicesScript);
        Assert.DoesNotContain("regenerateTokenUrlTemplate", devicesView);
        Assert.DoesNotContain("printBridgeTokenBox", devicesView);
        Assert.DoesNotContain("printBridgeTokenValue", devicesView);
    }

    [Fact]
    public void DeviceDetailsPage_ContainsTokenRegenerationAndDangerZone()
    {
        var root = LocateRepositoryRoot();
        var detailsView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "DeviceDetails.cshtml"));

        Assert.Contains("printBridgeDetailRegenerateTokenBtn", detailsView);
        Assert.Contains("PrintBridge.DeviceRemoveDangerTitle", detailsView);
        Assert.Contains("asp-action=\"RemoveDevice\"", detailsView);
        Assert.Contains("printBridgeRemoveDeviceModal", detailsView);
    }

    private static string LocateRepositoryRoot([CallerFilePath] string? sourceFile = null)
    {
        foreach (var start in new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetDirectoryName(sourceFile)
        })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
