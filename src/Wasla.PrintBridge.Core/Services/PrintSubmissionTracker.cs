using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

/// <summary>What Windows' evidence says about a whole receipt (all copies) so far.</summary>
public enum PrintSubmissionVerdict
{
    /// <summary>At least one copy is still in the Windows queue and none may have printed out of order: keep watching.</summary>
    Waiting,

    /// <summary>Windows finished sending every copy to the printer. Reported as printed; not proof that paper came out.</summary>
    Sent,

    /// <summary>No copy's data is in any Windows job: nothing can print. Reported as failed, so Wasla can print it again.</summary>
    NotPrinted,

    /// <summary>Some output may exist but not all of it is accounted for: the user's check decides.</summary>
    Uncertain
}

/// <summary>
/// The rules that turn what the spooler reports into a copy's outcome and a receipt's verdict (WAS-216). They never
/// treat Windows as physical proof: a copy that left the queue normally is "sent", never "on paper". Once any data may
/// have reached the printer, a copy is never called not submitted.
/// </summary>
public static class PrintSubmissionTracker
{
    /// <summary>A copy whose job is finished in the spooler and still has to leave the queue.</summary>
    public static bool NeedsObservation(PrintJournalCopy copy) =>
        copy.Outcome is null && copy.Spooled && copy.WindowsJobId is not null;

    /// <summary>A copy the process ended in the middle of (no outcome, not finished in the spooler): only a restart finds one.</summary>
    public static bool WasInterrupted(PrintJournalCopy copy) => copy.Outcome is null && !copy.Spooled;

    /// <summary>The copy after its submission returned.</summary>
    public static PrintJournalCopy AfterSubmit(PrintJournalCopy copy, SpoolSubmitResult result) =>
        result.Outcome switch
        {
            SpoolSubmitOutcome.Spooled => copy with { WindowsJobId = result.JobId, Spooled = true },
            SpoolSubmitOutcome.NotCreated => copy with { WindowsJobId = null, Outcome = PrintCopyOutcome.NotSubmitted },
            // A job that failed after it existed holds no data only when Windows took none (no byte reported, no failed or
            // implausible write that may have passed data on) and deleted it.
            _ => copy with
            {
                WindowsJobId = result.JobId,
                Outcome = !result.MayHaveData && result.Aborted ? PrintCopyOutcome.NotSubmitted : PrintCopyOutcome.MaybePrinted
            }
        };

    /// <summary>
    /// The copy after one look at its job. <paramref name="printer"/> is the queue's state at the same moment: a job that
    /// waits while Windows reports the printer unavailable is stuck, not waiting its turn.
    /// </summary>
    public static PrintJournalCopy Observe(PrintJournalCopy copy, SpoolJobObservation observation, PrinterAvailability? printer)
    {
        if (!NeedsObservation(copy))
            return copy;

        switch (observation.State)
        {
            case SpoolJobState.InQueue:
            {
                var status = observation.Status;
                // JOB_STATUS_PRINTED or _COMPLETE: Windows finished sending the data (to the port, not to paper).
                if ((status & (SpoolJobObservation.JobPrinted | SpoolJobObservation.JobComplete)) != 0)
                    return copy with { SeenPrinting = true, Outcome = PrintCopyOutcome.Sent };

                return copy with
                {
                    SeenPrinting = copy.SeenPrinting || (status & SpoolJobObservation.JobPrinting) != 0,
                    SeenDeleting = copy.SeenDeleting || (status & (SpoolJobObservation.JobDeleting | SpoolJobObservation.JobDeleted)) != 0,
                    SeenProblem = copy.SeenProblem
                                  || (status & SpoolJobObservation.JobProblems) != 0
                                  || printer is { IsReady: false }
                };
            }

            case SpoolJobState.Gone:
                // Leaving the queue is how a finished job ends, and also how a deleted one ends. A job seen being deleted,
                // or one that was stuck and left without being seen printing, may have been removed: unknown.
                return copy with
                {
                    Outcome = copy.SeenDeleting || (copy.SeenProblem && !copy.SeenPrinting)
                        ? PrintCopyOutcome.MaybePrinted
                        : PrintCopyOutcome.Sent
                };

            case SpoolJobState.QueueMissing:
                // The printer was removed with its jobs; part of it may have printed before that.
                return copy with { Outcome = PrintCopyOutcome.MaybePrinted };

            default:
                // The queue could not be read: nothing is learned, the copy keeps waiting.
                return copy;
        }
    }

    /// <summary>
    /// A copy found interrupted after a restart: its job may exist (found by its unique document name, in case the process
    /// ended between creating the job and recording its id) and part of its data may have printed.
    /// </summary>
    public static PrintJournalCopy Recover(PrintJournalCopy copy, SpoolJobLookup? lookup) =>
        copy with
        {
            WindowsJobId = lookup is { State: SpoolJobLookupState.Found } ? lookup.JobId : copy.WindowsJobId,
            Outcome = PrintCopyOutcome.MaybePrinted
        };

    public static PrintSubmissionVerdict Decide(PrintJournalSubmission submission)
    {
        var copies = submission.Copies;
        if (copies.Any(c => c.Outcome == PrintCopyOutcome.MaybePrinted))
            return PrintSubmissionVerdict.Uncertain;
        if (copies.Count == 0 || copies.Any(c => c.Outcome is null))
            return PrintSubmissionVerdict.Waiting;

        var sent = copies.Count(c => c.Outcome == PrintCopyOutcome.Sent);
        if (sent == submission.CopyCount)
            return PrintSubmissionVerdict.Sent;

        // Some copies sent and the rest never submitted: printing it again would repeat the sent ones.
        return sent == 0 ? PrintSubmissionVerdict.NotPrinted : PrintSubmissionVerdict.Uncertain;
    }
}
