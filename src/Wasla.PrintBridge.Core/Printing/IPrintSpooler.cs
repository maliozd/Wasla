namespace Wasla.PrintBridge.Printing;

/// <summary>
/// The Windows print spooler as Print Bridge uses it (WAS-216): what Windows reports about a printer queue, one RAW job
/// per call, and what Windows reports about a job this application created. Every member is a blocking spooler call;
/// callers run them off the IPC and status paths. Nothing here proves that paper came out: Windows only reports how far
/// it got with the data.
/// </summary>
public interface IPrintSpooler
{
    /// <summary>What the queue's attributes and status report now. "Ready" only means Windows reports no problem.</summary>
    PrinterAvailability GetAvailability(string printerName);

    /// <summary>
    /// Creates one RAW job named <paramref name="documentName"/>, writes <paramref name="data"/> and finishes it.
    /// <paramref name="onJobCreated"/> is called with the Windows job id as soon as the job exists, before any byte is
    /// written, so the caller can record it. Never throws once the job exists; the result says how far it got.
    /// </summary>
    SpoolSubmitResult Submit(string printerName, string documentName, byte[] data, Action<int> onJobCreated);

    /// <summary>
    /// What Windows reports about job <paramref name="jobId"/>. A job with that id but another document name is not this
    /// application's job (job ids are reused): it is reported as gone.
    /// </summary>
    SpoolJobObservation Observe(string printerName, int jobId, string documentName);

    /// <summary>Looks for a job named <paramref name="documentName"/> in the queue (the crash between creating a job and recording its id).</summary>
    SpoolJobLookup Find(string printerName, string documentName);
}

/// <summary>What a printer queue reports, as far as Print Bridge acts on it.</summary>
public enum PrinterAvailabilityState
{
    /// <summary>The queue exists and Windows reports no problem. Not proof that the printer works.</summary>
    Ready,

    NotFound,

    /// <summary>Offline, "use printer offline", not available, or its server unknown or offline.</summary>
    Offline,

    /// <summary>Printing is paused in Windows.</summary>
    Paused,

    /// <summary>An error, paper out or jammed, a door open, user intervention, or the queue could not be read.</summary>
    Error
}

/// <summary>A queue's state with the raw Windows status and attributes it was derived from (for logs).</summary>
public sealed record PrinterAvailability(PrinterAvailabilityState State, uint Status = 0, uint Attributes = 0, int Win32Error = 0)
{
    public bool IsReady => State == PrinterAvailabilityState.Ready;

    public static PrinterAvailability Ready { get; } = new(PrinterAvailabilityState.Ready);

    public static PrinterAvailability NotFound { get; } = new(PrinterAvailabilityState.NotFound);

    // PRINTER_INFO_2 Attributes and Status bits (winspool.h).
    internal const uint AttributeWorkOffline = 0x00000400;

    internal const uint StatusPaused = 0x00000001;
    internal const uint StatusError = 0x00000002;
    internal const uint StatusPendingDeletion = 0x00000004;
    internal const uint StatusPaperJam = 0x00000008;
    internal const uint StatusPaperOut = 0x00000010;
    internal const uint StatusPaperProblem = 0x00000040;
    internal const uint StatusOffline = 0x00000080;
    internal const uint StatusOutputBinFull = 0x00000800;
    internal const uint StatusNotAvailable = 0x00001000;
    internal const uint StatusNoToner = 0x00040000;
    internal const uint StatusPagePunt = 0x00080000;
    internal const uint StatusUserIntervention = 0x00100000;
    internal const uint StatusOutOfMemory = 0x00200000;
    internal const uint StatusDoorOpen = 0x00400000;
    internal const uint StatusServerUnknown = 0x00800000;
    internal const uint StatusServerOffline = 0x02000000;

    private const uint OfflineStatuses = StatusOffline | StatusNotAvailable | StatusServerUnknown | StatusServerOffline;

    private const uint ErrorStatuses = StatusError | StatusPendingDeletion | StatusPaperJam | StatusPaperOut | StatusPaperProblem
        | StatusOutputBinFull | StatusNoToner | StatusPagePunt | StatusUserIntervention | StatusOutOfMemory | StatusDoorOpen;

    /// <summary>
    /// Maps a queue's PRINTER_INFO_2 status and attributes. Busy, printing, waiting, warming up, power save, toner low and
    /// similar states do not stop printing and count as ready.
    /// </summary>
    public static PrinterAvailability FromWindows(uint status, uint attributes)
    {
        var state = (attributes & AttributeWorkOffline) != 0 || (status & OfflineStatuses) != 0
            ? PrinterAvailabilityState.Offline
            : (status & StatusPaused) != 0
                ? PrinterAvailabilityState.Paused
                : (status & ErrorStatuses) != 0
                    ? PrinterAvailabilityState.Error
                    : PrinterAvailabilityState.Ready;
        return new PrinterAvailability(state, status, attributes);
    }
}

public enum SpoolSubmitOutcome
{
    /// <summary>The job was created, every byte written and the job finished (EndDocPrinter). Windows still has to send it.</summary>
    Spooled,

    /// <summary>No job was created (OpenPrinter or StartDocPrinter failed). Nothing can print.</summary>
    NotCreated,

    /// <summary>The job was created but a later step failed. Some data may have reached the printer.</summary>
    Incomplete
}

/// <summary>How far one submission got. <see cref="Aborted"/>: Windows accepted AbortPrinter for the unfinished job.</summary>
public sealed record SpoolSubmitResult(
    SpoolSubmitOutcome Outcome,
    int JobId,
    long BytesWritten,
    bool Aborted,
    string? Error,
    int Win32Error = 0)
{
    public static SpoolSubmitResult Spooled(int jobId, long bytesWritten) => new(SpoolSubmitOutcome.Spooled, jobId, bytesWritten, false, null);

    public static SpoolSubmitResult NotCreated(string error, int win32Error = 0) => new(SpoolSubmitOutcome.NotCreated, 0, 0, false, error, win32Error);

    public static SpoolSubmitResult Incomplete(int jobId, long bytesWritten, bool aborted, string error, int win32Error = 0) =>
        new(SpoolSubmitOutcome.Incomplete, jobId, bytesWritten, aborted, error, win32Error);
}

public enum SpoolJobState
{
    /// <summary>The job is in the queue; <see cref="SpoolJobObservation.Status"/> has its JOB_STATUS bits.</summary>
    InQueue,

    /// <summary>No job with that id and document name is in the queue any more.</summary>
    Gone,

    /// <summary>The printer queue itself no longer exists.</summary>
    QueueMissing,

    /// <summary>The queue could not be read; nothing is known.</summary>
    Unknown
}

public sealed record SpoolJobObservation(SpoolJobState State, uint Status = 0, int Win32Error = 0)
{
    // JOB_INFO Status bits (winspool.h).
    public const uint JobPaused = 0x00000001;
    public const uint JobError = 0x00000002;
    public const uint JobDeleting = 0x00000004;
    public const uint JobPrinting = 0x00000010;
    public const uint JobOffline = 0x00000020;
    public const uint JobPaperOut = 0x00000040;
    public const uint JobPrinted = 0x00000080;
    public const uint JobDeleted = 0x00000100;
    public const uint JobBlockedDevq = 0x00000200;
    public const uint JobUserIntervention = 0x00000400;
    public const uint JobRestart = 0x00000800;
    public const uint JobComplete = 0x00001000;

    /// <summary>Statuses of a job that is stuck rather than waiting its turn.</summary>
    public const uint JobProblems = JobPaused | JobError | JobOffline | JobPaperOut | JobBlockedDevq | JobUserIntervention | JobRestart;

    public static SpoolJobObservation Gone { get; } = new(SpoolJobState.Gone);

    public static SpoolJobObservation InQueue(uint status) => new(SpoolJobState.InQueue, status);
}

public enum SpoolJobLookupState
{
    Found,
    NotFound,
    QueueMissing,
    Unknown
}

public sealed record SpoolJobLookup(SpoolJobLookupState State, int JobId = 0, int Win32Error = 0);
