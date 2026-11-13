using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61 with the real Windows Service Control Manager. A uniquely named instance of the built service is registered
/// with the production <c>sc.exe</c> settings (virtual account, Spooler dependency, required privileges, recovery
/// actions) on a data folder under ProgramData that an older Print Bridge left: inherited (permissive) access rules, a
/// paired settings file with its device token and print history. The production client talks to it with the production
/// server check (the pipe's server must be the process the Service Control Manager runs for the service). The engine
/// listens to a local fake server with a fake token: nothing is printed on paper.
///
/// Two tests, so that a printer environment problem cannot hide the service, permission and recovery results: one for
/// the Service Control Manager, the data folder's rules, the virtual account, the pipe's users and recovery (test mode);
/// one for the printers the service can use, with a disposable machine printer it really prints to.
///
/// Registering a service changes the machine, so this runs only on GitHub-hosted runners (disposable, elevated) and skips
/// everywhere else, like <see cref="SetupInstanceCrossAccountTests"/>; a skip fails CI there. The instance has its own
/// service name, account, pipe, engine lock and data folder, and is stopped and deleted afterwards. Each step writes what
/// it showed to the test output (the TRX file).
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeWindowsServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private readonly List<PrintBridgeServiceClient> _clients = [];
    private readonly List<string> _evidence = [];
    private readonly string _install;
    private readonly string _name;
    private IDisposable? _rootScope;
    private TrayFakeServer? _server;
    private bool _registered;
    private string? _machinePrinter;
    private bool _debugMode;

    public PrintBridgeWindowsServiceTests()
    {
        _name = "WaslaPbTest_" + _id;
        _install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaslaPbTest-" + _id);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DataRoot => Path.Combine(_install, "data");

    private string SettingsPath => Path.Combine(DataRoot, "appsettings.json");

    private PrintBridgeServiceEndpoint Endpoint => PrintBridgeServiceEndpoint.ForDataRoot(DataRoot, _name);

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        if (_registered)
        {
            Sc("stop", _name);
            WaitForStatus(ServiceControllerStatus.Stopped, throwOnTimeout: false);
            Sc("delete", _name);
        }

        if (_machinePrinter is not null)
        {
            // The port is in use until the spooler has deleted the printer; the runner is disposable, so a port left
            // behind is harmless.
            PowerShell($"Remove-Printer -Name '{_machinePrinter}' -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                       $"Remove-PrinterPort -Name '{_machinePrinter}' -ErrorAction SilentlyContinue");
        }

        _server?.Dispose();
        _rootScope?.Dispose();
        try
        {
            if (Directory.Exists(_install))
            {
                Run("icacls.exe", _install, "/reset", "/T", "/C", "/Q");
                Directory.Delete(_install, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (_debugMode)
            Process.LeaveDebugMode();

        if (_evidence.Count > 0)
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, _evidence));
    }

    [Fact]
    public async Task TheRealService_MigratesAnExistingInstallation_RunsAsItsVirtualAccount_ServesOnlyLocalUsers_AndRecovers()
    {
        SetupInstanceCrossAccountTests.SkipUnlessDisposableRunner();
        Assert.True(IsElevated(), "The GitHub-hosted runner is elevated; registering a service needs it.");
        // The service's process belongs to its virtual account: an administrator opens it (its token, a kill) with the
        // debug privilege, as Task Manager does.
        Process.EnterDebugMode();
        _debugMode = true;
        _evidence.Add($"runner: {Environment.MachineName}, elevated, session {Process.GetCurrentProcess().SessionId}");

        // ---- 1. An installation an older Print Bridge app left: ProgramData's inherited rules, a paired token, history.
        _server = new TrayFakeServer();
        Directory.CreateDirectory(DataRoot);
        _rootScope = PrintBridgePaths.UseRootForTests(DataRoot);
        var printer = PreferredPrinter();
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        TrayExitTests.ListenToTheFakeServer(document, _server.Url);
        document.PrintBridge.PrinterName = printer;
        store.Save(document);
        var earlierJob = Guid.NewGuid();
        new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance).Record(new LocalPrintJobRecord
        {
            JobId = earlierJob,
            OrderDisplay = "GTR-OLD-1",
            Status = LocalPrintJobStatus.Printed,
            CreatedAtUtc = DateTime.UtcNow,
            PrintedAtUtc = DateTime.UtcNow
        });
        Assert.True(Grants(new FileInfo(SettingsPath).GetAccessControl(), Users, FileSystemRights.ReadData),
            "Before WAS-61 every local user can read the settings file (and so the token).");
        _evidence.Add("existing installation: settings with a device token and history under ProgramData; BUILTIN\\Users can read the token");

        var exe = InstallServiceBinaries();
        Register(exe);
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", _name).Translate(typeof(SecurityIdentifier));
        GrantReadAndExecute(Path.Combine(_install, "bin"), serviceSid);

        // ---- 2. Not secured yet: the service starts, refuses to run its engine, and says why.
        Sc("start", _name);
        WaitForStatus(ServiceControllerStatus.Running);
        var refusing = Connect();
        await refusing.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(
            () => refusing.GetStatus().LastIssue?.Code == PrintBridgeRuntimeIssueCode.DataFolderNotSecured,
            "the app shows DataFolderNotSecured");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.DoesNotContain(_server.Requests, r => r.EndsWith("jobs/pending", StringComparison.Ordinal));
        Sc("stop", _name);
        WaitForStatus(ServiceControllerStatus.Stopped);
        await refusing.DisposeAsync();
        _clients.Remove(refusing);
        _evidence.Add("unsecured folder: service running, engine not started, app shows DataFolderNotSecured, no server request");

        // ---- 3. secure-data needs an administrator: a standard user's attempt changes nothing.
        var before = Sddl(DataRoot);
        using (var standardUser = SetupInstanceCrossAccountTests.TemporaryWindowsAccount.Create())
        {
            var refused = standardUser.Run(() =>
            {
                try
                {
                    PrintBridgeDataFolderSecurity.Apply(DataRoot, serviceSid);
                    return "applied";
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
                {
                    return ex.GetType().Name;
                }
            });
            Assert.NotEqual("applied", refused);
            _evidence.Add($"secure-data as a standard user: refused ({refused}); folder rules unchanged");
        }

        Assert.Equal(before, Sddl(DataRoot));

        // ---- 4. secure-data as the installer runs it, twice (idempotent).
        var secured = Run(exe, "secure-data", "--service-name", _name, "--data-root", DataRoot);
        Assert.True(secured.ExitCode == 0, secured.Output);
        Assert.Null(PrintBridgeDataFolderSecurity.Verify(DataRoot, serviceSid));
        var rootSecurity = new DirectoryInfo(DataRoot).GetAccessControl();
        Assert.Equal(Administrators, rootSecurity.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(rootSecurity.AreAccessRulesProtected);
        Assert.False(Grants(new FileInfo(SettingsPath).GetAccessControl(), Users, FileSystemRights.ReadData));
        Assert.True(Grants(new DirectoryInfo(Path.Combine(DataRoot, "logs")).GetAccessControl(), Users, FileSystemRights.ReadData));
        var securedOnce = (Sddl(DataRoot), Sddl(SettingsPath), Sddl(Path.Combine(DataRoot, "logs")));
        var again = Run(exe, "secure-data", "--service-name", _name, "--data-root", DataRoot);
        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal(securedOnce, (Sddl(DataRoot), Sddl(SettingsPath), Sddl(Path.Combine(DataRoot, "logs"))));
        _evidence.Add($"secure-data: owner Administrators, protected, SYSTEM/Administrators/{serviceSid} only, logs readable by Users; a second run changes nothing");

        // ---- 5. The service, as its virtual account, uses the existing token, printer and history.
        Sc("start", _name);
        WaitForStatus(ServiceControllerStatus.Running);
        var firstPid = ServiceControlManagerVerifier.QueryServiceProcessId(_name)!.Value;
        await WaitUntilAsync(() => CountLogLines($"Account={serviceSid.Value}") >= 1, "the service logs the account it runs as");
        Assert.Equal(serviceSid, ProcessUser((int)firstPid));
        _evidence.Add($"running as NT SERVICE\\{_name} ({serviceSid}: its process token and its own startup line), pid {firstPid}, session {Process.GetProcessById((int)firstPid).SessionId}");

        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => app.IsRunning, "the service starts printing with the existing pairing");
        Assert.True(app.Settings.HasSavedToken);
        var servicePrinters = await app.ListPrintersAsync(Ct);
        Assert.Contains(servicePrinters, p => string.Equals(p, printer, StringComparison.OrdinalIgnoreCase));
        var history = await app.QueryPrintHistoryAsync(PrintHistoryDateFilter.Today, null, 0, 20, Ct);
        Assert.Contains(history.Rows, r => r.JobId == earlierJob);
        var first = _server.AddJob();
        await _server.JobFinished(first).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(first));
        _evidence.Add($"migration: existing token accepted by the server, history kept; service printers: {string.Join(", ", servicePrinters)}");

        // ---- 6. Closing the app does not stop printing or acknowledgement.
        await app.DisposeAsync();
        _clients.Remove(app);
        var second = _server.AddJob();
        await _server.JobFinished(second).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(second));
        _evidence.Add("printed and acknowledged with the app connected and after it closed");

        // ---- 7. Who may connect and read: another interactive user may use the app; a network logon may not open the
        // pipe; neither can read the token; local users can read the service log.
        var logFile = Directory.GetFiles(Path.Combine(DataRoot, "logs"), "wasla-print-bridge-*.log").First();
        using (var interactiveUser = SetupInstanceCrossAccountTests.TemporaryWindowsAccount.Create(TemporaryLogon.Interactive))
        {
            Assert.True(interactiveUser.HasGroup(WellKnownSidType.InteractiveSid));
            Assert.False(interactiveUser.HasGroup(WellKnownSidType.BuiltinAdministratorsSid));
            Assert.Equal("welcome", interactiveUser.Run(() => Handshake(Endpoint.PipeName)));
            Assert.False(interactiveUser.Run(() => CanRead(SettingsPath)));
            Assert.True(interactiveUser.Run(() => CanRead(logFile)));
        }

        using (var networkUser = SetupInstanceCrossAccountTests.TemporaryWindowsAccount.Create(TemporaryLogon.Network))
        {
            Assert.True(networkUser.HasGroup(WellKnownSidType.NetworkSid));
            Assert.False(networkUser.HasGroup(WellKnownSidType.InteractiveSid));
            Assert.Equal("refused", networkUser.Run(() => Handshake(Endpoint.PipeName)));
            Assert.False(networkUser.Run(() => CanRead(SettingsPath)));
        }

        _evidence.Add("another standard user, interactive logon (INTERACTIVE in its token): pipe welcome, token unreadable, log readable; a standard user with a network logon (NETWORK, no INTERACTIVE): pipe refused, token unreadable");

        // An app connected for the rest of the test (it reconnects after each restart).
        var waiting = Connect();
        await waiting.Connected.WaitAsync(Wait, Ct);

        // ---- 8. A kill between the print and its acknowledgement: the Service Control Manager restarts the service,
        // which reports the job printed once and never prints it again.
        var drop = new TrayGate();
        _server.DropPrintedReports = drop;
        var third = _server.AddJob();
        await drop.Entered.Task.WaitAsync(Wait, Ct);
        Process.GetProcessById((int)firstPid).Kill();
        _server.DropPrintedReports = null;
        drop.Open();
        await WaitUntilAsync(
            () => ServiceControlManagerVerifier.QueryServiceProcessId(_name) is { } pid && pid != 0 && pid != firstPid,
            "the recovery action restarts the service");
        WaitForStatus(ServiceControllerStatus.Running);
        await _server.JobFinished(third).WaitAsync(Wait, Ct);
        Assert.Equal("Printed", _server.State(third));
        // The server answers before the service logs that answer: wait for the line, then count it exactly.
        await WaitUntilAsync(() => CountLogLines($"Printed job reported after an interruption. JobId=\"{third}\"") >= 1, "the service logs the report");
        Assert.Equal(1, CountLogLines($"DryRun: no physical print was sent. JobId=\"{third}\""));
        Assert.Equal(1, CountLogLines($"Printed job reported after an interruption. JobId=\"{third}\""));
        _evidence.Add($"killed pid {firstPid} after printing job {third:N}; restarted by SCM as pid {ServiceControlManagerVerifier.QueryServiceProcessId(_name)}; reported printed once, printed once");

        // ---- 9. A graceful stop, then an engine lock held elsewhere (an older Print Bridge app): no printing until it is
        // released.
        Sc("stop", _name);
        WaitForStatus(ServiceControllerStatus.Stopped);
        Assert.True(CountLogLines("Wasla Print Bridge service stopped.") >= 1);
        Assert.False(new PrintBridgeSettingsStore().Load().PrintBridge.PrintingStoppedByUser);
        _evidence.Add("sc stop: stopped gracefully; not recorded as a user stop");

        using (new Mutex(initiallyOwned: false, Endpoint.EngineLockName, out var created))
        {
            Assert.True(created);
            Sc("start", _name);
            WaitForStatus(ServiceControllerStatus.Running);
            await WaitUntilAsync(
                () => waiting.ConnectionState == PrintBridgeServiceConnectionState.Connected
                      && waiting.GetStatus().LastIssue?.Code == PrintBridgeRuntimeIssueCode.EngineHeldElsewhere,
                "the app shows that another engine holds the lock");
            var held = _server.AddJob();
            await Task.Delay(TimeSpan.FromSeconds(4), Ct);
            Assert.Equal("Pending", _server.State(held));
        }

        await WaitUntilAsync(() => waiting.IsRunning, "the service takes over once the other engine is gone");
        _evidence.Add("engine lock held elsewhere: no job claimed; released: the service started printing");

        // ---- 10. Administrators keep access, and the rollback command gives the folder back to ProgramData's rules.
        Sc("stop", _name);
        WaitForStatus(ServiceControllerStatus.Stopped);
        Assert.Contains(TrayFakeServer.Token, File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        var rollback = Run("icacls.exe", DataRoot, "/reset", "/T", "/C");
        Assert.True(rollback.ExitCode == 0, rollback.Output);
        Assert.False(new DirectoryInfo(DataRoot).GetAccessControl().AreAccessRulesProtected);
        Assert.True(Grants(new FileInfo(SettingsPath).GetAccessControl(), Users, FileSystemRights.ReadData));
        _evidence.Add("administrator: settings readable after securing; icacls /reset /T restores ProgramData's inherited rules (rollback)");
    }

    /// <summary>
    /// The printers the real service can use, with a disposable machine printer: Windows' in-box "Generic / Text Only"
    /// driver on a standard TCP/IP port that sends RAW to 127.0.0.1, where this test receives what a network receipt
    /// printer on port 9100 would. Test mode is off: the service really prints, through the Windows spooler.
    ///
    /// A printer that only one Windows user has (a connection to another computer's shared printer, kept in that user's
    /// profile) cannot be created on a single GitHub-hosted runner. Windows refuses a connection to a printer the computer
    /// shares itself, under any of its names (error 123 by its name or address, 1801 by localhost or 127.0.0.1, also with
    /// DisableLoopbackCheck, the File and Printer Sharing firewall rules or RPC over named pipes), and "Microsoft Print to
    /// PDF" cannot be shared at all (3022). So this test covers the service's side of that boundary with a saved printer
    /// the service cannot see, and the app's message from the real status with this user's printer list substituted. A
    /// real per-user connection needs two computers and remains for WAS-28.
    /// </summary>
    [Fact]
    public async Task TheRealService_PrintsToAMachinePrinterInSession0_AndNeverToAPrinterItCannotSee()
    {
        SetupInstanceCrossAccountTests.SkipUnlessDisposableRunner();
        Assert.True(IsElevated(), "The GitHub-hosted runner is elevated; registering a service and a printer needs it.");
        _evidence.Add($"runner: {Environment.MachineName}, elevated, session {Process.GetCurrentProcess().SessionId}");

        // ---- 1. A paired installation from before WAS-61 whose saved printer the service cannot see (as a printer
        // connection in the signed-in user's profile), test mode off; registered, secured and started as the installer
        // does.
        const string unseen = @"\\wasla-print-server.invalid\Receipt";
        _server = new TrayFakeServer();
        Directory.CreateDirectory(DataRoot);
        _rootScope = PrintBridgePaths.UseRootForTests(DataRoot);
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        TrayExitTests.ListenToTheFakeServer(document, _server.Url);
        document.PrintBridge.PrinterName = unseen;
        document.PrintBridge.DryRun = false;
        store.Save(document);

        var exe = InstallServiceBinaries();
        Register(exe);
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", _name).Translate(typeof(SecurityIdentifier));
        GrantReadAndExecute(Path.Combine(_install, "bin"), serviceSid);
        var secured = Run(exe, "secure-data", "--service-name", _name, "--data-root", DataRoot);
        Assert.True(secured.ExitCode == 0, secured.Output);
        var job = _server.AddJob();
        Sc("start", _name);
        WaitForStatus(ServiceControllerStatus.Running);
        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);

        // ---- 2. The service does not print to a printer it cannot see: it does not start printing, reports the printer
        // missing, does not list it and refuses to save it. The waiting job is not claimed.
        const string declined = "Printing was not started at service start. Reason=Error.PrinterUnavailable";
        await WaitUntilAsync(() => CountLogLines(declined) >= 1, "the service declines to start printing");
        await WaitUntilAsync(() => app.GetStatus().PrinterName == unseen, "the app receives the service's state");
        var status = app.GetStatus();
        Assert.False(app.IsRunning);
        Assert.Equal(PrinterHealthStatus.NotFound, status.PrinterHealthStatus);
        Assert.DoesNotContain(await app.ListPrintersAsync(Ct), p => string.Equals(p, unseen, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Shell.Printer.NotInstalled", await app.SavePrinterAsync(unseen, Ct));
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal("Pending", _server.State(job));
        Assert.Equal(1, CountLogLines(declined));

        // The app's message for that printer, built from the status the real service sent. Whether this Windows user has
        // the printer is substituted (a real per-user connection cannot be created on one computer, above); the real
        // probe asks this user's Windows, finds the printer nowhere and keeps the plain "not found".
        var culture = WebShellTestSupport.Culture(SupportedCultures.English);
        var localizer = new PrintBridgeLocalizer(culture);
        var userHasIt = new ShellSnapshotFactory(localizer, culture, installedForThisUser: name => string.Equals(name, unseen, StringComparison.OrdinalIgnoreCase)).Create(status);
        var userLacksIt = new ShellSnapshotFactory(localizer, culture, installedForThisUser: UserPrinterProbe.IsInstalledForThisUser).Create(status);
        Assert.Equal(localizer["Shell.Printer.UserOnly"], userHasIt.Printer.Label);
        Assert.Equal(localizer.GetPrinterHealthStatus(PrinterHealthStatus.NotFound), userLacksIt.Printer.Label);
        _evidence.Add($"saved printer {unseen} the service cannot see: printing not started (Error.PrinterUnavailable), status NotFound, not in the service's list, saving refused (Shell.Printer.NotInstalled), job {job:N} left Pending");
        _evidence.Add($"app message from that real status, this user's printer list substituted: \"{userHasIt.Printer.Label}\"; with the real probe (not installed for this user either): \"{userLacksIt.Printer.Label}\"");

        // ---- 3. An administrator installs a receipt printer for the computer while the service runs: "Generic / Text
        // Only" on a standard TCP/IP port sending RAW to 127.0.0.1, where this test listens. The service lists it, it can be
        // saved, and printing starts.
        using var receiver = new RawPrinterReceiver();
        var printer = "WaslaPbTest_" + _id;
        _machinePrinter = printer;
        var installed = PowerShell(
            "Add-PrinterDriver -Name 'Generic / Text Only' -ErrorAction Stop; " +
            $"Add-PrinterPort -Name '{printer}' -PrinterHostAddress '127.0.0.1' -PortNumber {receiver.Port} -ErrorAction Stop; " +
            $"Add-Printer -Name '{printer}' -DriverName 'Generic / Text Only' -PortName '{printer}' -ErrorAction Stop");
        Assert.True(installed.ExitCode == 0, "Installing the machine printer: " + installed.Output);
        var servicePrinters = await app.ListPrintersAsync(Ct);
        Assert.Contains(servicePrinters, p => string.Equals(p, printer, StringComparison.OrdinalIgnoreCase));
        Assert.Null(await app.SavePrinterAsync(printer, Ct));
        await app.StartAsync(Ct);
        await WaitUntilAsync(
            () => app.IsRunning && app.GetStatus().PrinterHealthStatus == PrinterHealthStatus.Ready,
            "printing starts with the machine printer");
        _evidence.Add($"machine printer {printer} (Generic / Text Only, TCP/IP RAW to 127.0.0.1:{receiver.Port}) installed while the service ran: in the service's list ({string.Join(", ", servicePrinters)}), saved, printing started");

        // ---- 4. The waiting job is printed by the service, as its virtual account in session 0, through the Windows
        // spooler to that port, once, and reported printed.
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));
        await WaitUntilAsync(
            () => receiver.Received.Any(r => r.Contains("QA-5901", StringComparison.Ordinal)),
            "the receipt reaches the printer's port");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        var receipts = receiver.Received.Where(r => r.Contains("QA-5901", StringComparison.Ordinal)).ToArray();
        Assert.Single(receipts);
        Assert.Equal(0, CountLogLines("DryRun: no physical print was sent."));
        var servicePid = ServiceControlManagerVerifier.QueryServiceProcessId(_name)!.Value;
        _evidence.Add($"job {job:N} printed by pid {servicePid} (session {Process.GetProcessById((int)servicePid).SessionId}, NT SERVICE\\{_name}) through the spooler: the port received one receipt of {Encoding.UTF8.GetByteCount(receipts[0])} bytes with the order code; reported mark-printing, mark-printed");
    }

    private string InstallServiceBinaries()
    {
        var built = ServiceBuildOutput.Directory;
        _ = ServiceBuildOutput.Executable;
        var bin = Path.Combine(_install, "bin");
        foreach (var source in Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(bin, Path.GetRelativePath(built, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return Path.Combine(bin, "Wasla.PrintBridge.Service.exe");
    }

    /// <summary>The production registration (docs/integrations/print-bridge-service.md), for this instance's name and folder.</summary>
    private void Register(string exe)
    {
        var create = Sc(
            "create", _name,
            "binPath=", $"\"{exe}\" --service-name {_name} --data-root \"{DataRoot}\"",
            "obj=", $@"NT SERVICE\{_name}",
            "start=", "demand",
            "depend=", "Spooler",
            "DisplayName=", "Wasla Print Bridge test instance");
        Assert.True(create.ExitCode == 0, create.Output);
        _registered = true;
        Assert.True(Sc("privs", _name, "SeChangeNotifyPrivilege").ExitCode == 0);
        // The production delays are 5, 10 and 60 seconds; one second keeps the test short.
        Assert.True(Sc("failure", _name, "reset=", "86400", "actions=", "restart/1000/restart/1000/restart/1000").ExitCode == 0);
        Assert.True(Sc("failureflag", _name, "1").ExitCode == 0);
        _evidence.Add($"registered {_name} as NT SERVICE\\{_name}, depends on Spooler, privileges SeChangeNotifyPrivilege, restart on failure");
    }

    private PrintBridgeServiceClient Connect()
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(Endpoint.PipeName, new ServiceControlManagerVerifier(_name)),
            NullLogger.Instance);
        client.Start();
        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// The first answer of the service to a hello from the current (possibly impersonated) identity: <c>welcome</c>,
    /// <c>refused</c> when the pipe cannot be opened, or <c>disconnected</c>.
    /// </summary>
    private static string Handshake(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(5000);
        }
        catch (UnauthorizedAccessException)
        {
            return "refused";
        }

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hello", protocol = PrintBridgeIpcProtocol.Version, client = "test" }));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        pipe.Write(header);
        pipe.Write(body);
        pipe.Flush();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var text = PrintBridgeIpcProtocol.ReadFrameAsync(pipe, PrintBridgeIpcProtocol.MaxServiceFrameBytes, timeout.Token)
                .GetAwaiter().GetResult();
            return text is null ? "disconnected" : JsonDocument.Parse(text).RootElement.GetProperty("type").GetString() ?? "unknown";
        }
        catch (IOException)
        {
            return "disconnected";
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.ReadByte() >= -1;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Grants(FileSystemSecurity security, SecurityIdentifier sid, FileSystemRights right) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                      && sid.Equals(r.IdentityReference)
                      && (r.FileSystemRights & right) == right);

    private static string Sddl(string path)
    {
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
        return security.GetSecurityDescriptorSddlForm(sections);
    }

    private void WaitForStatus(ServiceControllerStatus status, bool throwOnTimeout = true)
    {
        try
        {
            using var controller = new ServiceController(_name);
            controller.WaitForStatus(status, Wait);
        }
        catch (Exception ex) when (!throwOnTimeout && ex is System.ServiceProcess.TimeoutException or InvalidOperationException)
        {
        }
    }

    private static (int ExitCode, string Output) Sc(params string[] args) => Run("sc.exe", args);

    private static (int ExitCode, string Output) PowerShell(string script) =>
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);

    private static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120_000);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static void GrantReadAndExecute(string folder, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string PreferredPrinter()
    {
        var installed = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
        return installed.FirstOrDefault(p => p == "Microsoft Print to PDF") ?? TrayExitTests.FirstInstalledPrinterOrSkip();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static SecurityIdentifier? ProcessUser(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        const uint tokenQuery = 0x0008;
        using var process = OpenProcess(processQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcess of the service");
        if (!OpenProcessToken(process, tokenQuery, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken of the service");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User;
    }

    private int CountLogLines(string text)
    {
        var count = 0;
        var directory = Path.Combine(DataRoot, "logs");
        foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory, "wasla-print-bridge-*.log") : [])
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains(text, StringComparison.Ordinal))
                    count++;
            }
        }

        return count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new System.TimeoutException($"Not reached in time: {what}.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// A loopback TCP listener that keeps what each connection sends, as a network receipt printer receives a RAW job from
    /// the spooler's standard TCP/IP port.
    /// </summary>
    private sealed class RawPrinterReceiver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _received = [];
        private readonly Task _accepting;

        public RawPrinterReceiver()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync();
        }

        public int Port { get; }

        public IReadOnlyList<string> Received
        {
            get
            {
                lock (_received)
                    return [.. _received];
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _accepting.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var buffer = new MemoryStream();
                    await client.GetStream().CopyToAsync(buffer, _stop.Token);
                    lock (_received)
                        _received.Add(Encoding.UTF8.GetString(buffer.ToArray()));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}
