using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61 across a real process boundary: the built <c>Wasla.PrintBridge.Service.exe</c> runs as its own process (a
/// console run on a data folder of its own, as a developer starts it), and this test process is the app, through the
/// production <see cref="PrintBridgeServiceClient"/> over the real pipe. Killing the process stands in for a crash. The
/// Windows Service Control Manager is not involved here; <see cref="PrintBridgeWindowsServiceTests"/> covers it. The engine
/// listens to a local fake server with a fake token in test mode (nothing is printed).
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeServiceProcessTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-process-tests");
    private readonly TrayFakeServer _server = new();
    private readonly List<Process> _processes = [];
    private readonly List<PrintBridgeServiceClient> _clients = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PrintBridgeServiceEndpoint Endpoint => PrintBridgeServiceEndpoint.ForDataRoot(_dataRoot.Path);

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        foreach (var process in _processes)
            Kill(process);
        _server.Dispose();
        _dataRoot.Dispose();
    }

    [Fact]
    public async Task TheServiceProcess_ServesTheApp_AndKeepsPrintingAfterTheAppIsGone()
    {
        Configure();
        StartService();
        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => app.IsRunning);

        var first = _server.AddJob();
        await _server.JobFinished(first).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(first));
        await WaitUntilAsync(() => app.GetStatus().RecentJobs.Any(j => j.JobId == first && j.Status == LocalPrintJobStatus.Printed));

        await app.DisposeAsync();
        _clients.Remove(app);

        var second = _server.AddJob();
        await _server.JobFinished(second).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(second));
        Assert.All(_processes, p => Assert.False(p.HasExited));
    }

    [Fact]
    public async Task TheApp_ShowsTheServiceGone_AndReconnectsToItsNextProcess()
    {
        Configure();
        var service = StartService();
        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);

        Kill(service);
        await WaitUntilAsync(() => app.ConnectionState == PrintBridgeServiceConnectionState.Unavailable);
        Assert.Equal(PrintBridgeRuntimeIssueCode.ServiceUnavailable, app.GetStatus().LastIssue?.Code);

        StartService();
        await app.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => app.IsRunning);
    }

    [Fact]
    public async Task AKillBetweenThePrintAndItsReport_IsReportedOnceByTheNextProcess_AndNeverPrintedAgain()
    {
        Configure();
        var drop = new TrayGate();
        _server.DropPrintedReports = drop;
        var service = StartService();
        var jobId = _server.AddJob();

        // Printed (test mode: the receipt file was written), the printed report sent but never processed: then the
        // process dies.
        await drop.Entered.Task.WaitAsync(Wait, Ct);
        Kill(service);
        Assert.Equal("Printing", _server.State(jobId));
        Assert.Equal(PrintJournalStage.Printed, Assert.Single(new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance).Entries).Stage);
        _server.DropPrintedReports = null;
        drop.Open();

        StartService();
        await _server.JobFinished(jobId).WaitAsync(Wait, Ct);

        Assert.Equal("Printed", _server.State(jobId));
        // The server answers before the service logs that answer: wait for the line, then count it exactly.
        await WaitUntilAsync(() => CountLogLines($"Printed job reported after an interruption. JobId=\"{jobId}\"") >= 1);
        Assert.Equal(1, CountLogLines($"DryRun: no physical print was sent. JobId=\"{jobId}\""));
        Assert.Equal(1, CountLogLines($"Printed job reported after an interruption. JobId=\"{jobId}\""));
        await WaitUntilAsync(() => new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance).Entries.Count == 0);
    }

    [Fact]
    public async Task ASavedPrinterTheServiceCannotSee_IsNeverPrintedTo_AndIsReportedMissing()
    {
        // As after an upgrade from a version that printed as the signed-in user to a printer only that user had: the
        // service does not list it. Test mode is off, so the service would print if it started; the printer does not exist
        // anywhere, so nothing can reach paper.
        const string unseen = @"\\wasla-print-server.invalid\Receipt";
        const string declined = "Printing was not started at service start. Reason=Error.PrinterUnavailable";
        Configure(document =>
        {
            document.PrintBridge.PrinterName = unseen;
            document.PrintBridge.DryRun = false;
        });
        var job = _server.AddJob();
        StartService();
        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);

        await WaitUntilAsync(() => CountLogLines(declined) >= 1);
        await WaitUntilAsync(() => app.GetStatus().PrinterName == unseen);
        Assert.False(app.IsRunning);
        Assert.Equal(PrinterHealthStatus.NotFound, app.GetStatus().PrinterHealthStatus);
        Assert.DoesNotContain(await app.ListPrintersAsync(Ct), p => string.Equals(p, unseen, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Shell.Printer.NotInstalled", await app.SavePrinterAsync(unseen, Ct));
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal("Pending", _server.State(job));
        Assert.Equal(1, CountLogLines(declined));
    }

    [Fact]
    public async Task ASecondServiceProcessOnTheSameFolder_StartsNoEngine_UntilTheFirstIsGone()
    {
        Configure();
        var first = StartService();
        var app = Connect();
        await app.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => app.IsRunning);

        var second = StartService();
        await WaitUntilAsync(() => CountLogLines("Another Print Bridge engine is running") >= 1);
        await WaitUntilAsync(() => CountLogLines("pipe name is already in use") >= 1);
        Assert.Equal(1, CountLogLines("Engine lock taken"));

        // The first process dies: the second takes the lock and the pipe, and the app follows.
        Kill(first);
        await WaitUntilAsync(() => CountLogLines("Engine lock taken") == 2);
        await app.Connected.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => app.IsRunning);
        Assert.False(second.HasExited);
    }

    [Fact]
    public async Task AnOlderAppHoldingTheEngineLock_KeepsTheServiceFromPrinting_UntilItExits()
    {
        Configure();
        // What a Print Bridge app before WAS-61 does on this folder: it holds the engine lock while its own engine runs.
        var olderApp = new Mutex(initiallyOwned: false, Endpoint.EngineLockName, out var created);
        Assert.True(created);
        try
        {
            StartService();
            var app = Connect();
            await app.Connected.WaitAsync(Wait, Ct);

            Assert.Equal(PrintBridgeRuntimeIssueCode.EngineHeldElsewhere, app.GetStatus().LastIssue?.Code);
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.DoesNotContain(_server.Requests, r => r.EndsWith("jobs/pending", StringComparison.Ordinal));

            olderApp.Dispose();
            await _server.NextPoll().WaitAsync(Wait, Ct);
            await WaitUntilAsync(() => app.IsRunning);
        }
        finally
        {
            olderApp.Dispose();
        }
    }

    private void Configure(Action<PrintBridgeSettingsStore.AppSettingsDocument>? customize = null)
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        TrayExitTests.ListenToTheFakeServer(document, _server.Url);
        customize?.Invoke(document);
        store.Save(document);
    }

    private Process StartService()
    {
        var start = new ProcessStartInfo(ServiceBuildOutput.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ServiceBuildOutput.Directory
        };
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(_dataRoot.Path);
        start.Environment.Remove(PrintBridgePaths.DevelopmentDataRootVariable);
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        return process;
    }

    private PrintBridgeServiceClient Connect()
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(Endpoint.PipeName, new CurrentUserServerVerifier()),
            NullLogger.Instance);
        client.Start();
        _clients.Add(client);
        return client;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private int CountLogLines(string text)
    {
        var count = 0;
        var directory = Path.Combine(_dataRoot.Path, "logs");
        if (!Directory.Exists(directory))
            return 0;

        foreach (var file in Directory.GetFiles(directory, "wasla-print-bridge-*.log"))
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(50, Ct);
        }
    }
}
