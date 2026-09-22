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
    public void SetupPage_ShowsPrimaryFlowAndCollapsesTechnicalSections()
    {
        var root = LocateRepositoryRoot();
        var setupView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "Setup.cshtml"));
        var setupScript = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "wwwroot",
            "js",
            "print-bridge",
            "print-bridge-setup.js"));

        Assert.Contains("id=\"pbAutoOpenBtn\"", setupView);
        Assert.Contains("id=\"pbSetupModeNew\"", setupView);
        Assert.Contains("id=\"pbSetupModeReconnect\"", setupView);
        Assert.Contains("PrintBridge.Auto.RecommendedBadge", setupView);
        Assert.Contains("PrintBridge.Auto.NewDeviceModeTitle", setupView);
        Assert.DoesNotContain("justify-content-between gap-2 mb-2", setupView);
        Assert.Contains("id=\"pbManualSetupCollapse\"", setupView);
        Assert.Contains("accordion-collapse collapse", setupView);
        Assert.Contains("wasla-print-bridge-setup-below-fold", setupView);
        Assert.Contains("id=\"pbOpenManualSetupLink\"", setupView);
        Assert.Contains("PrintBridge.Auto.OpenManualSetup", setupView);
        Assert.Contains("id=\"pbReconnectDeviceGuidance\"", setupView);
        Assert.Contains("PrintBridge.Auto.SelectReconnectDevice", setupView);
        Assert.Contains("PrintBridge.Auto.ReconnectConnectionNotice", setupView);
        Assert.Contains("id=\"pbSetupServerUrl\"", setupView);
        Assert.Contains("id=\"pbSetupCopyServerUrlBtn\"", setupView);
        Assert.Contains("id=\"pbManualSetupCodeActionBtn\"", setupView);
        Assert.Contains("PrintBridge.Auto.OpenButton", setupView);
        Assert.Contains("PrintBridge.Auto.ManualSectionTitle", setupView);
        Assert.Contains("PrintBridge.Troubleshooting", setupView);
        Assert.DoesNotContain("PrintBridge.DeviceTokenLabel", setupView);
        Assert.DoesNotContain("printBridgeTokenValue", setupView);
        Assert.DoesNotContain("alert alert-warning", setupView);
        Assert.Contains("updatePrimaryCtaState", setupScript);
        Assert.Contains("updateReconnectDeviceDetails", setupScript);
        Assert.Contains("getReconnectDisplayName", setupScript);
        Assert.Contains("shouldShowReconnectMachineName", setupScript);
        Assert.Contains("updatePrimaryCtaLabel", setupScript);
        Assert.Contains("formatReconnectLastSeen", setupScript);
        Assert.Contains("expandManualSetupSection", setupScript);
        Assert.Contains("bindManualSectionLinks", setupScript);
    }

    [Fact]
    public void SetupPage_ReconnectDevicePickerUsesShortOptionsAndDetailRow()
    {
        var root = LocateRepositoryRoot();
        var setupView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "Setup.cshtml"));
        var setupScript = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "wwwroot",
            "js",
            "print-bridge",
            "print-bridge-setup.js"));

        Assert.Contains("id=\"pbReconnectDeviceDetails\"", setupView);
        Assert.Contains("id=\"pbReconnectDeviceTitle\"", setupView);
        Assert.Contains("id=\"pbReconnectDeviceMachine\"", setupView);
        Assert.Contains("id=\"pbReconnectDeviceBadges\"", setupView);
        Assert.Contains("id=\"pbReconnectDeviceLastSeen\"", setupView);
        Assert.Contains("id=\"pbAutoOpenBtnText\"", setupView);
        Assert.Contains("ReconnectDeviceDisplayName(device)", setupView);
        Assert.Contains("machineName = d.MachineName", setupView);
        Assert.DoesNotContain("<option value=\"@device.Id\">@device.Name</option>", setupView);

        var reconnectSelectStart = setupView.IndexOf("id=\"pbReconnectDeviceSelect\"", StringComparison.Ordinal);
        var reconnectDetailsStart = setupView.IndexOf("id=\"pbReconnectDeviceDetails\"", reconnectSelectStart, StringComparison.Ordinal);
        Assert.True(reconnectSelectStart >= 0);
        Assert.True(reconnectDetailsStart > reconnectSelectStart);
        var reconnectSelectSection = setupView[reconnectSelectStart..reconnectDetailsStart];
        Assert.DoesNotContain("LastSeenAtUtc", reconnectSelectSection);
        Assert.DoesNotContain(" UTC", reconnectSelectSection);
        Assert.DoesNotContain("ConnectionStatusLabelKey", reconnectSelectSection);

        Assert.Contains("reconnectOpenButton", setupScript);
        Assert.Contains("reconnectMachineLabel", setupScript);
        Assert.Contains("messages.reconnectOpenButton", setupScript);
        Assert.Contains("device.machineName", setupScript);
        Assert.Contains("openBtn.disabled = disabled", setupScript);
    }

    [Fact]
    public void SetupPage_ReconnectDeviceDisplayNameFallsBackToMachineName()
    {
        var root = LocateRepositoryRoot();
        var setupScript = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "wwwroot",
            "js",
            "print-bridge",
            "print-bridge-setup.js"));

        Assert.Contains("function getReconnectDisplayName(device)", setupScript);
        Assert.Contains("normalizeReconnectText(device.name)", setupScript);
        Assert.Contains("normalizeReconnectText(device.machineName)", setupScript);
        Assert.Contains("function shouldShowReconnectMachineName(device)", setupScript);
    }

    [Fact]
    public void SetupPage_ReconnectPickerLocalizationExistsInAllCultures()
    {
        var root = LocateRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.Web", "Resources");
        foreach (var culture in new[] { "SharedResource.resx", "SharedResource.tr-TR.resx", "SharedResource.en-US.resx", "SharedResource.ar-SA.resx", "SharedResource.ru-RU.resx" })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("PrintBridge.Auto.ReconnectOpenButton", content);
            Assert.Contains("PrintBridge.Auto.ReconnectMachineLabel", content);
            Assert.Contains("PrintBridge.Auto.ReconnectConnectionNotice", content);
            Assert.Contains("PrintBridge.Auto.InstalledHint", content);
            Assert.Contains("PrintBridge.Auto.SelectReconnectDevice", content);
        }
    }

    [Fact]
    public void SetupPage_PackagePlaceholderRendersWithoutActiveDownloadLink()
    {
        var root = LocateRepositoryRoot();
        var setupView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "Setup.cshtml"));

        Assert.Contains("id=\"pbPackagePlaceholderCard\"", setupView);
        Assert.Contains("wasla-print-bridge-package-placeholder-row", setupView);
        Assert.Contains("PrintBridge.PackageCardTitle", setupView);
        Assert.Contains("PrintBridge.PackageComingSoon", setupView);
        Assert.Contains("PrintBridge.PackagePlaceholderFileName", setupView);
        Assert.Contains("PrintBridge.PackageNotReadyStatus", setupView);
        Assert.Contains("id=\"pbPackageDownloadUnavailableBtn\"", setupView);
        Assert.Contains("disabled", setupView);

        var placeholderStart = setupView.IndexOf("id=\"pbPackagePlaceholderCard\"", StringComparison.Ordinal);
        var manualSetupEnd = setupView.IndexOf("wasla-print-bridge-connection-card", placeholderStart, StringComparison.Ordinal);
        Assert.True(placeholderStart >= 0);
        Assert.True(manualSetupEnd > placeholderStart);
        var placeholderSection = setupView[placeholderStart..manualSetupEnd];
        Assert.DoesNotContain("PackageDownloadUrl", placeholderSection);
        Assert.DoesNotContain("<a ", placeholderSection);
    }

    [Fact]
    public void SetupPage_PackagePlaceholderLocalizationExistsInAllCultures()
    {
        var root = LocateRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.Web", "Resources");
        foreach (var culture in new[] { "SharedResource.resx", "SharedResource.tr-TR.resx", "SharedResource.en-US.resx", "SharedResource.ar-SA.resx", "SharedResource.ru-RU.resx" })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("PrintBridge.PackageCardTitle", content);
            Assert.Contains("PrintBridge.PackageComingSoon", content);
            Assert.Contains("PrintBridge.PackagePlaceholderFileName", content);
            Assert.Contains("PrintBridge.PackageNotReadyStatus", content);
            Assert.Contains("PrintBridge.PackageDownloadUnavailable", content);
        }
    }

    [Fact]
    public void SetupPage_LocalizationIncludesFirstScreenReconnectKeys()
    {
        var root = LocateRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.Web", "Resources");
        foreach (var culture in new[] { "SharedResource.resx", "SharedResource.tr-TR.resx", "SharedResource.en-US.resx", "SharedResource.ar-SA.resx", "SharedResource.ru-RU.resx" })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("PrintBridge.Auto.OpenManualSetup", content);
            Assert.Contains("PrintBridge.Auto.ReconnectOpenButton", content);
            Assert.Contains("PrintBridge.Auto.ReconnectMachineLabel", content);
            Assert.Contains("PrintBridge.Auto.ReconnectConnectionNotice", content);
            Assert.Contains("PrintBridge.Auto.SelectReconnectDevice", content);
            Assert.Contains("PrintBridge.Auto.ReconnectConfirm", content);
        }
    }

    [Fact]
    public void SetupPage_UsesSixManualStepsAndDoesNotRenderStepSeven()
    {
        var root = LocateRepositoryRoot();
        var setupView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "Setup.cshtml"));

        Assert.Contains("PrintBridge.SetupStep1", setupView);
        Assert.Contains("PrintBridge.SetupStep6", setupView);
        Assert.DoesNotContain("PrintBridge.SetupStep7", setupView);
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
        Assert.Contains("printBridgeDetailSetActiveBtn", detailsView);
        Assert.Contains("PrintBridge.DeviceRemoveDangerTitle", detailsView);
        Assert.Contains("asp-action=\"RemoveDevice\"", detailsView);
        Assert.Contains("printBridgeRemoveDeviceModal", detailsView);
    }

    [Fact]
    public void DeviceDetailsPage_UsesJsonSerializedEndpointsAndAntiForgeryPostActions()
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
        var controllerSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Controllers",
            "PrintBridgeController.cs"));

        Assert.Contains("deviceDetailsConfigJson = JsonSerializer.Serialize", detailsView);
        Assert.Contains("regenerate-token", detailsView);
        Assert.Contains("set-active", detailsView);
        Assert.Contains("__RequestVerificationToken", detailsView);
        Assert.Contains("function post(url, fields)", detailsView);
        Assert.DoesNotContain("regenerate: \"@Url.Action", detailsView);

        Assert.Contains("[ValidateAntiForgeryToken]", controllerSource);
        Assert.Contains("[HttpPost(\"devices/{deviceId:guid}/regenerate-token\")]", controllerSource);
        Assert.Contains("[HttpPost(\"devices/{deviceId:guid}/set-active\")]", controllerSource);
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
