using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using static Wasla.PrintBridge.Tests.PrintBridgeStopRaceTests;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-216: what Print Bridge reports for a receipt, decided from what Windows can actually tell. The real engine and its
/// journal run against the fake server of <see cref="PrintBridgeStopRaceTests"/> (the server's job contract) and a
/// Windows print queue under the test's control (<see cref="ScriptedPrintSpooler"/>). Before WAS-216 the engine reported
/// Printed as soon as the spooler accepted the data, and reported any exception during submission as not printed.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintOutcomeTrackingTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-outcome-tests");

    public void Dispose() => _dataRoot.Dispose();

    // ---- Printer availability -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PrinterAvailabilityState.Offline, PrinterHealthStatus.Offline)]
    [InlineData(PrinterAvailabilityState.Paused, PrinterHealthStatus.Paused)]
    [InlineData(PrinterAvailabilityState.Error, PrinterHealthStatus.Error)]
    public async Task APrinterWindowsReportsUnavailable_LeavesNewJobsPending_AndTheyPrintOnceWhenItIsReady(
        PrinterAvailabilityState state,
        PrinterHealthStatus shown)
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler { Availability = new PrinterAvailability(state) };
        using var bridge = Bridge.Create(server, spooler);

        bridge.Runtime.Start();
        // Start's own check, then three cycles of the loop.
        await WaitUntilAsync(() => spooler.AvailabilityCalls >= 4);

        Assert.Equal(JobState.Pending, server.State(jobId));
        Assert.Empty(server.Attempted(jobId));
        Assert.Equal(0, spooler.SubmitCalls);
        Assert.Equal(shown, bridge.Runtime.GetStatus().PrinterHealthStatus);

        spooler.Availability = PrinterAvailability.Ready;
        await WaitUntilAsync(() => server.State(jobId) == JobState.Printed);

        Assert.Single(spooler.Submitted);
        Assert.Equal(["mark-printing", "mark-printed"], server.Received(jobId));
        Assert.Equal(PrinterHealthStatus.Ready, bridge.Runtime.GetStatus().PrinterHealthStatus);
    }

    [Fact]
    public async Task APrinterThatBecomesUnavailableAfterTheClaim_IsNeverHandedTheReceipt_AndTheJobCanBePrintedAgain()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        // Ready while the job is Pending (the poll), offline once it is claimed (the check before submission).
        var spooler = new ScriptedPrintSpooler
        {
            AvailabilityProvider = () => server.State(jobId) == JobState.Printing
                ? new PrinterAvailability(PrinterAvailabilityState.Offline, Attributes: 0x400)
                : PrinterAvailability.Ready
        };
        using var bridge = Bridge.Create(server, spooler);

        bridge.Runtime.Start();
        await WaitUntilAsync(() => server.State(jobId) == JobState.Failed);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(0, spooler.SubmitCalls);
        Assert.Equal(["mark-printing", "mark-failed"], server.Received(jobId));
        var record = bridge.History.FindByJobId(jobId);
        Assert.Equal(LocalPrintJobStatus.Failed, record?.Status);
        Assert.Equal(LocalPrintJobFailureCategory.Printer, LocalPrintJobFailure.Classify(record?.ErrorMessage));
        Assert.Empty(Journal().Entries);
    }

    [Fact]
    public async Task AStatusRequest_DoesNotWaitForASpoolerThatDoesNotAnswer_NorHoldTheEngine()
    {
        var server = new FakePrintServer();
        var hold = new Gate();
        var spooler = new ScriptedPrintSpooler { HoldAvailability = hold };
        using var bridge = Bridge.Create(server, spooler);
        bridge.Runtime.AvailabilityStatusWait = TimeSpan.FromMilliseconds(100);

        var started = DateTime.UtcNow;
        var status = await Task.Run(bridge.Runtime.GetStatus, Ct).WaitAsync(Wait, Ct);
        var change = await Task.Run(bridge.Runtime.TryBeginConnectionChange, Ct).WaitAsync(Wait, Ct);

        // No answer yet: shown as a problem instead of waiting for the spooler, and the engine's lock was never held.
        Assert.Equal(PrinterHealthStatus.Error, status.PrinterHealthStatus);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.NotNull(change);
        change!.Dispose();

        hold.Open();
        await WaitUntilAsync(() => bridge.Runtime.GetStatus().PrinterHealthStatus == PrinterHealthStatus.Ready);
    }

    // ---- Submission ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReceiptWindowsNeverCreatedAJobFor_IsReportedFailed_SoWaslaCanPrintItAgain()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler
        {
            SubmitScript = _ => SpoolSubmitResult.NotCreated("Unable to start print job on 'POS' (Win32 error 1804).", 1804)
        };
        using var bridge = Bridge.Create(server, spooler);

        bridge.Runtime.Start();
        await WaitUntilAsync(() => server.State(jobId) == JobState.Failed);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Empty(spooler.Submitted);
        Assert.Equal(["mark-printing", "mark-failed"], server.Received(jobId));
        Assert.Empty(Journal().Entries);
    }

    [Theory]
    // A write that failed after some bytes reached Windows; the job was aborted.
    [InlineData(120, true, true)]
    // StartPagePrinter or a first write failed and AbortPrinter failed too: the unfinished job is still there.
    [InlineData(0, false, true)]
    // Every byte written, EndDocPrinter failed: Windows may still send what it has.
    [InlineData(512, false, true)]
    // Nothing written and Windows deleted the job: no data exists, it cannot print.
    [InlineData(0, true, false)]
    public async Task AJobThatFailedAfterWindowsCreatedIt_IsNeverReportedFailed_UnlessNoDataCanExist(
        long bytesWritten,
        bool aborted,
        bool uncertain)
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler
        {
            SubmitScript = _ => SpoolSubmitResult.Incomplete(0, bytesWritten, aborted, "Unable to write to printer (Win32 error 1167).", 1167)
        };
        using var bridge = Bridge.Create(server, spooler);

        bridge.Runtime.Start();
        await WaitUntilAsync(() => uncertain
            ? bridge.History.FindByJobId(jobId)?.Status == LocalPrintJobStatus.Uncertain
            : server.State(jobId) == JobState.Failed);
        await server.NextPoll().WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(1, spooler.SubmitCalls);
        if (uncertain)
        {
            // Before WAS-216 this exception was reported as mark-failed, which invited a second receipt.
            Assert.Equal(JobState.Printing, server.State(jobId));
            Assert.Equal(["mark-printing"], server.Attempted(jobId));
            var entry = Assert.Single(Journal().Entries);
            Assert.Equal(PrintJournalStage.Uncertain, entry.Stage);
            Assert.Equal(PrintCopyOutcome.MaybePrinted, Assert.Single(entry.Submission!.Copies).Outcome);
            Assert.Equal(1, bridge.Runtime.GetStatus().UncertainJobsCount);
        }
        else
        {
            Assert.Equal(["mark-printing", "mark-failed"], server.Received(jobId));
            Assert.Empty(Journal().Entries);
        }
    }

    [Theory]
    [InlineData(SpoolSubmitOutcome.NotCreated)]
    [InlineData(SpoolSubmitOutcome.Incomplete)]
    public async Task ASecondCopyThatFailsAfterTheFirstWasSent_IsUncertain_NeverReportedFailedOrPrintedAgain(SpoolSubmitOutcome secondCopy)
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 2);
        var spooler = new ScriptedPrintSpooler
        {
            SubmitScript = call => call == 2
                ? secondCopy == SpoolSubmitOutcome.NotCreated
                    ? SpoolSubmitResult.NotCreated("Unable to start print job on 'POS' (Win32 error 1804).", 1804)
                    : SpoolSubmitResult.Incomplete(0, 0, aborted: true, "Unable to write to printer: no progress after 0 of 64 bytes.")
                : null
        };
        using var bridge = Bridge.Create(server, spooler);

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.Status == LocalPrintJobStatus.Uncertain);
        await server.NextPoll().WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        // The first copy left Windows; printing the receipt again would repeat it, and "printed" would hide the missing one.
        Assert.Equal(2, spooler.SubmitCalls);
        Assert.Equal(JobState.Printing, server.State(jobId));
        Assert.Equal(["mark-printing"], server.Attempted(jobId));
        var copies = Assert.Single(Journal().Entries).Submission!.Copies.OrderBy(c => c.Number).ToList();
        Assert.Equal([PrintCopyOutcome.Sent, PrintCopyOutcome.NotSubmitted], copies.Select(c => c.Outcome!.Value));
    }

    // ---- The Windows queue ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AReceiptStillInTheWindowsQueue_StaysPrinting_AndIsReportedOnceWhenWindowsFinishesIt()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler { KeepJobsQueued = true };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.StatusNote == PrintBridgeRuntime.InWindowsQueueNote);
        await server.NextPoll().WaitAsync(Wait, Ct);
        await server.NextPoll().WaitAsync(Wait, Ct);

        // Before WAS-216 it was reported printed the moment Windows accepted it, while it was still waiting.
        Assert.Equal(JobState.Printing, server.State(jobId));
        Assert.Equal(["mark-printing"], server.Attempted(jobId));
        Assert.Equal(LocalPrintJobStatus.Printing, bridge.History.FindByJobId(jobId)?.Status);
        var queued = Assert.Single(Journal().Entries);
        Assert.Equal(PrintJournalStage.Submitting, queued.Stage);
        Assert.True(Assert.Single(queued.Submission!.Copies).Spooled);

        spooler.RemoveAll();
        await WaitUntilAsync(() => server.State(jobId) == JobState.Printed);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(1, spooler.SubmitCalls);
        Assert.Equal(["mark-printing", "mark-printed"], server.Received(jobId));
        var record = bridge.History.FindByJobId(jobId);
        Assert.Equal(LocalPrintJobStatus.Printed, record?.Status);
        Assert.Equal(PrintBridgeRuntime.SentToPrinterNote, record?.StatusNote);
        Assert.Empty(Journal().Entries);
    }

    [Theory]
    [InlineData(SpoolJobObservation.JobPrinted)]
    [InlineData(SpoolJobObservation.JobComplete)]
    public async Task WindowsReportingAJobPrintedOrComplete_IsRecordedAsSentToThePrinter_NotAsPaperOutput(uint status)
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        // Kept in the queue with the status Windows gives a finished job (a printer that keeps printed documents).
        var spooler = new ScriptedPrintSpooler { KeepJobsQueued = true, QueuedStatus = status };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => server.State(jobId) == JobState.Printed);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(["mark-printing", "mark-printed"], server.Received(jobId));
        Assert.Equal(PrintBridgeRuntime.SentToPrinterNote, bridge.History.FindByJobId(jobId)?.StatusNote);
        // The finished job is the user's to keep: Print Bridge never deletes or changes queue jobs.
        Assert.Single(spooler.QueuedJobIds());
    }

    [Theory]
    [InlineData(SpoolJobObservation.JobOffline)]
    [InlineData(SpoolJobObservation.JobError | SpoolJobObservation.JobPaperOut)]
    [InlineData(SpoolJobObservation.JobDeleting)]
    public async Task AJobThatLeavesTheQueueAfterBeingStuckOrDeleted_IsUncertain_NeverReportedOrPrintedAgain(uint status)
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler { KeepJobsQueued = true, QueuedStatus = status };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.StatusNote == PrintBridgeRuntime.InWindowsQueueNote);

        // Removed from the queue between two looks: finished, or deleted by someone. Windows cannot tell which.
        spooler.RemoveAll();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.Status == LocalPrintJobStatus.Uncertain);
        await server.NextPoll().WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(1, spooler.SubmitCalls);
        Assert.Equal(JobState.Printing, server.State(jobId));
        Assert.Equal(["mark-printing"], server.Attempted(jobId));
        Assert.Equal(PrintJournalStage.Uncertain, Assert.Single(Journal().Entries).Stage);
    }

    [Fact]
    public async Task APrinterQueueRemovedWhileItHeldTheReceipt_LeavesItUncertain()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler { KeepJobsQueued = true };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.StatusNote == PrintBridgeRuntime.InWindowsQueueNote);
        spooler.QueueMissing = true;
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.Status == LocalPrintJobStatus.Uncertain);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(["mark-printing"], server.Attempted(jobId));
        Assert.Equal(PrintJournalStage.Uncertain, Assert.Single(Journal().Entries).Stage);
    }

    [Fact]
    public async Task AQueuedReceipt_SurvivesARestartOfPrintBridge_AndIsReportedOnceItLeavesTheQueue_WithoutPrintingAgain()
    {
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        var copy = SpooledCopy(windowsJobId: 7);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, Submission(copy));
        var spooler = new ScriptedPrintSpooler();
        spooler.AddQueuedJob(7, copy.DocumentName);

        using var restarted = Bridge.Create(server, spooler);
        await restarted.StartAndSettleAsync(server);

        Assert.Equal(JobState.Printing, server.State(jobId));
        Assert.Empty(server.Attempted(jobId));
        Assert.Equal(PrintBridgeRuntime.InWindowsQueueNote, restarted.History.FindByJobId(jobId)?.StatusNote);

        spooler.Remove(7);
        await WaitUntilAsync(() => server.State(jobId) == JobState.Printed);

        Assert.Equal(0, spooler.SubmitCalls);
        Assert.Equal(["mark-printed"], server.Received(jobId));
        Assert.Equal(LocalPrintJobStatus.Printed, restarted.History.FindByJobId(jobId)?.Status);
    }

    [Fact]
    public async Task AJobIdReusedByAnotherDocument_IsNeverMistakenForTheReceipt_NorTouched()
    {
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        var copy = SpooledCopy(windowsJobId: 7);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, Submission(copy));
        var spooler = new ScriptedPrintSpooler();
        // While Print Bridge was stopped the receipt's job finished and Windows gave id 7 to someone else's document.
        spooler.ReuseId(7, "Quarterly report.pdf");

        using var restarted = Bridge.Create(server, spooler);
        await restarted.StartAndSettleAsync(server);

        Assert.Equal(JobState.Printed, server.State(jobId));
        Assert.Equal(["mark-printed"], server.Received(jobId));
        Assert.Equal([7], spooler.QueuedJobIds());
        Assert.Equal(0, spooler.SubmitCalls);
    }

    [Fact]
    public async Task APrintedReportThatFailsAfterWindowsFinishedTheReceipt_IsRepeatedWithoutPrintingAgain()
    {
        var server = new FakePrintServer { FailPrintedReports = 2 };
        var jobId = server.AddJob(copies: 1);
        var spooler = new ScriptedPrintSpooler { KeepJobsQueued = true };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));
        bridge.Runtime.JournalRetryInterval = TimeSpan.Zero;

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.StatusNote == PrintBridgeRuntime.InWindowsQueueNote);
        spooler.RemoveAll();
        await WaitUntilAsync(() => server.State(jobId) == JobState.Printed);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(1, spooler.SubmitCalls);
        Assert.Equal(["mark-printing", "mark-printed", "mark-printed", "mark-printed"], server.Attempted(jobId));
        Assert.DoesNotContain("mark-failed", server.Attempted(jobId));
        Assert.Empty(Journal().Entries);
    }

    // ---- A crash while a copy was handed to Windows ------------------------------------------------------------

    [Fact]
    public async Task ACopyWhoseJobIdWasNeverRecorded_IsFoundByItsDocumentName_AndNotPrintedCannotBeAnsweredWhileItCanStillPrint()
    {
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        // The process ended between StartDocPrinter and recording the id: the journal has only the copy's document name.
        var copy = new PrintJournalCopy(1, PrintJournalSubmission.DocumentName("a1b2c3d4e5f6", 1, 1), DateTime.UtcNow);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, Submission(copy));
        var spooler = new ScriptedPrintSpooler();
        spooler.AddQueuedJob(55, copy.DocumentName);

        using var restarted = Bridge.Create(server, spooler);
        await restarted.StartAndSettleAsync(server);

        Assert.Equal(0, spooler.SubmitCalls);
        Assert.Empty(server.Attempted(jobId));
        var entry = Assert.Single(Journal().Entries);
        Assert.Equal(PrintJournalStage.Uncertain, entry.Stage);
        Assert.Equal(55, Assert.Single(entry.Submission!.Copies).WindowsJobId);
        Assert.Equal(LocalPrintJobStatus.Uncertain, restarted.History.FindByJobId(jobId)?.Status);

        // "It did not print" would let Wasla print it again while this job can still come out: refused until it is gone.
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(
            () => restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct));
        Assert.Equal("History.Uncertain.StillQueued", refused.ResourceKey);
        Assert.Empty(server.Attempted(jobId));

        spooler.Remove(55);
        await restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct);

        Assert.Equal(["mark-failed"], server.Received(jobId));
        Assert.Equal(JobState.Failed, server.State(jobId));
        Assert.Empty(Journal().Entries);
    }

    [Fact]
    public async Task ACopyInterruptedMidWrite_IsUncertain_AndItPrintedIsAcceptedEvenWhileItsJobIsQueued()
    {
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        var copy = new PrintJournalCopy(1, PrintJournalSubmission.DocumentName("0f0e0d0c0b0a", 1, 1), DateTime.UtcNow, WindowsJobId: 9);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, Submission(copy));
        var spooler = new ScriptedPrintSpooler();
        spooler.AddQueuedJob(9, copy.DocumentName);

        using var restarted = Bridge.Create(server, spooler);
        await restarted.StartAndSettleAsync(server);
        Assert.Equal(PrintJournalStage.Uncertain, Assert.Single(Journal().Entries).Stage);

        // "It printed" creates no second receipt, so it does not wait for the queue.
        await restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: true, Ct);

        Assert.Equal(["mark-printed"], server.Received(jobId));
        Assert.Equal(0, spooler.SubmitCalls);
        Assert.Empty(Journal().Entries);
    }

    [Fact]
    public async Task ACopyInterruptedBeforeItsJobWasFound_IsUncertain_AndCanBeAnsweredNotPrinted()
    {
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        var copy = new PrintJournalCopy(1, PrintJournalSubmission.DocumentName("123456abcdef", 1, 1), DateTime.UtcNow);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, Submission(copy));
        var spooler = new ScriptedPrintSpooler();

        using var restarted = Bridge.Create(server, spooler);
        await restarted.StartAndSettleAsync(server);

        // It may have been created, printed and left before the restart: unknown, never "not printed" on its own.
        Assert.Empty(server.Attempted(jobId));
        Assert.Equal(PrintJournalStage.Uncertain, Assert.Single(Journal().Entries).Stage);

        await restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct);
        Assert.Equal(["mark-failed"], server.Received(jobId));
    }

    [Fact]
    public async Task NotPrintedIsRefused_WhileACopyIsStillQueued_OrTheQueueCannotBeRead()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 2);
        // Copy 1 waits in the queue; copy 2 fails after part of it was written: uncertain at once.
        var spooler = new ScriptedPrintSpooler
        {
            KeepJobsQueued = true,
            SubmitScript = call => call == 2 ? SpoolSubmitResult.Incomplete(0, 40, aborted: false, "Unable to write to printer (Win32 error 1167).") : null
        };
        using var bridge = QuickTracking(Bridge.Create(server, spooler));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.Status == LocalPrintJobStatus.Uncertain);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        var stillQueued = await Assert.ThrowsAsync<LocalizedApplicationException>(
            () => bridge.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct));
        Assert.Equal("History.Uncertain.StillQueued", stillQueued.ResourceKey);

        spooler.RemoveAll();
        spooler.QueueUnreadable = true;
        var unreadable = await Assert.ThrowsAsync<LocalizedApplicationException>(
            () => bridge.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct));
        Assert.Equal("History.Uncertain.QueueNotChecked", unreadable.ResourceKey);
        // While refused the server keeps the job in Printing, which also blocks a manual print in Wasla.
        Assert.Equal(JobState.Printing, server.State(jobId));
        Assert.Equal(["mark-printing"], server.Attempted(jobId));

        spooler.QueueUnreadable = false;
        await bridge.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct);
        Assert.Equal(["mark-printing", "mark-failed"], server.Received(jobId));
        Assert.Equal(2, spooler.SubmitCalls);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    /// <summary>A short own-cycle watch, so a job still in the queue is handed to the journal quickly.</summary>
    private static Bridge QuickTracking(Bridge bridge)
    {
        bridge.Runtime.SubmissionTrackingWindow = TimeSpan.FromMilliseconds(300);
        bridge.Runtime.SubmissionTrackingInterval = TimeSpan.FromMilliseconds(20);
        return bridge;
    }

    private static PrintJournalCopy SpooledCopy(int windowsJobId) =>
        new(1, PrintJournalSubmission.DocumentName("feedfacecafe", 1, 1), DateTime.UtcNow, windowsJobId, Spooled: true);

    private static PrintJournalSubmission Submission(PrintJournalCopy copy) =>
        new("POS-58 (test)", copy.DocumentName.Split(' ')[2], 1, [copy]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(25, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
