using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-216: the native winspool boundary against the real Windows spooler, with a disposable machine printer (Windows'
/// in-box "Generic / Text Only" driver on a standard TCP/IP port sending RAW to a loopback listener this test controls).
/// It proves the interop (job ids, byte counts, finishing, reading queue and job status, finding a job by its document
/// name) and how jobs behave while the printer cannot take them. It is spooler and port evidence, not paper: a physical
/// receipt printer is certified separately (WAS-28). Creates and deletes a printer and a port, so it runs only on
/// GitHub-hosted runners (elevated, disposable) and skips elsewhere (CI fails on a skipped test).
/// </summary>
public sealed class WindowsPrintSpoolerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(4);

    private readonly string _printer = "WaslaPbSpool_" + Guid.NewGuid().ToString("N")[..8];
    private bool _installed;

    public void Dispose()
    {
        if (_installed)
        {
            PowerShell($"Remove-Printer -Name '{_printer}' -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                       $"Remove-PrinterPort -Name '{_printer}' -ErrorAction SilentlyContinue");
        }
    }

    [Fact]
    public async Task TheRealSpooler_CreatesTracksAndFindsItsOwnJobs_ReportsQueueStates_AndLeavesOtherJobsAlone()
    {
        SetupInstanceCrossAccountTests.SkipUnlessDisposableRunner();
        var spooler = new WindowsPrintSpooler();

        // ---- 0. A printer Windows does not have.
        Assert.Equal(PrinterAvailabilityState.NotFound, spooler.GetAvailability(_printer + "_missing").State);
        Log("a printer that does not exist: NotFound");

        // ---- 1. The disposable printer; its port's listener is started and stopped by the test.
        var port = FreePort();
        var installed = PowerShell(
            "Add-PrinterDriver -Name 'Generic / Text Only' -ErrorAction Stop; " +
            $"Add-PrinterPort -Name '{_printer}' -PrinterHostAddress '127.0.0.1' -PortNumber {port} -ErrorAction Stop; " +
            $"Add-Printer -Name '{_printer}' -DriverName 'Generic / Text Only' -PortName '{_printer}' -ErrorAction Stop");
        _installed = true;
        Assert.True(installed.ExitCode == 0, "Installing the disposable printer: " + installed.Output);
        var ready = spooler.GetAvailability(_printer);
        Assert.Equal(PrinterAvailabilityState.Ready, ready.State);
        Log($"disposable printer {_printer} (Generic / Text Only, TCP/IP RAW to 127.0.0.1:{port}): Ready (status 0x{ready.Status:X8}, attributes 0x{ready.Attributes:X8})");

        // ---- 2. A printer that takes the data: the job gets an id, every byte is written, and it leaves the queue.
        using (var receiver = new RawReceiver(port))
        {
            var text = $"WAS-216 spooler check {Guid.NewGuid():N}";
            var data = Encoding.UTF8.GetBytes(text);
            var document = DocumentName("1");
            var createdId = 0;
            var result = spooler.Submit(_printer, document, data, id => createdId = id);

            Assert.Equal(SpoolSubmitOutcome.Spooled, result.Outcome);
            Assert.True(result.JobId > 0);
            Assert.Equal(result.JobId, createdId);
            Assert.Equal(data.Length, result.BytesWritten);
            await WaitUntilAsync(() => spooler.Observe(_printer, result.JobId, document).State == SpoolJobState.Gone, "the job leaves the queue");
            await WaitUntilAsync(() => receiver.Received.Any(r => r.Contains(text, StringComparison.Ordinal)), "the port receives the data");
            Assert.Single(receiver.Received, r => r.Contains(text, StringComparison.Ordinal));
            Assert.Equal(SpoolJobLookupState.NotFound, spooler.Find(_printer, document).State);
            Log($"listener running: job {result.JobId} created, {result.BytesWritten} bytes written, finished, left the queue, received once on the port");
        }

        // ---- 3. Paused in Windows: availability says so, a job waits in the queue, is found by its document name, and a
        // job id read with another document name is not ours. Another application's job is never touched.
        Assert.Equal(0, PowerShell($"Get-CimInstance Win32_Printer -Filter \"Name='{_printer}'\" | Invoke-CimMethod -MethodName Pause | Out-Null").ExitCode);
        await WaitUntilAsync(() => spooler.GetAvailability(_printer).State == PrinterAvailabilityState.Paused, "the queue reports paused");
        var paused = spooler.GetAvailability(_printer);
        Log($"paused: {paused.State} (status 0x{paused.Status:X8})");

        var ours = DocumentName("2");
        var foreign = "Another application's document " + Guid.NewGuid().ToString("N")[..6];
        var ourJob = spooler.Submit(_printer, ours, Encoding.UTF8.GetBytes("ours " + ours), static _ => { });
        var foreignJob = spooler.Submit(_printer, foreign, Encoding.UTF8.GetBytes("foreign " + foreign), static _ => { });
        Assert.Equal(SpoolSubmitOutcome.Spooled, ourJob.Outcome);
        Assert.Equal(SpoolSubmitOutcome.Spooled, foreignJob.Outcome);

        var waiting = spooler.Observe(_printer, ourJob.JobId, ours);
        Assert.Equal(SpoolJobState.InQueue, waiting.State);
        Assert.Equal(new SpoolJobLookup(SpoolJobLookupState.Found, ourJob.JobId), spooler.Find(_printer, ours));
        // The id of a job with another name is "gone" for this receipt, as for an id Windows reused.
        Assert.Equal(SpoolJobState.Gone, spooler.Observe(_printer, foreignJob.JobId, ours).State);
        Log($"paused queue: job {ourJob.JobId} InQueue (status 0x{waiting.Status:X8}), found by its document name; job {foreignJob.JobId} of another document reads as not ours");

        // ---- 4. Resumed, but nothing listens on the port: the jobs stay in the queue (Windows cannot send them).
        Assert.Equal(0, PowerShell($"Get-CimInstance Win32_Printer -Filter \"Name='{_printer}'\" | Invoke-CimMethod -MethodName Resume | Out-Null").ExitCode);
        await WaitUntilAsync(() => spooler.GetAvailability(_printer).State != PrinterAvailabilityState.Paused, "the queue is resumed");
        var stuckUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        SpoolJobObservation stuck;
        do
        {
            stuck = spooler.Observe(_printer, ourJob.JobId, ours);
            Assert.Equal(SpoolJobState.InQueue, stuck.State);
            await Task.Delay(250, Ct);
        }
        while (DateTime.UtcNow < stuckUntil);
        Log($"resumed without a listener: job {ourJob.JobId} still InQueue after 5 s (status 0x{stuck.Status:X8}), availability {spooler.GetAvailability(_printer).State}");

        // ---- 5. The listener starts: both jobs are sent, each once, ours leaves the queue, the other job was untouched.
        using (var receiver = new RawReceiver(port))
        {
            // The TCP/IP port monitor retries a port it could not reach on its own schedule: allow it minutes.
            await WaitUntilAsync(() => spooler.Observe(_printer, ourJob.JobId, ours).State == SpoolJobState.Gone, "our job leaves the queue", RetryWait);
            await WaitUntilAsync(
                () => receiver.Received.Any(r => r.Contains(ours, StringComparison.Ordinal))
                      && receiver.Received.Any(r => r.Contains(foreign, StringComparison.Ordinal)),
                "both jobs reach the port",
                RetryWait);
            Assert.Single(receiver.Received, r => r.Contains(ours, StringComparison.Ordinal));
            Assert.Single(receiver.Received, r => r.Contains(foreign, StringComparison.Ordinal));
            Log("listener started: both jobs sent once each, ours left the queue; the other document's job was printed as Windows would, never deleted or changed");
        }

        // ---- 6. A job that cannot be created (the printer is gone): no job id, "not created".
        PowerShell($"Remove-Printer -Name '{_printer}' -ErrorAction Stop");
        await WaitUntilAsync(() => spooler.GetAvailability(_printer).State == PrinterAvailabilityState.NotFound, "the printer is removed");
        var notCreated = spooler.Submit(_printer, DocumentName("3"), [0x41], static _ => throw new InvalidOperationException("No job may be created."));
        Assert.Equal(SpoolSubmitOutcome.NotCreated, notCreated.Outcome);
        Assert.Equal(SpoolJobState.QueueMissing, spooler.Observe(_printer, ourJob.JobId, ours).State);
        Log($"removed printer: submission NotCreated (Win32 error {notCreated.Win32Error}), its old job id reads QueueMissing");
    }

    private string DocumentName(string number) => $"Wasla Receipt {_printer[^8..]}0000 {number}/1";

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine("evidence: " + line);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? Wait);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"Timed out waiting until {what}.");
            await Task.Delay(100, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (int ExitCode, string Output) PowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120_000);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>A loopback listener on a given port that keeps what each connection sends, as a network receipt printer.</summary>
    private sealed class RawReceiver : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _received = [];
        private readonly Task _accepting;

        public RawReceiver(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _accepting = AcceptAsync();
        }

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
}
