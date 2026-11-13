using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.Installer;

/// <summary>
/// WAS-62 source contract: values the installer and the app must agree on, read from the WiX sources. What the built
/// installer does on a real computer is checked by <see cref="PrintBridgeInstallerTests"/> on GitHub-hosted runners.
/// </summary>
public sealed class PrintBridgeInstallerSourceTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly string Root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();

    [Fact]
    public void TheApp_FindsTheSetupByTheSetupsOwnUpgradeCode()
    {
        var bundle = Load("installer", "print-bridge", "Bundle", "Bundle.wxs").Descendants(Wix + "Bundle").Single();

        Assert.Equal(PrintBridgeInstalledSetup.SetupUpgradeCode.Trim('{', '}'), (string?)bundle.Attribute("UpgradeCode"), ignoreCase: true);
    }

    [Fact]
    public void TheSetupInstallsTheWebView2Runtime_BelowTheVersionTheAppNeeds()
    {
        var project = XDocument.Load(PathOf("installer", "print-bridge", "Bundle", "Wasla.PrintBridge.Bundle.wixproj"));
        Assert.Equal(WebView2RuntimeProbe.MinimumRuntimeVersion, project.Descendants("WebView2MinimumVersion").Single().Value);

        var package = Load("installer", "print-bridge", "Bundle", "Bundle.wxs").Descendants(Wix + "ExePackage").Single();
        Assert.Equal("WebView2RuntimeVersion AND WebView2RuntimeVersion >= v$(var.WebView2MinimumVersion)", (string?)package.Attribute("DetectCondition"));
        Assert.Equal("/silent /install", (string?)package.Attribute("InstallArguments"));
        Assert.Equal("yes", (string?)package.Attribute("Permanent"));
        Assert.Equal("no", (string?)package.Attribute("Vital"));
    }

    [Fact]
    public void TheServiceIsRegisteredAsWas61Documents_AndItsDataFolderSecuredBeforeItStarts()
    {
        var package = Load("installer", "print-bridge", "Package", "Package.wxs");
        var service = package.Descendants(Wix + "ServiceInstall").Single();
        Assert.Equal(PrintBridgePaths.ServiceName, (string?)service.Attribute("Name"));
        Assert.Equal($@"NT SERVICE\{PrintBridgePaths.ServiceName}", (string?)service.Attribute("Account"));
        Assert.Equal("auto", (string?)service.Attribute("Start"));
        Assert.Equal("Spooler", (string?)service.Element(Wix + "ServiceDependency")?.Attribute("Id"));
        Assert.Null(service.Attribute("Arguments"));

        // The same sc.exe settings as the WAS-61 handoff commands.
        var handoff = File.ReadAllText(PathOf("docs", "integrations", "print-bridge-service.md"));
        var commands = package.Descendants(Wix + "SetProperty").ToDictionary(p => (string)p.Attribute("Id")!, p => (string)p.Attribute("Value")!);
        foreach (var (action, arguments) in new[]
                 {
                     ("ConfigureServicePrivileges", "privs WaslaPrintBridge SeChangeNotifyPrivilege"),
                     ("ConfigureServiceRecovery", "failure WaslaPrintBridge reset= 86400 actions= restart/5000/restart/10000/restart/60000"),
                     ("ConfigureServiceRecoveryOnError", "failureflag WaslaPrintBridge 1")
                 })
        {
            Assert.Equal($"\"[System64Folder]sc.exe\" {arguments}", commands[action]);
            Assert.Contains($"sc.exe {arguments}", handoff, StringComparison.Ordinal);
        }

        Assert.Equal("\"[#ServiceExe]\" secure-data", commands["SecureDataFolder"]);

        // Recovery settings, then secure-data, between creating and starting the service; never while uninstalling.
        var sequence = package.Descendants(Wix + "InstallExecuteSequence").Single().Elements(Wix + "Custom")
            .ToDictionary(c => (string)c.Attribute("Action")!, c => c);
        Assert.Equal("InstallServices", (string?)sequence["ConfigureServicePrivileges"].Attribute("After"));
        Assert.Equal("ConfigureServiceRecoveryOnError", (string?)sequence["SecureDataFolder"].Attribute("After"));
        foreach (var action in new[] { "ConfigureServicePrivileges", "ConfigureServiceRecovery", "ConfigureServiceRecoveryOnError", "SecureDataFolder" })
            Assert.Equal("NOT REMOVE~=\"ALL\"", (string?)sequence[action].Attribute("Condition"));
        Assert.Equal("StopServices", (string?)sequence["CloseApps"].Attribute("Before"));
        Assert.Equal("afterInstallInitialize", (string?)package.Descendants(Wix + "MajorUpgrade").Single().Attribute("Schedule"));
    }

    [Fact]
    public void TheAppStartsAtSignIn_Quietly_AndTheSetupOwnsNoDesktopShortcutOrDataFolder()
    {
        var text = File.ReadAllText(PathOf("installer", "print-bridge", "Package", "Package.wxs"));
        var package = XDocument.Parse(text);

        var run = package.Descendants(Wix + "RegistryValue").Single(v => (string?)v.Attribute("Key") == @"Software\Microsoft\Windows\CurrentVersion\Run");
        Assert.Equal("HKLM", (string?)run.Attribute("Root"));
        Assert.Equal($"\"[INSTALLFOLDER]Wasla.PrintBridge.exe\" {PrintBridgeSignInStart.Argument}", (string?)run.Attribute("Value"));

        var handler = package.Descendants(Wix + "RegistryKey").Single(k => (string?)k.Attribute("Key") == @"Software\Classes\wasla-printbridge");
        Assert.Equal("HKLM", (string?)handler.Attribute("Root"));
        Assert.Equal("yes", (string?)handler.Attribute("ForceDeleteOnUninstall"));
        Assert.Contains(handler.Descendants(Wix + "RegistryValue"), v => (string?)v.Attribute("Value") == "\"[INSTALLFOLDER]Wasla.PrintBridge.exe\" \"%1\"");

        var shortcut = package.Descendants(Wix + "Shortcut").Single();
        Assert.Equal("ProgramMenuFolder", (string?)shortcut.Attribute("Directory"));
        foreach (var notOwned in new[] { "DesktopFolder", "CommonAppDataFolder", "ProgramData", "AgentToken", "Token" })
            Assert.DoesNotMatch(new Regex($@"\b{notOwned}\b"), Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline));
    }

    [Fact]
    public void OneVersion_OneWixRelease_AndTheAcceptedMaintenanceFeeEula()
    {
        foreach (var project in new[] { @"src\Wasla.PrintBridge\Wasla.PrintBridge.csproj", @"src\Wasla.PrintBridge.Service\Wasla.PrintBridge.Service.csproj" })
        {
            var csproj = File.ReadAllText(PathOf(project.Split('\\')));
            Assert.Contains(@"<Import Project=""..\Wasla.PrintBridge.Version.props"" />", csproj, StringComparison.Ordinal);
            Assert.DoesNotContain("<Version>", csproj, StringComparison.Ordinal);
        }

        var props = XDocument.Load(PathOf("installer", "print-bridge", "Directory.Build.props"));
        Assert.Equal("wix7", props.Descendants("AcceptEula").Single().Value);
        Assert.Contains(props.Descendants("Import"), i => (string?)i.Attribute("Project") == @"..\..\src\Wasla.PrintBridge.Version.props");

        var versions = new[] { "Package/Wasla.PrintBridge.Package.wixproj", "Bundle/Wasla.PrintBridge.Bundle.wixproj" }
            .Select(p => XDocument.Load(PathOf(["installer", "print-bridge", .. p.Split('/')])))
            .SelectMany(p => new[] { ((string)p.Root!.Attribute("Sdk")!).Split('/')[1] }
                .Concat(p.Descendants("PackageReference").Select(r => (string)r.Attribute("Version")!)))
            .Distinct()
            .ToArray();
        Assert.Equal(["7.0.1"], versions);
    }

    private static XDocument Load(params string[] parts) => XDocument.Load(PathOf(parts));

    private static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
}
