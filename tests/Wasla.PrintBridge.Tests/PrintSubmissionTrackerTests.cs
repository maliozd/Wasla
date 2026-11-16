using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-216: the rules that turn what Windows reports into a copy's outcome and a receipt's verdict, and a queue's
/// PRINTER_INFO_2 status into its availability. Pure functions; the engine tests run them inside the real runtime.
/// </summary>
public sealed class PrintSubmissionTrackerTests
{
    private static readonly PrintJournalCopy Queued =
        new(1, PrintJournalSubmission.DocumentName("abcdefabcdef", 1, 1), DateTime.UtcNow, WindowsJobId: 12, Spooled: true);

    [Theory]
    [InlineData(0u, 0u, PrinterAvailabilityState.Ready)]
    // Busy, printing, waiting, processing, warming up, toner low, power save, I/O active: printing goes on.
    [InlineData(0x00000200u, 0u, PrinterAvailabilityState.Ready)]
    [InlineData(0x00000400u, 0u, PrinterAvailabilityState.Ready)]
    [InlineData(0x00002000u | 0x00004000u | 0x00010000u, 0u, PrinterAvailabilityState.Ready)]
    [InlineData(0x00020000u | 0x01000000u | 0x00000100u, 0u, PrinterAvailabilityState.Ready)]
    // "Use printer offline" (the USB monitor sets it when the device is unplugged) and offline statuses.
    [InlineData(0u, 0x00000400u, PrinterAvailabilityState.Offline)]
    [InlineData(0x00000080u, 0u, PrinterAvailabilityState.Offline)]
    [InlineData(0x00001000u, 0u, PrinterAvailabilityState.Offline)]
    [InlineData(0x00800000u, 0u, PrinterAvailabilityState.Offline)]
    [InlineData(0x02000000u, 0u, PrinterAvailabilityState.Offline)]
    [InlineData(0x00000001u, 0u, PrinterAvailabilityState.Paused)]
    // Offline wins over paused: the user cannot resume a printer that is not there.
    [InlineData(0x00000001u, 0x00000400u, PrinterAvailabilityState.Offline)]
    [InlineData(0x00000002u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00000008u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00000010u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00000040u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00400000u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00100000u, 0u, PrinterAvailabilityState.Error)]
    [InlineData(0x00000004u, 0u, PrinterAvailabilityState.Error)]
    public void AQueuesStatusAndAttributes_DecideItsAvailability(uint status, uint attributes, PrinterAvailabilityState expected)
    {
        Assert.Equal(expected, PrinterAvailability.FromWindows(status, attributes).State);
    }

    [Theory]
    [InlineData(SpoolSubmitOutcome.Spooled, 300L, false, null)]
    [InlineData(SpoolSubmitOutcome.NotCreated, 0L, false, PrintCopyOutcome.NotSubmitted)]
    [InlineData(SpoolSubmitOutcome.Incomplete, 0L, true, PrintCopyOutcome.NotSubmitted)]
    [InlineData(SpoolSubmitOutcome.Incomplete, 0L, false, PrintCopyOutcome.MaybePrinted)]
    [InlineData(SpoolSubmitOutcome.Incomplete, 1L, true, PrintCopyOutcome.MaybePrinted)]
    [InlineData(SpoolSubmitOutcome.Incomplete, 300L, false, PrintCopyOutcome.MaybePrinted)]
    public void ASubmissionsResult_NeverCallsACopyWithPossibleDataNotSubmitted(
        SpoolSubmitOutcome outcome,
        long bytesWritten,
        bool aborted,
        PrintCopyOutcome? expected)
    {
        var started = Queued with { WindowsJobId = null, Spooled = false };
        var result = new SpoolSubmitResult(outcome, outcome == SpoolSubmitOutcome.NotCreated ? 0 : 12, bytesWritten, aborted, "error");

        var copy = PrintSubmissionTracker.AfterSubmit(started, result);

        Assert.Equal(expected, copy.Outcome);
        Assert.Equal(outcome == SpoolSubmitOutcome.Spooled, copy.Spooled);
        Assert.Equal(outcome == SpoolSubmitOutcome.NotCreated ? null : 12, copy.WindowsJobId);
    }

    [Fact]
    public void AJobThatLeavesTheQueueNormally_IsSent_NotProofOfPaper()
    {
        var waiting = PrintSubmissionTracker.Observe(Queued, SpoolJobObservation.InQueue(SpoolJobObservation.JobPrinting), PrinterAvailability.Ready);
        Assert.Null(waiting.Outcome);
        Assert.True(waiting.SeenPrinting);

        Assert.Equal(PrintCopyOutcome.Sent, PrintSubmissionTracker.Observe(waiting, SpoolJobObservation.Gone, PrinterAvailability.Ready).Outcome);
        Assert.Equal(PrintCopyOutcome.Sent, PrintSubmissionTracker.Observe(Queued, SpoolJobObservation.Gone, PrinterAvailability.Ready).Outcome);
    }

    [Fact]
    public void AStuckJobThatLeavesWithoutBeingSeenPrinting_OrADeletedOne_MayHavePrinted()
    {
        var stuck = PrintSubmissionTracker.Observe(Queued, SpoolJobObservation.InQueue(0), new PrinterAvailability(PrinterAvailabilityState.Offline));
        Assert.True(stuck.SeenProblem);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, PrintSubmissionTracker.Observe(stuck, SpoolJobObservation.Gone, PrinterAvailability.Ready).Outcome);

        // Stuck, then seen printing once the printer was back: it left the way a finished job leaves.
        var resumed = PrintSubmissionTracker.Observe(stuck, SpoolJobObservation.InQueue(SpoolJobObservation.JobPrinting), PrinterAvailability.Ready);
        Assert.Equal(PrintCopyOutcome.Sent, PrintSubmissionTracker.Observe(resumed, SpoolJobObservation.Gone, PrinterAvailability.Ready).Outcome);

        var deleting = PrintSubmissionTracker.Observe(Queued, SpoolJobObservation.InQueue(SpoolJobObservation.JobDeleting | SpoolJobObservation.JobPrinting), PrinterAvailability.Ready);
        Assert.Equal(PrintCopyOutcome.MaybePrinted, PrintSubmissionTracker.Observe(deleting, SpoolJobObservation.Gone, PrinterAvailability.Ready).Outcome);
    }

    [Fact]
    public void AnUnreadableQueueTeachesNothing_AndAMissingQueueLeavesTheCopyUnknown()
    {
        Assert.Equal(Queued, PrintSubmissionTracker.Observe(Queued, new SpoolJobObservation(SpoolJobState.Unknown, Win32Error: 1722), null));
        Assert.Equal(
            PrintCopyOutcome.MaybePrinted,
            PrintSubmissionTracker.Observe(Queued, new SpoolJobObservation(SpoolJobState.QueueMissing), null).Outcome);
    }

    [Fact]
    public void ACopyInterruptedByARestart_MayHavePrinted_AndKeepsTheJobFoundByItsName()
    {
        var interrupted = Queued with { WindowsJobId = null, Spooled = false };
        Assert.True(PrintSubmissionTracker.WasInterrupted(interrupted));

        var found = PrintSubmissionTracker.Recover(interrupted, new SpoolJobLookup(SpoolJobLookupState.Found, 77));
        Assert.Equal((77, PrintCopyOutcome.MaybePrinted), (found.WindowsJobId!.Value, found.Outcome!.Value));
        Assert.Equal(PrintCopyOutcome.MaybePrinted, PrintSubmissionTracker.Recover(interrupted, null).Outcome);
    }

    [Theory]
    [InlineData(new[] { "Sent", "Sent" }, 2, PrintSubmissionVerdict.Sent)]
    [InlineData(new[] { "Sent", "Waiting" }, 2, PrintSubmissionVerdict.Waiting)]
    [InlineData(new[] { "Sent" }, 2, PrintSubmissionVerdict.Uncertain)]
    [InlineData(new[] { "Sent", "NotSubmitted" }, 2, PrintSubmissionVerdict.Uncertain)]
    [InlineData(new[] { "NotSubmitted" }, 2, PrintSubmissionVerdict.NotPrinted)]
    [InlineData(new[] { "NotSubmitted" }, 1, PrintSubmissionVerdict.NotPrinted)]
    [InlineData(new[] { "Waiting", "MaybePrinted" }, 2, PrintSubmissionVerdict.Uncertain)]
    [InlineData(new[] { "MaybePrinted" }, 1, PrintSubmissionVerdict.Uncertain)]
    public void TheCopiesTogether_DecideTheReceipt(string[] outcomes, int copyCount, PrintSubmissionVerdict expected)
    {
        var copies = outcomes
            .Select((o, i) => Queued with
            {
                Number = i + 1,
                Outcome = o == "Waiting" ? null : Enum.Parse<PrintCopyOutcome>(o)
            })
            .ToList();

        Assert.Equal(expected, PrintSubmissionTracker.Decide(new PrintJournalSubmission("POS", "abcdefabcdef", copyCount, copies)));
    }
}
