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
        var commands = Commands(package);
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

        // The apps only, not their process trees: a setup the app started (update or Repair, WAS-63) is the app's child.
        Assert.Equal("\"[System64Folder]taskkill.exe\" /F /IM Wasla.PrintBridge.exe", commands["CloseApps"]);
    }

    /// <summary>
    /// WAS-63: a failed update rolls back the installed version's removal, and Windows Installer creates the deleted service
    /// again from its basic registration only. The same sc.exe settings are applied again in that rollback: rollback
    /// actions of the uninstallation, after StopServices and before DeleteServices, so they run once the service exists
    /// again and before Windows Installer starts it. CI proves the effect with the real setups
    /// (<see cref="PrintBridgeInstallerTests"/>, the rolled-back update).
    /// </summary>
    [Fact]
    public void AFailedUpdate_RestoresTheServicesPrivilegesAndRecoverySettings_BeforeTheRestoredServiceStarts()
    {
        var package = Load("installer", "print-bridge", "Package", "Package.wxs");
        var commands = Commands(package);
        var actions = package.Descendants(Wix + "CustomAction").ToDictionary(a => (string)a.Attribute("Id")!, a => a);
        var sequence = package.Descendants(Wix + "InstallExecuteSequence").Single().Elements(Wix + "Custom")
            .ToDictionary(c => (string)c.Attribute("Action")!, c => c);

        var previous = "StopServices";
        foreach (var setting in new[] { "Privileges", "Recovery", "RecoveryOnError" })
        {
            var restore = $"RestoreService{setting}";
            Assert.Equal(commands[$"ConfigureService{setting}"], commands[restore]);
            Assert.Equal("WixQuietExec", (string?)actions[restore].Attribute("DllEntry"));
            Assert.Equal("rollback", (string?)actions[restore].Attribute("Execute"));
            Assert.Equal("no", (string?)actions[restore].Attribute("Impersonate"));
            Assert.Equal(previous, (string?)sequence[restore].Attribute("After"));
            Assert.Null(sequence[restore].Attribute("Before"));
            Assert.Equal("REMOVE~=\"ALL\"", (string?)sequence[restore].Attribute("Condition"));
            previous = restore;
        }
    }

    /// <summary>
    /// WAS-63: an update keeps the computer's start at sign-in. The setup passes START_AT_SIGN_IN only when it is given
    /// one; otherwise an update without the Run entry keeps it off, and a first installation (or one with it) turns it on.
    /// </summary>
    [Fact]
    public void AnUpdateKeepsTheStartAtSignInChoice_UnlessTheSetupIsGivenOne()
    {
        var bundle = Load("installer", "print-bridge", "Bundle", "Bundle.wxs");
        var variable = bundle.Descendants(Wix + "Variable").Single(v => (string?)v.Attribute("Name") == "StartAtSignIn");
        Assert.Null(variable.Attribute("Value"));
        Assert.Contains(bundle.Descendants(Wix + "MsiProperty"),
            p => (string?)p.Attribute("Name") == "START_AT_SIGN_IN" && (string?)p.Attribute("Value") == "[StartAtSignIn]");

        var package = Load("installer", "print-bridge", "Package", "Package.wxs");
        var property = package.Descendants(Wix + "Property").Single(p => (string?)p.Attribute("Id") == "START_AT_SIGN_IN");
        Assert.Null(property.Attribute("Value"));
        Assert.Equal("yes", (string?)property.Attribute("Secure"));

        // The search reads the Run entry the StartAtSignIn feature installs, in the 64-bit registry view.
        var run = package.Descendants(Wix + "RegistryValue").Single(v => (string?)v.Attribute("Key") == @"Software\Microsoft\Windows\CurrentVersion\Run");
        var search = package.Descendants(Wix + "Property").Single(p => (string?)p.Attribute("Id") == "INSTALLED_RUN_ENTRY").Element(Wix + "RegistrySearch")!;
        Assert.Equal((string?)run.Attribute("Root"), (string?)search.Attribute("Root"));
        Assert.Equal((string?)run.Attribute("Key"), (string?)search.Attribute("Key"));
        Assert.Equal((string?)run.Attribute("Name"), (string?)search.Attribute("Name"));
        Assert.Equal("always64", (string?)search.Attribute("Bitness"));
        Assert.Equal("RunAtSignIn", (string?)run.Parent!.Attribute("Id"));

        var set = package.Descendants(Wix + "SetProperty").Where(p => (string?)p.Attribute("Id") == "START_AT_SIGN_IN").ToArray();
        Assert.Equal(2, set.Length);
        Assert.Equal(("KeepStartAtSignInOff", "0", "AppSearch", "both", "NOT START_AT_SIGN_IN AND WIX_UPGRADE_DETECTED AND NOT INSTALLED_RUN_ENTRY"), Describe(set[0]));
        Assert.Equal(("StartAtSignInByDefault", "1", "KeepStartAtSignInOff", "both", "NOT START_AT_SIGN_IN"), Describe(set[1]));

        var feature = package.Descendants(Wix + "Feature").Single(f => (string?)f.Attribute("Id") == "StartAtSignIn");
        Assert.Equal("START_AT_SIGN_IN = \"0\"", (string?)feature.Element(Wix + "Level")?.Attribute("Condition"));
        Assert.Equal("0", (string?)feature.Element(Wix + "Level")?.Attribute("Value"));
        Assert.Equal("RunAtSignIn", (string?)feature.Element(Wix + "ComponentRef")?.Attribute("Id"));

        static (string?, string?, string?, string?, string?) Describe(XElement p) =>
            ((string?)p.Attribute("Action"), (string?)p.Attribute("Value"), (string?)p.Attribute("After"), (string?)p.Attribute("Sequence"), (string?)p.Attribute("Condition"));
    }

    /// <summary>
    /// WAS-63: the service just started must be healthy before the installation commits, or Windows Installer restores the
    /// previous version; the rollback hook CI uses is WiX's own, off unless the setup is told otherwise; and the service
    /// recognizes its commit by the MSI's upgrade code.
    /// </summary>
    [Fact]
    public void TheNewServiceIsCheckedInsideTheTransaction_TheRollbackHookIsOff_AndTheServiceKnowsTheUpgradeCode()
    {
        var package = Load("installer", "print-bridge", "Package", "Package.wxs");
        var commands = Commands(package);
        Assert.Equal("\"[#ServiceExe]\" health-check", commands["CheckServiceHealth"]);
        Assert.True(Wasla.PrintBridge.Hosting.PrintBridgeServiceOptions.TryParse(["health-check"], out var options, out _));
        Assert.True(options!.HealthCheckOnly);

        var action = package.Descendants(Wix + "CustomAction").Single(a => (string?)a.Attribute("Id") == "CheckServiceHealth");
        Assert.Equal("WixQuietExec", (string?)action.Attribute("DllEntry"));
        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        var check = package.Descendants(Wix + "InstallExecuteSequence").Single().Elements(Wix + "Custom")
            .Single(c => (string?)c.Attribute("Action") == "CheckServiceHealth");
        Assert.Equal("StartServices", (string?)check.Attribute("After"));
        Assert.Equal("NOT REMOVE~=\"ALL\"", (string?)check.Attribute("Condition"));

        XNamespace util = "http://wixtoolset.org/schemas/v4/wxs/util";
        Assert.Single(package.Descendants(util + "FailWhenDeferred"));
        var bundle = Load("installer", "print-bridge", "Bundle", "Bundle.wxs");
        var hook = bundle.Descendants(Wix + "Variable").Single(v => (string?)v.Attribute("Name") == "FailWhenDeferred");
        Assert.Equal("0", (string?)hook.Attribute("Value"));
        Assert.Contains(bundle.Descendants(Wix + "MsiProperty"),
            p => (string?)p.Attribute("Name") == "WIXFAILWHENDEFERRED" && (string?)p.Attribute("Value") == "[FailWhenDeferred]");

        Assert.Equal(
            Wasla.PrintBridge.Updates.PrintBridgeInstalledProduct.MsiUpgradeCode.Trim('{', '}'),
            (string?)package.Descendants(Wix + "Package").Single().Attribute("UpgradeCode"),
            ignoreCase: true);
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

    /// <summary>Each deferred or rollback action's command line: the SetProperty named after it (no Action of its own).</summary>
    private static Dictionary<string, string> Commands(XDocument package) =>
        package.Descendants(Wix + "SetProperty")
            .Where(p => p.Attribute("Action") is null)
            .ToDictionary(p => (string)p.Attribute("Id")!, p => (string)p.Attribute("Value")!);

    private static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
}
