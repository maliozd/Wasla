using System.Collections.Concurrent;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// A Windows print queue under the test's control (WAS-216), behind the same boundary the engine uses for the real
/// spooler. It keeps jobs by id with their document names, as winspool does, so the engine's own coordination and journal
/// logic run unchanged: availability answers, how far each submission gets, what each job reports while it waits, and
/// when it leaves (finished, deleted, or its id reused by another document).
/// </summary>
internal sealed class ScriptedPrintSpooler : IPrintSpooler
{
    private readonly object _sync = new();
    private readonly Dictionary<int, (string DocumentName, uint Status)> _queue = [];
    private readonly ConcurrentQueue<string> _submitted = new();
    private readonly ConcurrentQueue<PrinterAvailability> _nextAvailabilities = new();
    private int _nextJobId = 41;
    private int _submitCalls;
    private int _availabilityCalls;

    /// <summary>The queue's state when no scripted answer is pending.</summary>
    public PrinterAvailability Availability { get; set; } = PrinterAvailability.Ready;

    /// <summary>When set, decides each availability answer instead of <see cref="Availability"/>.</summary>
    public Func<PrinterAvailability>? AvailabilityProvider { get; set; }

    /// <summary>
    /// How the n-th submission (1-based) ends. Null: created, every byte written, finished. A job that is created is put
    /// in the queue before the script decides, as Windows does; <see cref="SpoolSubmitOutcome.Incomplete"/> with
    /// <c>Aborted</c> removes it again.
    /// </summary>
    public Func<int, SpoolSubmitResult?>? SubmitScript { get; set; }

    /// <summary>When set, created jobs stay in the queue (with <see cref="QueuedStatus"/>) until the test lets them go.</summary>
    public bool KeepJobsQueued { get; set; }

    public uint QueuedStatus { get; set; }

    /// <summary>Answers every observation and search with "the queue could not be read".</summary>
    public bool QueueUnreadable { get; set; }

    /// <summary>The printer queue no longer exists.</summary>
    public bool QueueMissing { get; set; }

    /// <summary>Holds every availability answer until opened (a spooler that does not respond).</summary>
    public PrintBridgeStopRaceTests.Gate? HoldAvailability { get; set; }

    /// <summary>The document names of the jobs Windows created, in order: one per copy handed over.</summary>
    public string[] Submitted => [.. _submitted];

    public int SubmitCalls => Volatile.Read(ref _submitCalls);

    public int AvailabilityCalls => Volatile.Read(ref _availabilityCalls);

    /// <summary>The answers the next availability checks give, in order, before <see cref="Availability"/> again.</summary>
    public void ThenAvailability(params PrinterAvailability[] answers)
    {
        foreach (var answer in answers)
            _nextAvailabilities.Enqueue(answer);
    }

    public int[] QueuedJobIds()
    {
        lock (_sync)
            return [.. _queue.Keys];
    }

    /// <summary>A job created before this queue was observed (by an earlier run of the engine).</summary>
    public void AddQueuedJob(int jobId, string documentName, uint status = 0)
    {
        lock (_sync)
            _queue[jobId] = (documentName, status);
    }

    public void SetStatus(int jobId, uint status)
    {
        lock (_sync)
            _queue[jobId] = (_queue[jobId].DocumentName, status);
    }

    /// <summary>The job leaves the queue (finished, or deleted between two looks).</summary>
    public void Remove(int jobId)
    {
        lock (_sync)
            _queue.Remove(jobId);
    }

    public void RemoveAll()
    {
        lock (_sync)
            _queue.Clear();
    }

    /// <summary>The job left and Windows gave its id to another document.</summary>
    public void ReuseId(int jobId, string otherDocument)
    {
        lock (_sync)
            _queue[jobId] = (otherDocument, 0);
    }

    public PrinterAvailability GetAvailability(string printerName)
    {
        Interlocked.Increment(ref _availabilityCalls);
        if (HoldAvailability is { } gate)
        {
            gate.Entered.TrySetResult();
            gate.Opened.Task.GetAwaiter().GetResult();
        }

        if (string.IsNullOrWhiteSpace(printerName))
            return PrinterAvailability.NotFound;
        if (_nextAvailabilities.TryDequeue(out var next))
            return next;
        return AvailabilityProvider?.Invoke() ?? Availability;
    }

    public SpoolSubmitResult Submit(string printerName, string documentName, byte[] data, Action<int> onJobCreated)
    {
        var call = Interlocked.Increment(ref _submitCalls);
        var scripted = SubmitScript?.Invoke(call);
        if (scripted is { Outcome: SpoolSubmitOutcome.NotCreated })
            return scripted;

        int jobId;
        lock (_sync)
        {
            jobId = ++_nextJobId;
            _queue[jobId] = (documentName, QueuedStatus);
        }

        _submitted.Enqueue(documentName);
        onJobCreated(jobId);

        if (scripted is { Outcome: SpoolSubmitOutcome.Incomplete })
        {
            if (scripted.Aborted)
                Remove(jobId);
            return scripted with { JobId = jobId };
        }

        if (!KeepJobsQueued)
            Remove(jobId);
        return SpoolSubmitResult.Spooled(jobId, data.Length);
    }

    public SpoolJobObservation Observe(string printerName, int jobId, string documentName)
    {
        if (QueueMissing)
            return new SpoolJobObservation(SpoolJobState.QueueMissing);
        if (QueueUnreadable)
            return new SpoolJobObservation(SpoolJobState.Unknown, Win32Error: 1722);

        lock (_sync)
        {
            return _queue.TryGetValue(jobId, out var job) && string.Equals(job.DocumentName, documentName, StringComparison.Ordinal)
                ? SpoolJobObservation.InQueue(job.Status)
                : SpoolJobObservation.Gone;
        }
    }

    public SpoolJobLookup Find(string printerName, string documentName)
    {
        if (QueueMissing)
            return new SpoolJobLookup(SpoolJobLookupState.QueueMissing);
        if (QueueUnreadable)
            return new SpoolJobLookup(SpoolJobLookupState.Unknown, Win32Error: 1722);

        lock (_sync)
        {
            foreach (var (id, job) in _queue)
            {
                if (string.Equals(job.DocumentName, documentName, StringComparison.Ordinal))
                    return new SpoolJobLookup(SpoolJobLookupState.Found, id);
            }
        }

        return new SpoolJobLookup(SpoolJobLookupState.NotFound);
    }
}
