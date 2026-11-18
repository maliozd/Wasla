using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using static Wasla.PrintBridge.Tests.PrintBridgeStopRaceTests;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-216 review: <see cref="WindowsPrintSpooler"/> itself, against scripted winspool results (<see cref="ScriptedWinspool"/>).
/// These cover what the real spooler rarely produces on demand: a failed enumeration, a write that fails after Windows may
/// have taken data, a queue that changes between the size probe and the read. The runner-only
/// <c>Service.WindowsPrintSpoolerTests</c> cover the same class against the real spooler.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class WindowsPrintSpoolerNativeResultTests : IDisposable
{
    private const string Printer = "POS-58 (scripted)";
    private const int MaxEnumeratedJobs = 4096;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static readonly byte[] Receipt = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-native-tests");

    public void Dispose() => _dataRoot.Dispose();

    // ---- Find: looking for a copy by its document name -----------------------------------------------------------

    [Theory]
    [InlineData(ScriptedWinspool.ErrorAccessDenied)]
    [InlineData(ScriptedWinspool.ErrorRpcUnavailable)]
    [InlineData(ScriptedWinspool.ErrorInsufficientBuffer)]
    public void AQueueThatCannotBeEnumerated_IsUnknown_NeverAnEmptyQueue(int error)
    {
        using var winspool = new ScriptedWinspool { EnumProbeError = error };
        winspool.AddJob(55, Document());

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        // Before the review the probe's result was ignored and "nothing needed" read as an empty queue: NotFound, which
        // let "It did not print" through while the copy could still be in the queue.
        Assert.Equal(SpoolJobLookupState.Unknown, lookup.State);
        Assert.Equal(error, lookup.Win32Error);
        Assert.Equal("ClosePrinter", winspool.Calls[^1]);
    }

    [Fact]
    public void AnEmptyQueueThatWasRead_IsNotFound()
    {
        using var winspool = new ScriptedWinspool();

        Assert.Equal(SpoolJobLookupState.NotFound, new WindowsPrintSpooler(winspool).Find(Printer, Document()).State);
    }

    [Fact]
    public void AQueueThatWasRead_FindsTheCopyByItsDocumentName_OrReportsItAbsent()
    {
        using var winspool = new ScriptedWinspool();
        winspool.AddOtherJobs(3, firstId: 10);
        winspool.AddJob(55, Document());
        var spooler = new WindowsPrintSpooler(winspool);

        var found = spooler.Find(Printer, Document());
        var absent = spooler.Find(Printer, Document("fedcba987654"));

        Assert.Equal(new SpoolJobLookup(SpoolJobLookupState.Found, 55), found);
        Assert.Equal(SpoolJobLookupState.NotFound, absent.State);
    }

    [Fact]
    public void AReadThatFailsAfterTheSizeProbe_IsUnknown()
    {
        using var winspool = new ScriptedWinspool { EnumDataError = ScriptedWinspool.ErrorRpcUnavailable };
        winspool.AddJob(10, "Someone else's document");

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        Assert.Equal(SpoolJobLookupState.Unknown, lookup.State);
        Assert.Equal(ScriptedWinspool.ErrorRpcUnavailable, lookup.Win32Error);
    }

    [Fact]
    public void AQueueThatGrowsBetweenTheProbeAndTheRead_IsReadAgain_NotGivenUp()
    {
        using var winspool = new ScriptedWinspool();
        winspool.AddJob(10, "Someone else's document");
        winspool.BeforeEnumData = call =>
        {
            if (call == 1)
                winspool.AddJob(55, Document());
        };

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        Assert.Equal(new SpoolJobLookup(SpoolJobLookupState.Found, 55), lookup);
    }

    [Fact]
    public void AQueueThatChangesOnEveryRead_IsUnknown_AfterABoundedNumberOfTries()
    {
        using var winspool = new ScriptedWinspool();
        winspool.AddJob(10, "Someone else's document");
        var next = 100;
        winspool.BeforeEnumData = _ => winspool.AddJob(next++, "Someone else's document");

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        Assert.Equal(SpoolJobLookupState.Unknown, lookup.State);
        Assert.Equal(ScriptedWinspool.ErrorInsufficientBuffer, lookup.Win32Error);
        Assert.InRange(next - 100, 1, 5);
    }

    [Fact]
    public void AQueueWithMoreJobsThanOneReadReturns_IsUnknown_WhenTheCopyIsNotAmongThem()
    {
        using var winspool = new ScriptedWinspool();
        winspool.AddOtherJobs(MaxEnumeratedJobs, firstId: 1000);
        winspool.AddJob(55, Document());

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        // The copy may be past the jobs Windows returned: not seen is not absent.
        Assert.Equal(SpoolJobLookupState.Unknown, lookup.State);
    }

    [Theory]
    [InlineData(ScriptedWinspool.ErrorInvalidPrinterName, SpoolJobLookupState.QueueMissing)]
    [InlineData(ScriptedWinspool.ErrorAccessDenied, SpoolJobLookupState.Unknown)]
    public void AQueueThatCannotBeOpened_IsMissingOnlyWhenWindowsSaysSo(int error, SpoolJobLookupState expected)
    {
        using var winspool = new ScriptedWinspool { OpenError = error };

        var lookup = new WindowsPrintSpooler(winspool).Find(Printer, Document());

        Assert.Equal(expected, lookup.State);
        Assert.Equal(error, lookup.Win32Error);
    }

    // ---- Submit: what a failed or partial write leaves behind ----------------------------------------------------

    [Theory]
    // Windows reported nothing taken by the failed call, then deleted the job: before the review this was "not
    // submitted", reported failed and printed again, though the call may have passed data on before it failed.
    [InlineData(0)]
    // Windows reported part of the data taken by the call that failed: before the review that part was dropped.
    [InlineData(24)]
    public void AWriteThatFails_LeavesTheCopyUncertain_EvenWhenWindowsDeletedTheJob(int reportedByTheFailedCall)
    {
        using var winspool = new ScriptedWinspool
        {
            WriteScript = (_, _) => (false, reportedByTheFailedCall, ScriptedWinspool.ErrorDeviceNotConnected)
        };

        var result = Submit(winspool);

        Assert.Equal(SpoolSubmitOutcome.Incomplete, result.Outcome);
        Assert.Equal(7, result.JobId);
        Assert.True(result.Aborted);
        Assert.Equal(reportedByTheFailedCall, result.BytesWritten);
        Assert.Equal(ScriptedWinspool.ErrorDeviceNotConnected, result.Win32Error);
        Assert.Contains("Win32 error 1167", result.Error, StringComparison.Ordinal);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, AfterSubmit(result));
    }

    [Fact]
    public void AWriteThatFailsAfterEarlierWritesWereTaken_CountsEveryByteWindowsReported()
    {
        using var winspool = new ScriptedWinspool
        {
            WriteScript = (call, offered) => call == 1 ? (true, 20, 0) : (false, 5, ScriptedWinspool.ErrorDeviceNotConnected)
        };

        var result = Submit(winspool);

        Assert.Equal(25, result.BytesWritten);
        Assert.Equal(Receipt[..25], winspool.Received);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, AfterSubmit(result));
    }

    [Fact]
    public void AWriteThatReportsMoreBytesThanItWasGiven_IsUncertain()
    {
        using var winspool = new ScriptedWinspool { WriteScript = (_, offered) => (true, offered + 1, 0) };

        var result = Submit(winspool);

        Assert.Equal(SpoolSubmitOutcome.Incomplete, result.Outcome);
        Assert.True(result.Aborted);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, AfterSubmit(result));
    }

    [Fact]
    public void PartialWrites_Continue_UntilEveryByteIsTaken()
    {
        using var winspool = new ScriptedWinspool { WriteScript = (_, offered) => (true, Math.Min(10, offered), 0) };

        var result = Submit(winspool);

        Assert.Equal(SpoolSubmitResult.Spooled(7, Receipt.Length), result);
        Assert.Equal(Receipt, winspool.Received);
        Assert.Equal(7, winspool.Calls.Count(c => c == "WritePrinter"));
        Assert.Equal(0, winspool.AbortCalls);
    }

    [Fact]
    public void WritesThatSucceedButTakeNothing_EndTheJob_AndWithNothingTakenAndTheJobDeleted_NothingCanPrint()
    {
        using var winspool = new ScriptedWinspool { WriteScript = (_, _) => (true, 0, 0) };

        var result = Submit(winspool);

        // Windows answered every call and reported that it took no byte; then it deleted the job. No data exists.
        Assert.Equal(SpoolSubmitOutcome.Incomplete, result.Outcome);
        Assert.Equal(0, result.BytesWritten);
        Assert.Equal(3, winspool.Calls.Count(c => c == "WritePrinter"));
        Assert.Equal(PrintCopyOutcome.NotSubmitted, AfterSubmit(result));
    }

    [Fact]
    public void WritesThatStopTakingDataAfterSomeWasTaken_AreUncertain()
    {
        using var winspool = new ScriptedWinspool { WriteScript = (call, _) => call == 1 ? (true, 16, 0) : (true, 0, 0) };

        var result = Submit(winspool);

        Assert.Equal(16, result.BytesWritten);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, AfterSubmit(result));
    }

    [Theory]
    // StartPagePrinter failed: no write was attempted. With the job deleted no data exists; without it, it is unknown.
    [InlineData(true, PrintCopyOutcome.NotSubmitted)]
    [InlineData(false, PrintCopyOutcome.MaybePrinted)]
    public void APageThatCannotStart_WritesNothing(bool abortSucceeds, PrintCopyOutcome expected)
    {
        using var winspool = new ScriptedWinspool { StartPageError = ScriptedWinspool.ErrorDeviceNotConnected, AbortSucceeds = abortSucceeds };

        var result = Submit(winspool);

        Assert.DoesNotContain("WritePrinter", winspool.Calls);
        Assert.Equal(ScriptedWinspool.ErrorDeviceNotConnected, result.Win32Error);
        Assert.Equal(expected, AfterSubmit(result));
    }

    [Fact]
    public void AJobThatCannotBeFinishedAfterEveryByteWasTaken_IsUncertain()
    {
        using var winspool = new ScriptedWinspool { EndDocError = ScriptedWinspool.ErrorDeviceNotConnected };

        var result = Submit(winspool);

        Assert.Equal(SpoolSubmitOutcome.Incomplete, result.Outcome);
        Assert.Equal(Receipt.Length, result.BytesWritten);
        Assert.Contains("Unable to finish print job", result.Error, StringComparison.Ordinal);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, AfterSubmit(result));
    }

    [Fact]
    public void TheJobIdIsHandedOver_BeforeAnyByteIsWritten()
    {
        using var winspool = new ScriptedWinspool();

        new WindowsPrintSpooler(winspool).Submit(Printer, Document(), Receipt, id => winspool.Note($"onJobCreated {id}"));

        var calls = winspool.Calls.ToList();
        Assert.True(calls.IndexOf("onJobCreated 7") < calls.IndexOf("WritePrinter"));
        Assert.True(calls.IndexOf("StartDocPrinter") < calls.IndexOf("onJobCreated 7"));
    }

    // ---- Through the engine -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AReceiptWhoseFirstWriteFailed_IsNeverReportedFailed_NorPrintedAgain()
    {
        using var winspool = new ScriptedWinspool
        {
            WriteScript = (_, _) => (false, 0, ScriptedWinspool.ErrorDeviceNotConnected)
        };
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        using var bridge = Bridge.Create(server, new WindowsPrintSpooler(winspool));

        bridge.Runtime.Start();
        await WaitUntilAsync(() => bridge.History.FindByJobId(jobId)?.Status is LocalPrintJobStatus.Uncertain or LocalPrintJobStatus.Failed);
        await server.NextPoll().WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        // Before the review: mark-failed, and Wasla would print a second receipt.
        Assert.Equal(1, winspool.Calls.Count(c => c == "StartDocPrinter"));
        Assert.Equal(LocalPrintJobStatus.Uncertain, bridge.History.FindByJobId(jobId)?.Status);
        Assert.Equal(["mark-printing"], server.Attempted(jobId));
        Assert.Equal(JobState.Printing, server.State(jobId));
        var entry = Assert.Single(Journal().Entries);
        Assert.Equal(PrintJournalStage.Uncertain, entry.Stage);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, Assert.Single(entry.Submission!.Copies).Outcome);
    }

    [Fact]
    public async Task NotPrinted_IsRefused_WhileTheQueueThatMayHoldTheCopyCannotBeEnumerated()
    {
        using var winspool = new ScriptedWinspool { EnumProbeError = ScriptedWinspool.ErrorAccessDenied };
        var server = new FakePrintServer();
        var jobId = server.AddClaimedJob();
        // The process ended between StartDocPrinter and recording the id: only the copy's document name is known.
        var copy = new PrintJournalCopy(1, Document(), DateTime.UtcNow);
        Journal().Record(jobId, PrintJournalStage.Submitting, SavedConnection, new PrintJournalSubmission(Printer, "abcdef012345", 1, [copy]));
        using var restarted = Bridge.Create(server, new WindowsPrintSpooler(winspool));
        await restarted.StartAndSettleAsync(server);
        Assert.Equal(PrintJournalStage.Uncertain, Assert.Single(Journal().Entries).Stage);

        // Before the review the failed enumeration read as an empty queue and "It did not print" was accepted.
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(
            () => restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct));
        Assert.Equal("History.Uncertain.QueueNotChecked", refused.ResourceKey);
        Assert.Empty(server.Attempted(jobId));

        winspool.EnumProbeError = null;
        await restarted.Runtime.ResolveUncertainJobAsync(jobId, printed: false, Ct);

        Assert.Equal(["mark-failed"], server.Received(jobId));
        Assert.DoesNotContain("StartDocPrinter", winspool.Calls);
        await restarted.Runtime.StopAsync().WaitAsync(Wait, Ct);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static string Document(string submissionId = "abcdef012345") => PrintJournalSubmission.DocumentName(submissionId, 1, 1);

    private static SpoolSubmitResult Submit(ScriptedWinspool winspool) =>
        new WindowsPrintSpooler(winspool).Submit(Printer, Document(), Receipt, _ => { });

    private static PrintCopyOutcome? AfterSubmit(SpoolSubmitResult result) =>
        PrintSubmissionTracker.AfterSubmit(new PrintJournalCopy(1, Document(), DateTime.UtcNow), result).Outcome;

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
