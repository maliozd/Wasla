using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.Installer;

/// <summary>
/// WAS-62: the runtime notice's Repair finds the setup that installed Print Bridge by the setup's upgrade code in Installed
/// Apps (either registry view) and starts its cached copy with <c>/repair</c>. The Installed Apps roots here are keys of this
/// test under HKCU; nothing is started.
/// </summary>
public sealed class PrintBridgeInstalledSetupTests : IDisposable
{
    private const string TestsKey = @"Software\WaslaPrintBridgeTests";

    private readonly string _root = TestsKey + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _uninstall64;
    private readonly RegistryKey _uninstall32;
    private readonly DirectoryInfo _cache = Directory.CreateTempSubdirectory("wasla-pb-package-cache-");

    public PrintBridgeInstalledSetupTests()
    {
        _uninstall64 = Registry.CurrentUser.CreateSubKey(_root + @"\64");
        _uninstall32 = Registry.CurrentUser.CreateSubKey(_root + @"\32");
    }

    public void Dispose()
    {
        _uninstall64.Dispose();
        _uninstall32.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        try
        {
            // The shared parent goes once no test class uses it; another class may just have added its own key.
            Registry.CurrentUser.DeleteSubKey(TestsKey, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
        }

        _cache.Delete(recursive: true);
    }

    [Fact]
    public void TheSetup_IsFoundByItsUpgradeCode_InEitherRegistryView()
    {
        var other = CachedFile("other-setup.exe");
        Register(_uninstall64, "{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}", other);
        Assert.Null(PrintBridgeInstalledSetup.FindCachedSetup([_uninstall64, _uninstall32]));

        var setup = CachedFile("Wasla.PrintBridge-Setup-x64.exe");
        Register(_uninstall32, "{33333333-3333-3333-3333-333333333333}", PrintBridgeInstalledSetup.SetupUpgradeCode.ToLowerInvariant(), setup);

        Assert.Equal(setup, PrintBridgeInstalledSetup.FindCachedSetup([_uninstall64, _uninstall32]));
        Assert.Equal(setup, PrintBridgeInstalledSetup.FindCachedSetup([null, _uninstall32]));
    }

    [Fact]
    public void ARegistrationWithoutItsCachedSetup_OrWithARelativePath_OffersNoRepair()
    {
        Register(_uninstall64, "{44444444-4444-4444-4444-444444444444}", PrintBridgeInstalledSetup.SetupUpgradeCode,
            Path.Combine(_cache.FullName, "deleted.exe"));
        Register(_uninstall32, "{55555555-5555-5555-5555-555555555555}", PrintBridgeInstalledSetup.SetupUpgradeCode,
            "Wasla.PrintBridge-Setup-x64.exe");

        Assert.Null(PrintBridgeInstalledSetup.FindCachedSetup([_uninstall64, _uninstall32]));
    }

    [Fact]
    public void Repair_StartsTheCachedSetup_AndSaysWhetherItStarted()
    {
        var path = CachedFile("Wasla.PrintBridge-Setup-x64.exe");
        var started = new List<string>();

        var installed = new PrintBridgeInstalledSetup(NullLogger.Instance, () => path, p => { started.Add(p); return true; });
        Assert.True(installed.CanRepair);
        Assert.True(installed.StartRepair());
        Assert.Equal([path], started);

        var refused = new PrintBridgeInstalledSetup(NullLogger.Instance, () => path, _ => false);
        Assert.False(refused.StartRepair());

        var none = new PrintBridgeInstalledSetup(NullLogger.Instance, () => null, p => { started.Add(p); return true; });
        Assert.False(none.CanRepair);
        Assert.False(none.StartRepair());
        Assert.Single(started);
        Assert.False(PrintBridgeInstalledSetup.None.CanRepair);
    }

    [Fact]
    public void WithAnEmptyRuntimeFolder_TheProbeReportsTheRuntimeMissing()
    {
        // What WEBVIEW2_BROWSER_EXECUTABLE_FOLDER does for the installed app in PrintBridgeInstallerTests: a folder without a
        // runtime counts as no runtime, whatever this computer has installed.
        var empty = Directory.CreateDirectory(Path.Combine(_cache.FullName, "no-webview2-runtime"));

        var availability = new WebView2RuntimeProbe(empty.FullName).Probe();

        Assert.False(availability.IsAvailable);
        Assert.Null(availability.Version);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--startup" }, true)]
    [InlineData(new[] { "--STARTUP" }, true)]
    [InlineData(new[] { "wasla-printbridge://setup?code=x" }, false)]
    [InlineData(new[] { "--startup-later" }, false)]
    public void OnlyTheInstallersSignInStart_EndsQuietly_WhenTheAppAlreadyRuns(string[] args, bool atSignIn)
    {
        Assert.Equal(atSignIn, PrintBridgeSignInStart.IsRequestedBy(args));
    }

    private string CachedFile(string name)
    {
        var path = Path.Combine(_cache.FullName, Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stand-in");
        return path;
    }

    private static void Register(RegistryKey uninstall, string bundleId, string upgradeCode, string cachePath)
    {
        using var entry = uninstall.CreateSubKey(bundleId);
        entry.SetValue("BundleUpgradeCode", new[] { upgradeCode }, RegistryValueKind.MultiString);
        entry.SetValue("BundleCachePath", cachePath);
        entry.SetValue("DisplayName", "Wasla Print Bridge");
    }
}
