using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.Installer;

/// <summary>
/// WAS-63 with the real setups on the disposable, elevated GitHub-hosted runner (docs/integrations/print-bridge-updates.md):
/// the installed service of this commit's test-signed build discovers the next build through the (fake) Wasla server,
/// refuses a tampered download, pauses printing at a safe point, and stages the verified setup; that setup, run as the app
/// starts it, either fails at its end (WiX's FailWhenDeferred) and Windows Installer rolls back to the installed version, or
/// installs, passes its health check inside the transaction and commits. The pairing, history, journal, the user's Stop and
/// the administrator's startup choice survive both.
/// <para>
/// What is real and what is simulated: the installations, rollbacks, service stops and starts are Windows Installer's and
/// the Service Control Manager's. The failure is induced (FailWhenDeferred), not a crash; no machine reboot happens here.
/// The app's confirmation dialog and its launch of the setup are not driven: the test runs the staged setup quietly, as
/// the app would start it after the user's confirmation (the app's flow is covered by the WebShell tests). Both builds are
/// signed with a throwaway certificate only this runner trusts (ci.yml); an unsigned build cannot update itself.
/// </para>
/// </summary>
public sealed partial class PrintBridgeInstallerTests
{
    [Fact]
    public async Task AConfirmedUpdate_IsVerified_ARolledBackOneRestoresThePreviousVersion_AndACommittedOneKeepsDataStopAndStartup()
    {
        var (setup, upgrade) = Prepare();
        Assert.True(setup.Publisher is not null, "CI signs both setups with its test certificate; an unsigned installation cannot update.");
        var seeded = SeedExistingInstallation();
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier));
        var upgradeBytes = File.ReadAllBytes(upgrade.Path);
        var updates = Path.Combine(_dataRoot, "updates");

        // ---- 1. The installed version, without the app at sign-in (an administrator's choice an update must keep).
        var installLog = Path.Combine(_logs, "u1-install.log");
        Assert.Equal(0, RunSetup(setup.Path, "/quiet", "/norestart", "StartAtSignIn=0", "/log", installLog));
        AssertInstalled(setup.Version, startsAtSignIn: false);
        var installedPid = await WaitForServiceAsync();
        AssertHealthCheckPassed(installLog, setup.Version);
        var client = Connect();
        await client.Connected.WaitAsync(Wait, Ct);
        await PrintsOnceAsync("after install");
        _evidence.Add($"install {setup.Version} (test-signed, StartAtSignIn=0): exit 0; no HKLM Run entry; health check in the MSI log: \"Wasla Print Bridge {setup.Version} is running and healthy.\"; service pid {installedPid}; a job printed once");

        // ---- 2. Discovery: a signed installation asks the server, which offers the next build.
        _server!.Release = new TrayFakeRelease(upgrade.Version, upgradeBytes);
        await WaitUntilAsync(() => client.CurrentUpdate is { Supported: true }, "the service reports updates as supported");
        Assert.Equal(new IpcUpdateCheckResult(setup.Version, upgrade.Version), await client.CheckForUpdateAsync(Ct));
        _evidence.Add($"check: installed {setup.Version}, available {upgrade.Version} (release description from the fake server: size {upgradeBytes.Length}, sha256 {upgrade.Sha256})");

        // ---- 3. A download that is not the described file is refused, deleted and never run.
        var tampered = (byte[])upgradeBytes.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        _server.Release = new TrayFakeRelease(upgrade.Version, upgradeBytes) { ServedSetup = tampered };
        var refusedAttempt = await client.PrepareUpdateAsync(upgrade.Version, Ct);
        var refused = await WaitForUpdateAsync(client, u => u.AttemptId == refusedAttempt && u.Stage == PrintBridgeUpdateStage.Failed, "the tampered download is refused");
        Assert.Equal(PrintBridgeUpdateFailure.HashMismatch, refused.Failure);
        Assert.Empty(StagedSetups(updates));
        AssertInstalled(setup.Version, startsAtSignIn: false);
        Assert.Equal(installedPid, ServiceProcessId());
        await PrintsOnceAsync("after a refused download");
        _evidence.Add($"tampered download (one byte changed, same size): attempt {refusedAttempt:N} failed HashMismatch; nothing staged; {setup.Version} still installed, service pid {installedPid} unchanged; a job printed once");

        // ---- 4. Prepared while a receipt is in progress: the pause waits for it, then printing is paused.
        _server.Release = new TrayFakeRelease(upgrade.Version, upgradeBytes);
        var claim = new TrayGate();
        _server.HoldClaimAnswer = claim;
        var inProgress = _server.AddJob();
        await claim.Entered.Task.WaitAsync(Wait, Ct);
        var attempt = await client.PrepareUpdateAsync(upgrade.Version, Ct);
        await WaitForUpdateAsync(client, u => u.AttemptId == attempt && u.Stage == PrintBridgeUpdateStage.WaitingForPrinting, "the update waits for the receipt in progress");
        _server.HoldClaimAnswer = null;
        claim.Open();
        await _server.JobFinished(inProgress).WaitAsync(Wait, Ct);
        var ready = await WaitForUpdateAsync(client, u => u.AttemptId == attempt && u.Stage == PrintBridgeUpdateStage.Ready, "the verified setup is ready");
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(inProgress));
        var staged = AssertStaged(ready, upgrade, updates);

        // As the app does before it starts the setup: the same checks again, against the installed publisher and version.
        var release = new PrintBridgeRelease(Version.Parse(upgrade.Version), Path.GetFileName(staged), ready.SetupBytes!.Value, ready.SetupSha256!);
        Assert.Equal(PrintBridgeSetupVerification.Verified, PrintBridgeSetupVerifier.Verify(staged, release, setup.Publisher!, Version.Parse(setup.Version)));
        var paused = _server.AddJob();
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("Pending", _server.State(paused));
        _evidence.Add($"prepare {upgrade.Version} (attempt {attempt:N}) while a job was claimed: WaitingForPrinting until that job printed and reported once, then Ready; staged {staged} ({ready.SetupBytes} bytes, sha256 = the build's), Users may read, not change, the file or folder; re-verified as the app does (signature, publisher {setup.Publisher!.Subject}, version); a new job stayed Pending for 5 s");

        // ---- 5. The setup fails at its end: Windows Installer rolls back to the installed version.
        await client.DisposeAsync();
        _clients.Remove(client);
        var rolledBackLog = Path.Combine(_logs, "u2-update-rolled-back.log");
        var failedExit = RunSetup(staged, "/quiet", "/norestart", "FailWhenDeferred=1", "/log", rolledBackLog);
        Assert.NotEqual(0, failedExit);
        AssertHealthCheckPassed(rolledBackLog, upgrade.Version);
        var rolledBackMsi = PackageLog(rolledBackLog);
        Assert.Contains("FailWhenDeferred", rolledBackMsi, StringComparison.Ordinal);
        Assert.Contains("Rollback", rolledBackMsi, StringComparison.Ordinal);
        AssertInstalled(setup.Version, startsAtSignIn: false);
        var restoredPid = await WaitForServiceAsync();
        AssertServiceRegistration(serviceSid);
        Assert.Null(PrintBridgeDataFolderSecurity.Verify(_dataRoot, serviceSid));
        client = Connect();
        await client.Connected.WaitAsync(Wait, Ct);
        var notApplied = await WaitForUpdateAsync(client, u => u.AttemptId == attempt && u.Stage == PrintBridgeUpdateStage.NotApplied, "the restored version records the update as not applied");
        Assert.Equal(setup.Version, notApplied.InstalledVersion);
        AssertDataKept(seeded, "after a rolled-back update");
        await WaitUntilAsync(() => StagedSetups(updates).Length == 0, "the staged setup is deleted once the setup has exited");
        await _server.JobFinished(paused).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(paused));
        AssertNoRequestFrom(upgrade.Version);
        _evidence.Add($"update run with FailWhenDeferred=1 (an induced failure at the end of the MSI, not a crash or reboot): setup exit {failedExit}; {upgrade.Version}'s health check passed inside the transaction, then WixFailWhenDeferred failed and Windows Installer rolled back; {setup.Version} installed again (files, Installed Apps, MSI, no Run entry), service restored and registered as before (pid {restoredPid}), data folder still secured; attempt NotApplied; staged setup deleted; data kept; the job held by the pause printed once; {upgrade.Version} never contacted the server");

        // ---- 6. The user stops printing, then confirms the update again: it installs and commits.
        await client.StopAsync();
        Assert.True(Settings().PrintBridge.PrintingStoppedByUser);
        var secondAttempt = await client.PrepareUpdateAsync(upgrade.Version, Ct);
        var secondReady = await WaitForUpdateAsync(client, u => u.AttemptId == secondAttempt && u.Stage == PrintBridgeUpdateStage.Ready, "the verified setup is ready again");
        var secondStaged = AssertStaged(secondReady, upgrade, updates);
        await client.DisposeAsync();
        _clients.Remove(client);

        // The app starts the setup, so the setup is the app's child process; the installation ends the app but must not
        // end its children (WAS-62's taskkill /T did, aborting an app-started update or Repair). Here the setup is started
        // with the running installed app as its parent process, as the app's own launch makes it.
        // No Run entry on this installation (StartAtSignIn=0): the app is started with the sign-in argument by hand.
        var app = Process.Start(new ProcessStartInfo(AppExe, PrintBridgeSignInStart.Argument) { UseShellExecute = false, WorkingDirectory = _installFolder })!;
        _apps.Add(app);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.False(app.HasExited);
        var updateLog = Path.Combine(_logs, "u3-update.log");
        Assert.Equal(0, RunSetupAsChildOf(app, secondStaged, "/quiet", "/norestart", "/log", updateLog));
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(30)), "The update ends the app that started it.");
        AssertHealthCheckPassed(updateLog, upgrade.Version);
        var updateMsi = PackageLog(updateLog);
        var removeAt = updateMsi.IndexOf(": RemoveExistingProducts.", StringComparison.Ordinal);
        var installAt = updateMsi.IndexOf(": InstallFiles.", StringComparison.Ordinal);
        Assert.True(removeAt >= 0 && installAt > removeAt, "The update removes the installed version before it writes new files.");
        AssertInstalled(upgrade.Version, startsAtSignIn: false);
        var updatedPid = await WaitForServiceAsync();
        AssertServiceRegistration(serviceSid);
        Assert.Null(PrintBridgeDataFolderSecurity.Verify(_dataRoot, serviceSid));
        client = Connect();
        await client.Connected.WaitAsync(Wait, Ct);
        var succeeded = await WaitForUpdateAsync(client, u => u.AttemptId == secondAttempt && u.Stage == PrintBridgeUpdateStage.Succeeded, "the new version records the committed update");
        Assert.Equal(upgrade.Version, succeeded.InstalledVersion);
        await WaitUntilAsync(() => StagedSetups(updates).Length == 0, "the staged setup is deleted once the setup has exited");
        AssertDataKept(seeded, "after the update");

        // The user's Stop holds across the update; Start prints again.
        Assert.True(Settings().PrintBridge.PrintingStoppedByUser);
        Assert.False(client.IsRunning);
        var afterUpdate = _server.AddJob();
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("Pending", _server.State(afterUpdate));
        await client.StartAsync(Ct);
        await _server.JobFinished(afterUpdate).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(afterUpdate));
        Assert.Contains(_server.VersionedRequests, r => r.Version == upgrade.Version + ".0");

        // The installed health check, as the installer ran it.
        Assert.Equal(0, RunProcess(ServiceExe, ["health-check"], TimeSpan.FromMinutes(2)));
        AssertNoSecretInLogs();
        _evidence.Add($"user Stop, then update {setup.Version} -> {upgrade.Version} (attempt {secondAttempt:N}), its setup started as a child of the running installed app (pid {app.Id}): setup exit 0, the app ended by the installation; RemoveExistingProducts before InstallFiles; health check passed inside the transaction; {upgrade.Version} installed with no Run entry (startup choice kept), service registered as before (pid {restoredPid} -> {updatedPid}), data folder secured; attempt Succeeded after Windows Installer's commit; staged setup deleted; data kept; Stop kept (a job stayed Pending for 5 s) until Start, then printed once by {upgrade.Version}; health-check exit 0; no device token in {Directory.GetFiles(_logs).Length} installer logs");
    }

    /// <summary>
    /// Runs <paramref name="setup"/> as a child process of <paramref name="parent"/> (CreateProcess with the parent-process
    /// attribute), as when the app starts it, and returns its exit code. Its own children (Burn's clean room and engine) are
    /// then the parent's descendants too.
    /// </summary>
    private int RunSetupAsChildOf(Process parent, string setup, params string[] args)
    {
        var started = Stopwatch.StartNew();
        var commandLine = new StringBuilder($"\"{setup}\"");
        foreach (var arg in args)
            commandLine.Append(' ').Append(arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg);

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var parentHandle = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Assert.True(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), $"InitializeProcThreadAttributeList: {Marshal.GetLastWin32Error()}");
            Marshal.WriteIntPtr(parentHandle, parent.Handle);
            Assert.True(
                UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributeParentProcess, parentHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero),
                $"UpdateProcThreadAttribute: {Marshal.GetLastWin32Error()}");
            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            Assert.True(
                CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent, IntPtr.Zero, Path.GetDirectoryName(setup), ref startup, out var info),
                $"CreateProcess: {Marshal.GetLastWin32Error()}");
            try
            {
                Assert.Equal(0u, WaitForSingleObject(info.Process, (uint)SetupTimeout.TotalMilliseconds));
                Assert.True(GetExitCodeProcess(info.Process, out var exit));
                _evidence.Add($"  {Path.GetFileName(setup)} {string.Join(' ', args.Where(a => a.StartsWith('/') && a != "/log"))} (child of app pid {parent.Id}): exit {exit} in {started.Elapsed.TotalSeconds:0} s");
                return (int)exit;
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(parentHandle);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private const int ProcThreadAttributeParentProcess = 0x00020000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    private static string[] StagedSetups(string updates) =>
        Directory.Exists(updates) ? Directory.GetFiles(updates, "Wasla.PrintBridge-Setup-x64-*") : [];

    /// <summary>The verified setup the service staged for the app: the build's bytes, readable by users, changeable by none.</summary>
    private static string AssertStaged(IpcUpdate ready, BuiltSetup upgrade, string updates)
    {
        var staged = ready.StagedSetupPath;
        Assert.NotNull(staged);
        Assert.Equal(updates, Path.GetDirectoryName(staged), ignoreCase: true);
        Assert.Equal($"Wasla.PrintBridge-Setup-x64-{upgrade.Version}.exe", Path.GetFileName(staged));
        Assert.Equal(upgrade.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged))).ToLowerInvariant());
        Assert.Equal(upgrade.Sha256, ready.SetupSha256);
        Assert.Single(StagedSetups(updates));
        Assert.True(UsersCanRead(staged), "The app (a signed-in user) can read the staged setup.");
        Assert.False(UsersCanWrite(staged), "No user can change the staged setup.");
        Assert.False(UsersCanWrite(updates), "No user can add or replace files in the updates folder.");
        return staged;
    }

    /// <summary>The MSI's health check ran with the new service inside the transaction and passed (WixQuietExec logs its output).</summary>
    private static void AssertHealthCheckPassed(string bundleLog, string version) =>
        Assert.Contains($"Wasla Print Bridge {version} is running and healthy.", PackageLog(bundleLog), StringComparison.Ordinal);

    private void AssertNoRequestFrom(string version)
    {
        var fromVersion = _server!.VersionedRequests.Where(r => r.Version == version + ".0").Select(r => r.Request).ToArray();
        Assert.True(fromVersion.Length == 0, $"Version {version} contacted the server before its installation committed: {string.Join(", ", fromVersion)}");
    }

    private PrintBridgeSettingsStore.AppSettingsDocument Settings()
    {
        using (PrintBridgePaths.UseRootForTests(_dataRoot))
            return new PrintBridgeSettingsStore().Load();
    }

    private static async Task<IpcUpdate> WaitForUpdateAsync(PrintBridgeServiceClient client, Func<IpcUpdate, bool> condition, string what)
    {
        IpcUpdate? last = null;
        try
        {
            await WaitUntilAsync(() => (last = client.CurrentUpdate) is { } update && condition(update), what);
        }
        catch (System.TimeoutException)
        {
            throw new System.TimeoutException($"{what}: last update state {last?.Stage} {last?.Failure} (attempt {last?.AttemptId})");
        }

        return last!;
    }
}
