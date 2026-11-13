using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests.Installer;

/// <summary>
/// WAS-62 with the real setup EXE (<c>Wasla.PrintBridge-Setup-x64.exe</c>) the CI job built from this commit, installed on
/// the GitHub-hosted runner: install over an existing (pre-installer) Print Bridge data folder, repair, a major upgrade to a
/// newer build, a refused downgrade and uninstall; the sign-in start, one app per session and one engine; the WebView2
/// runtime notice of the installed app while the service prints.
/// <para>
/// Installing a product, a service and machine-wide registry entries changes the machine, so this runs only on GitHub-hosted
/// runners (disposable, elevated) and skips everywhere else, like <see cref="SetupInstanceCrossAccountTests"/>; a skip fails
/// CI there. The CI job passes the two setups in <c>WASLA_PRINT_BRIDGE_SETUP</c> and <c>WASLA_PRINT_BRIDGE_UPGRADE_SETUP</c>.
/// The installed service listens to a local fake server with a fake token in test mode: nothing is printed on paper. Each
/// step writes what it showed to the test output (the TRX file) only after its checks passed; cleanup runs after a failure.
/// </para>
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeInstallerTests : IAsyncDisposable
{
    private const string ServiceName = PrintBridgePaths.ServiceName;
    private const string MsiUpgradeCode = "{2DAEE4A2-EF79-45BD-B403-602FFC1E737E}";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Wasla Print Bridge";
    private const string HandlerCommandKey = @"Software\Classes\wasla-printbridge\shell\open\command";
    private const string UserHandlerKey = @"Software\Classes\wasla-printbridge";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(15);

    private readonly List<string> _evidence = [];
    private readonly List<Process> _apps = [];
    private readonly List<PrintBridgeServiceClient> _clients = [];
    private readonly string _installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wasla", "Print Bridge");
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wasla", "PrintBridge");
    private readonly string _logs;
    private string? _dataBackup;
    private bool _touchedMachine;
    private TrayFakeServer? _server;

    public PrintBridgeInstallerTests()
    {
        var configured = Environment.GetEnvironmentVariable("WASLA_PRINT_BRIDGE_INSTALLER_LOGS");
        _logs = Path.Combine(string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured, Guid.NewGuid().ToString("N")[..8]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string AppExe => Path.Combine(_installFolder, "Wasla.PrintBridge.exe");

    private string ServiceExe => Path.Combine(_installFolder, "Wasla.PrintBridge.Service.exe");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Wasla Print Bridge.lnk");

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        foreach (var app in _apps)
        {
            try
            {
                if (!app.HasExited)
                    app.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            app.Dispose();
        }

        if (_touchedMachine)
        {
            // Whatever a failed step left: the product (through its own cached setup or the MSI), then the service.
            Cleanup("uninstall the setup", () =>
            {
                if (PrintBridgeInstalledSetup.FindCachedSetup(UninstallRoots()) is { } cached)
                    RunProcess(cached, ["/uninstall", "/quiet", "/norestart", "/log", Path.Combine(_logs, "cleanup-bundle.log")], SetupTimeout);
            });
            Cleanup("remove the MSI", () =>
            {
                foreach (var product in RelatedProducts())
                    RunProcess("msiexec.exe", ["/x", product, "/qn", "/norestart"], SetupTimeout);
            });
            Cleanup("delete the service", () =>
            {
                RunProcess("sc.exe", ["stop", ServiceName], TimeSpan.FromMinutes(1));
                RunProcess("sc.exe", ["delete", ServiceName], TimeSpan.FromMinutes(1));
            });
            Cleanup("remove the per-user handler", () => Registry.CurrentUser.DeleteSubKeyTree(UserHandlerKey, throwOnMissingSubKey: false));

            // The data folder this test created (the uninstall keeps it), then the runner's own copy back, if it had one.
            try
            {
                if (Directory.Exists(_dataRoot))
                {
                    RunProcess("icacls.exe", [_dataRoot, "/reset", "/T", "/C", "/Q"], TimeSpan.FromMinutes(1));
                    Directory.Delete(_dataRoot, recursive: true);
                }

                if (_dataBackup is not null)
                    Directory.Move(_dataBackup, _dataRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _evidence.Add($"cleanup: data folder not restored ({ex.GetType().Name})");
            }
        }

        _server?.Dispose();
        if (_evidence.Count > 0)
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, _evidence));
    }

    private void Cleanup(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            _evidence.Add($"cleanup: could not {what} ({ex.GetType().Name})");
        }
    }

    [Fact]
    public async Task TheSetup_InstallsOverAnExistingInstallation_Repairs_Upgrades_RefusesADowngrade_AndUninstalls_KeepingTheData()
    {
        var (setup, upgrade) = Prepare();

        // ---- 1. A Print Bridge from before the installer: its data folder (ProgramData's inherited rules, a paired token,
        // history, a receipt waiting for the user's check) and its per-user setup-link handler.
        var seeded = SeedExistingInstallation();
        const string portable = "\"C:\\Wasla\\PrintBridge\\Wasla.PrintBridge.exe\" \"%1\"";
        using (var handler = Registry.CurrentUser.CreateSubKey(UserHandlerKey))
        using (var command = handler.CreateSubKey(@"shell\open\command"))
            command.SetValue(string.Empty, portable);
        _evidence.Add($"existing installation: paired settings (installation {seeded.InstallationId:N}), history, an uncertain journal entry, a marker file; BUILTIN\\Users can read the token; per-user wasla-printbridge handler -> portable copy");

        // ---- 2. Clean install of this commit's setup, quietly, as a deployment tool would.
        var installLog = Path.Combine(_logs, "1-install.log");
        Assert.Equal(0, RunSetup(setup.Path, "/quiet", "/norestart", "/log", installLog));
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier));
        AssertInstalled(setup.Version);
        var installedPid = await WaitForServiceAsync();
        AssertServiceRegistration(serviceSid);
        var bundleLog = File.ReadAllText(installLog);
        Assert.Contains("Detected package: WebView2Runtime, state: Present", bundleLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Applying execute package: WebView2Runtime", bundleLog, StringComparison.Ordinal);
        Assert.Empty(AppProcesses());
        _evidence.Add($"install {setup.Version}: exit 0; files, Start menu shortcut, no desktop shortcut, HKLM Run \"...Wasla.PrintBridge.exe\" --startup, HKLM wasla-printbridge handler, Installed Apps entry (MSI hidden); WebView2 Runtime {WebView2Version()} detected Present, its package not run; no app process started by setup or service");
        _evidence.Add($"service {ServiceName}: running as NT SERVICE\\{ServiceName} ({serviceSid.Value}), pid {installedPid}, session {Process.GetProcessById((int)installedPid).SessionId}; auto start, depends on Spooler, SeChangeNotifyPrivilege only, restart 5 s/10 s/60 s reset 1 day, also on error exit");

        // The data folder was migrated (secure-data, permissions only) and the existing pairing prints.
        Assert.Null(PrintBridgeDataFolderSecurity.Verify(_dataRoot, serviceSid));
        Assert.False(UsersCanRead(Path.Combine(_dataRoot, "appsettings.json")));
        AssertDataKept(seeded, "after install");
        var client = Connect();
        await client.Connected.WaitAsync(Wait, Ct);
        await PrintsOnceAsync("after install");
        _evidence.Add("data folder: secured for the service SID (Users cannot read the token); existing token, installation id, history, uncertain entry and marker kept; a job printed once (test mode) with the existing pairing");

        // ---- 3. The installed app at sign-in: it drops the per-user handler an earlier Print Bridge wrote.
        var app = StartAppAtSignIn();
        await WaitUntilAsync(() => Registry.CurrentUser.OpenSubKey(UserHandlerKey) is null, "the app removes the per-user handler");
        Assert.False(app.HasExited);
        _evidence.Add($"app started as the Run entry does (pid {app.Id}, session {app.SessionId}): per-user handler of the portable copy removed; the HKLM handler serves");

        // ---- 4. Repair restores a deleted program file and changes no data.
        var shellPage = Path.Combine(_installFolder, "shell-ui", "index.html");
        File.Delete(shellPage);
        await client.DisposeAsync();
        _clients.Remove(client);
        Assert.Equal(0, RunSetup(setup.Path, "/repair", "/quiet", "/norestart", "/log", Path.Combine(_logs, "2-repair.log")));
        Assert.True(File.Exists(shellPage), "Repair restores a missing program file.");
        AssertInstalled(setup.Version);
        var repairedPid = await WaitForServiceAsync();
        AssertServiceRegistration(serviceSid);
        AssertDataKept(seeded, "after repair");
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(30)), "Setup closes the running app before it changes files.");
        await PrintsOnceAsync("after repair");
        _evidence.Add($"repair: exit 0; deleted shell-ui\\index.html restored; app closed by setup; service restarted (pid {installedPid} -> {repairedPid}), registration unchanged; data kept; a job printed once");

        // ---- 5. Major upgrade to a newer build: the app is closed, the old version removed before new files, data kept.
        var upgradedApp = StartAppAtSignIn();
        var upgradeLog = Path.Combine(_logs, "3-upgrade.log");
        Assert.Equal(0, RunSetup(upgrade.Path, "/quiet", "/norestart", "/log", upgradeLog));
        Assert.True(upgradedApp.WaitForExit(TimeSpan.FromSeconds(30)), "The upgrade closes the running app.");
        AssertInstalled(upgrade.Version);
        var upgradedPid = await WaitForServiceAsync();
        AssertServiceRegistration(serviceSid);
        AssertDataKept(seeded, "after upgrade");
        var msiLog = PackageLog(upgradeLog);
        var removeAt = msiLog.IndexOf(": RemoveExistingProducts.", StringComparison.Ordinal);
        var installAt = msiLog.IndexOf(": InstallFiles.", StringComparison.Ordinal);
        Assert.True(removeAt >= 0 && installAt > removeAt, "The upgrade removes the installed version before it writes new files.");
        await PrintsOnceAsync("after upgrade");
        _evidence.Add($"upgrade {setup.Version} -> {upgrade.Version}: exit 0; running app closed; RemoveExistingProducts before InstallFiles in the MSI log; app and service files {upgrade.Version}; one Installed Apps entry and one MSI product; service pid {repairedPid} -> {upgradedPid}; data kept; a job printed once");

        // ---- 6. The older setup is refused, and nothing changes.
        var downgrade = RunSetup(setup.Path, "/quiet", "/norestart", "/log", Path.Combine(_logs, "4-downgrade.log"));
        Assert.NotEqual(0, downgrade);
        AssertInstalled(upgrade.Version);
        Assert.Equal(upgradedPid, ServiceProcessId());
        var msiDowngrade = RunProcess("msiexec.exe", ["/i", setup.Msi, "/qn", "/norestart", "/l*v", Path.Combine(_logs, "4-downgrade-msi.log")], SetupTimeout);
        Assert.Equal(1603, msiDowngrade);
        Assert.Contains("A newer version of Wasla Print Bridge is already installed.", File.ReadAllText(Path.Combine(_logs, "4-downgrade-msi.log")), StringComparison.Ordinal);
        AssertInstalled(upgrade.Version);
        _evidence.Add($"downgrade to {setup.Version}: setup exit {downgrade}, MSI exit 1603 with the downgrade message; {upgrade.Version} still installed, service pid {upgradedPid} unchanged");

        // ---- 7. Uninstall, as Installed Apps runs it: everything the setup owns goes, the data folder stays.
        var cached = PrintBridgeInstalledSetup.FindCachedSetup(UninstallRoots());
        Assert.NotNull(cached);
        Assert.Equal(0, RunSetup(cached!, "/uninstall", "/quiet", "/norestart", "/log", Path.Combine(_logs, "5-uninstall.log")));
        AssertUninstalled();
        AssertDataKept(seeded, "after uninstall");
        _evidence.Add("uninstall (cached setup, as Installed Apps runs it): exit 0; service, program folder, Start menu shortcut, HKLM Run entry, HKLM and HKCU wasla-printbridge handlers, Installed Apps entry and MSI product gone; data folder kept (token, installation id, history, uncertain entry, marker)");

        // ---- 8. No secret in what the setup wrote or ran.
        AssertNoSecretInLogs();
        _evidence.Add($"installer logs ({Directory.GetFiles(_logs).Length} files): no device token; service command line, Run entry and handler carry no secret");
    }

    [Fact]
    public async Task TheInstalledApp_StartsOncePerSession_TheServiceIsTheOneEngine_AndPrintsWhileTheRuntimeNoticeIsShown()
    {
        var (setup, _) = Prepare();
        var seeded = SeedExistingInstallation();
        Assert.Equal(0, RunSetup(setup.Path, "/quiet", "/norestart", "/log", Path.Combine(_logs, "1-install.log")));
        AssertInstalled(setup.Version);
        var servicePid = await WaitForServiceAsync();

        // ---- 1. Startup ownership: the setup and the service start no app; the Run entry does, once per session.
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Empty(AppProcesses());
        var first = StartAppAtSignIn();
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.False(first.HasExited);
        var second = StartAppAtSignIn();
        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(30)), "A second start in the same session ends.");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal([first.Id], AppProcesses().Select(p => p.Id));
        Assert.Equal([(int)servicePid], Process.GetProcessesByName("Wasla.PrintBridge.Service").Select(p => p.Id));
        var client = Connect();
        await client.Connected.WaitAsync(Wait, Ct);
        Assert.NotEqual(PrintBridgeRuntimeIssueCode.EngineHeldElsewhere, client.GetStatus().LastIssue?.Code);
        await PrintsOnceAsync("with the app running");
        _evidence.Add($"startup: no app after a quiet install or from the service; Run entry start -> one app (pid {first.Id}, session {first.SessionId}); a second start in that session exits 0 at once; one service engine (pid {servicePid}), not held elsewhere; a job printed once");
        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync(Ct);

        // ---- 2. The installed app without a usable WebView2 Runtime (a runtime folder without a runtime, as
        // WEBVIEW2_BROWSER_EXECUTABLE_FOLDER makes it look): a setup link opens the runtime notice, which offers Repair
        // because the setup installed this computer. The service prints meanwhile.
        var noRuntime = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wasla-pb-no-webview2-" + Guid.NewGuid().ToString("N")[..8]));
        var link = "wasla-printbridge://setup?server=" + Uri.EscapeDataString("https://wasla.invalid") + "&code=notarealsetupcode000000000001";
        var start = new ProcessStartInfo(AppExe) { UseShellExecute = false, WorkingDirectory = _installFolder };
        start.ArgumentList.Add(link);
        start.Environment["WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"] = noRuntime.FullName;
        var noticeApp = Process.Start(start)!;
        _apps.Add(noticeApp);

        // The notice can open before the app has read the saved language from the service; it is found in any language.
        var languages = new[] { "", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU" }.Select(LoadResources).ToArray();
        IntPtr notice = IntPtr.Zero;
        Dictionary<string, string>? shown = null;
        await WaitUntilAsync(() =>
        {
            foreach (var language in languages)
            {
                if ((notice = FindWindow(noticeApp.Id, language["RuntimeNotice.Title"])) != IntPtr.Zero)
                {
                    shown = language;
                    return true;
                }
            }

            return false;
        }, "the runtime notice");
        var texts = ChildTexts(notice);
        Assert.Contains(shown!["RuntimeNotice.Repair"], texts);
        Assert.Contains(shown["RuntimeNotice.Close"], texts);
        Assert.Contains(shown["RuntimeNotice.MessageRepair"], texts);
        var cached = new PrintBridgeInstalledSetup(NullLogger.Instance);
        Assert.True(cached.CanRepair);
        Assert.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            PrintBridgeInstalledSetup.FindCachedSetup(UninstallRoots()), StringComparison.OrdinalIgnoreCase);

        await PrintsOnceAsync("while the runtime notice is shown");
        Assert.True(IsWindowVisible(notice), "The notice stays open while the service prints.");
        AssertDataKept(seeded, "with the notice");
        _evidence.Add($"no usable WebView2 Runtime (empty runtime folder for the app process): a setup link opens \"{shown["RuntimeNotice.Title"]}\" with \"{shown["RuntimeNotice.Repair"]}\", \"{shown["RuntimeNotice.Close"]}\" and the repair text; the Installed Apps entry points at the cached setup under Package Cache; a job printed once while the notice was open");
    }

    // ---- Setup and checks -------------------------------------------------------------------------------------------

    private sealed record BuiltSetup(string Path, string Version, string Msi, string Sha256);

    private (BuiltSetup Setup, BuiltSetup Upgrade) Prepare()
    {
        SetupInstanceCrossAccountTests.SkipUnlessDisposableRunner();
        Assert.True(IsElevated(), "The GitHub-hosted runner is elevated; installing needs it.");
        var setup = Built("WASLA_PRINT_BRIDGE_SETUP");
        var upgrade = Built("WASLA_PRINT_BRIDGE_UPGRADE_SETUP");
        Assert.True(Version.Parse(upgrade.Version) > Version.Parse(setup.Version), "The upgrade setup must be newer.");

        // A clean machine for this product: nothing installed, no service; the runner's own data folder, if any, aside.
        Assert.Empty(RelatedProducts());
        Assert.Null(PrintBridgeInstalledSetup.FindCachedSetup(UninstallRoots()));
        Assert.Null(ServiceProcessId());
        Assert.False(Directory.Exists(_installFolder));
        _touchedMachine = true;
        Directory.CreateDirectory(_logs);
        if (Directory.Exists(_dataRoot))
        {
            _dataBackup = _dataRoot + ".before-installer-test-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(_dataRoot, _dataBackup);
        }

        _evidence.Add($"runner: {Environment.MachineName}, elevated, session {Process.GetCurrentProcess().SessionId}; setup {setup.Version} ({setup.Sha256[..12]}…), upgrade {upgrade.Version} ({upgrade.Sha256[..12]}…), both matching their metadata");
        return (setup, upgrade);
    }

    /// <summary>A setup the CI job built, with its version and SHA-256 checked against the build's metadata.</summary>
    private static BuiltSetup Built(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(path), $"{variable} must name the setup EXE the CI job built.");
        Assert.True(File.Exists(path), $"{variable}: {path} does not exist.");
        var folder = System.IO.Path.GetDirectoryName(path)!;
        using var metadata = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "Wasla.PrintBridge-package.json")));
        var root = metadata.RootElement;
        var setupEntry = root.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("role").GetString() == "setup");
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(setupEntry.GetProperty("sha256").GetString(), sha256);
        Assert.Equal(new FileInfo(path).Length, setupEntry.GetProperty("bytes").GetInt64());
        Assert.False(root.GetProperty("signed").GetBoolean(), "CI builds are unsigned development builds.");
        var msi = System.IO.Path.Combine(folder, "Wasla.PrintBridge-x64.msi");
        Assert.True(File.Exists(msi));
        return new BuiltSetup(path, root.GetProperty("version").GetString()!, msi, sha256);
    }

    private sealed record Seeded(Guid InstallationId, Guid HistoryJob, Guid UncertainJob, string Marker);

    private Seeded SeedExistingInstallation()
    {
        _server = new TrayFakeServer();
        Directory.CreateDirectory(_dataRoot);
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        using (PrintBridgePaths.UseRootForTests(_dataRoot))
        {
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            TrayExitTests.ListenToTheFakeServer(document, _server.Url);
            document.PrintBridge.InstallationId = seeded.InstallationId.ToString();
            document.Ui.Language = "en-US";
            store.Save(document);
            new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance).Record(new LocalPrintJobRecord
            {
                JobId = seeded.HistoryJob,
                OrderDisplay = "INST-HISTORY-1",
                Status = LocalPrintJobStatus.Printed,
                CreatedAtUtc = DateTime.UtcNow,
                PrintedAtUtc = DateTime.UtcNow
            });
            // Uncertain entries are never reported or removed by the service on their own: the user decides.
            new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance)
                .Record(seeded.UncertainJob, PrintJournalStage.Uncertain, document.Wasla);
        }

        File.WriteAllText(Path.Combine(_dataRoot, "installer-test-marker.txt"), seeded.Marker);
        Assert.True(UsersCanRead(Path.Combine(_dataRoot, "appsettings.json")), "Before the installer every local user can read the settings file.");
        return seeded;
    }

    private void AssertDataKept(Seeded seeded, string when)
    {
        using (PrintBridgePaths.UseRootForTests(_dataRoot))
        {
            var document = new PrintBridgeSettingsStore().Load();
            Assert.True(TrayFakeServer.Token == document.Wasla.AgentToken, $"The device token is kept {when}.");
            Assert.Equal(_server!.Url, document.Wasla.ServerUrl);
            Assert.Equal(seeded.InstallationId.ToString(), document.PrintBridge.InstallationId);
            Assert.NotNull(new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance).FindByJobId(seeded.HistoryJob));
            Assert.Contains(new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance).Entries,
                e => e.JobId == seeded.UncertainJob && e.Stage == PrintJournalStage.Uncertain);
        }

        Assert.Equal(seeded.Marker, File.ReadAllText(Path.Combine(_dataRoot, "installer-test-marker.txt")));
    }

    private void AssertInstalled(string version)
    {
        foreach (var exe in new[] { AppExe, ServiceExe })
        {
            Assert.True(File.Exists(exe), exe);
            Assert.Equal(version, Version.Parse(FileVersionInfo.GetVersionInfo(exe).FileVersion!).ToString(3));
        }

        Assert.True(File.Exists(Path.Combine(_installFolder, "shell-ui", "index.html")));
        Assert.Equal(StartMenuTarget(), AppExe, ignoreCase: true);
        foreach (var desktop in new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory })
        {
            var folder = Environment.GetFolderPath(desktop);
            Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder, "*Print Bridge*").Length == 0, $"No shortcut on {desktop}.");
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var run = machine.OpenSubKey(RunKey))
            Assert.Equal($"\"{AppExe}\" {PrintBridgeSignInStart.Argument}", run?.GetValue(RunValue) as string, ignoreCase: true);
        using (var handler = machine.OpenSubKey(HandlerCommandKey))
            Assert.Equal($"\"{AppExe}\" \"%1\"", handler?.GetValue(string.Empty) as string, ignoreCase: true);

        var products = RelatedProducts();
        Assert.Single(products);
        Assert.Equal(version, ProductInfo(products[0], "VersionString"));
        var bundles = BundleRegistrations();
        Assert.Single(bundles);
        Assert.Equal(version, bundles[0].Version);
        Assert.StartsWith("Wasla Print Bridge", bundles[0].Name, StringComparison.Ordinal);
        using var uninstall = machine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{products[0]}");
        Assert.Equal(1, uninstall?.GetValue("SystemComponent"));
    }

    private void AssertServiceRegistration(SecurityIdentifier serviceSid)
    {
        var config = Sc("qc", ServiceName);
        Assert.Matches(@"START_TYPE\s+:\s+2\s+AUTO_START", config);
        Assert.Matches($@"BINARY_PATH_NAME\s+:\s+""{System.Text.RegularExpressions.Regex.Escape(ServiceExe)}""\s*\r?\n", config);
        Assert.Matches($@"SERVICE_START_NAME\s+:\s+NT SERVICE\\{ServiceName}", config);
        Assert.Matches(@"DEPENDENCIES\s+:\s+Spooler", config);
        Assert.Matches(@"PRIVILEGES\s+:\s+SeChangeNotifyPrivilege\s*$", Sc("qprivs", ServiceName).TrimEnd());
        var failure = Sc("qfailure", ServiceName);
        Assert.Matches(@"RESET_PERIOD \(in seconds\)\s+:\s+86400", failure);
        Assert.Matches(@"RESTART -- Delay = 5000 milliseconds\.\s+RESTART -- Delay = 10000 milliseconds\.\s+RESTART -- Delay = 60000 milliseconds\.", failure);
        Assert.Matches(@"FAILURE_ACTIONS_ON_NONCRASH_FAILURES:\s+TRUE", Sc("qfailureflag", ServiceName));
        var pid = ServiceProcessId()!.Value;
        Assert.Equal(serviceSid, ProcessUser((int)pid));
    }

    private void AssertUninstalled()
    {
        Assert.Null(ServiceProcessId());
        Assert.Contains("1060", Sc("query", ServiceName), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_installFolder), "The program folder is removed.");
        Assert.False(File.Exists(StartMenuShortcut));
        Assert.Empty(RelatedProducts());
        Assert.Empty(BundleRegistrations());
        Assert.Empty(AppProcesses());
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var run = machine.OpenSubKey(RunKey))
            Assert.Null(run?.GetValue(RunValue));
        Assert.Null(machine.OpenSubKey(@"Software\Classes\wasla-printbridge"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(UserHandlerKey));
        Assert.Null(Registry.ClassesRoot.OpenSubKey("wasla-printbridge"));
    }

    private void AssertNoSecretInLogs()
    {
        foreach (var log in Directory.GetFiles(_logs))
            Assert.DoesNotContain(TrayFakeServer.Token, File.ReadAllText(log), StringComparison.Ordinal);
    }

    private async Task PrintsOnceAsync(string when)
    {
        var job = _server!.AddJob();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.True(_server.ReportsFor(job).SequenceEqual(["mark-printing", "mark-printed"]), $"A job is printed and reported once {when}.");
    }

    private PrintBridgeServiceClient Connect()
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(PrintBridgeServiceEndpoint.Production.PipeName, new ServiceControlManagerVerifier(ServiceName)),
            NullLogger.Instance);
        client.Start();
        _clients.Add(client);
        return client;
    }

    private Process StartAppAtSignIn()
    {
        // The Run entry's command line, as Explorer runs it at sign-in (here in the runner's session, from this elevated
        // process: Explorer would start it without elevation).
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var run = machine.OpenSubKey(RunKey)!;
        var command = (string)run.GetValue(RunValue)!;
        var exe = command[1..command.IndexOf('"', 1)];
        var start = new ProcessStartInfo(exe, command[(command.IndexOf('"', 1) + 1)..].Trim()) { UseShellExecute = false, WorkingDirectory = _installFolder };
        var process = Process.Start(start)!;
        _apps.Add(process);
        return process;
    }

    private async Task<uint> WaitForServiceAsync()
    {
        using (var controller = new ServiceController(ServiceName))
            controller.WaitForStatus(ServiceControllerStatus.Running, Wait);
        uint? pid = null;
        await WaitUntilAsync(() => (pid = ServiceProcessId()) is > 0, "the service's process");
        return pid!.Value;
    }

    private static uint? ServiceProcessId()
    {
        try
        {
            return ServiceControlManagerVerifier.QueryServiceProcessId(ServiceName) is { } pid and > 0 ? pid : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static Process[] AppProcesses() =>
        Process.GetProcessesByName("Wasla.PrintBridge").Where(p => !p.HasExited).ToArray();

    private int RunSetup(string setup, params string[] args)
    {
        var started = Stopwatch.StartNew();
        var exit = RunProcess(setup, args, SetupTimeout);
        _evidence.Add($"  {Path.GetFileName(setup)} {string.Join(' ', args.Where(a => a.StartsWith('/') && a != "/log"))}: exit {exit} in {started.Elapsed.TotalSeconds:0} s");
        return exit;
    }

    private static int RunProcess(string file, IEnumerable<string> args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new System.TimeoutException($"{Path.GetFileName(file)} did not finish within {timeout.TotalMinutes} minutes.");
        }

        return process.ExitCode;
    }

    private static string Sc(params string[] args)
    {
        var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return output;
    }

    /// <summary>The MSI log Burn wrote for the Print Bridge package of a setup run.</summary>
    private static string PackageLog(string bundleLog)
    {
        var folder = Path.GetDirectoryName(bundleLog)!;
        var stem = Path.GetFileNameWithoutExtension(bundleLog);
        var log = Directory.GetFiles(folder, stem + "_*_PrintBridge.log").Single();
        return File.ReadAllText(log);
    }

    private static string WebView2Version()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
        return key?.GetValue("pv") as string ?? "none";
    }

    private static string StartMenuTarget()
    {
        Assert.True(File.Exists(StartMenuShortcut), "The Start menu shortcut exists.");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(StartMenuShortcut);
            return (string)link.TargetPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool UsersCanRead(string path)
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        return new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && users.Equals(r.IdentityReference)
                      && (r.FileSystemRights & FileSystemRights.ReadData) == FileSystemRights.ReadData);
    }

    private static IEnumerable<RegistryKey?> UninstallRoots()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        return [machine64.OpenSubKey(uninstall), machine32.OpenSubKey(uninstall)];
    }

    private static List<(string Name, string Version)> BundleRegistrations()
    {
        var found = new List<(string, string)>();
        foreach (var root in UninstallRoots())
        {
            using (root)
            {
                if (root is null)
                    continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("BundleUpgradeCode") is string[] codes &&
                        codes.Any(c => string.Equals(c, PrintBridgeInstalledSetup.SetupUpgradeCode, StringComparison.OrdinalIgnoreCase)))
                        found.Add(((string)entry.GetValue("DisplayName")!, (string)entry.GetValue("DisplayVersion")!));
                }
            }
        }

        return found;
    }

    private static List<string> RelatedProducts()
    {
        var products = new List<string>();
        for (uint index = 0; ; index++)
        {
            var buffer = new StringBuilder(39);
            if (MsiEnumRelatedProducts(MsiUpgradeCode, 0, index, buffer) != 0)
                break;
            products.Add(buffer.ToString());
        }

        return products;
    }

    private static string ProductInfo(string product, string property)
    {
        uint length = 64;
        var buffer = new StringBuilder((int)length);
        Assert.Equal(0u, MsiGetProductInfo(product, property, buffer, ref length));
        return buffer.ToString();
    }

    private static Dictionary<string, string> LoadResources(string culture)
    {
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "Wasla.PrintBridge", "Resources", $"PrintBridgeResources{culture}.resx"));
        return document.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
    }

    private static IntPtr FindWindow(int processId, string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window) && WindowText(window) == title)
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static List<string> ChildTexts(IntPtr window)
    {
        var texts = new List<string>();
        EnumChildWindows(window, (child, _) =>
        {
            texts.Add(WindowText(child));
            return true;
        }, IntPtr.Zero);
        return texts;
    }

    private static string WindowText(IntPtr window)
    {
        var buffer = new StringBuilder(2048);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static SecurityIdentifier? ProcessUser(int processId)
    {
        using var process = Process.GetProcessById(processId);
        using var identity = TokenOf(process);
        return identity?.User;
    }

    private static WindowsIdentity? TokenOf(Process process)
    {
        const uint processQueryLimitedInformation = 0x1000;
        const uint tokenQuery = 0x0008;
        Process.EnterDebugMode();
        try
        {
            var handle = OpenProcess(processQueryLimitedInformation, false, (uint)process.Id);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                if (!OpenProcessToken(handle, tokenQuery, out var token))
                    return null;
                try
                {
                    return new WindowsIdentity(token);
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        finally
        {
            Process.LeaveDebugMode();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new System.TimeoutException($"Not reached in time: {what}.");
            await Task.Delay(200, Ct);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint productIndex, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
