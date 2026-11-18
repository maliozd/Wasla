using System.Runtime.InteropServices;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// The winspool calls under the test's control (WAS-216): each call returns what the script says, with the last Win32
/// error and the byte counts or enumeration sizes the real winspool.drv would report. <see cref="WindowsPrintSpooler"/>
/// runs unchanged on top of it, so its handling of every native result is what the tests see. Thread-safe: the engine
/// calls the spooler from pool threads.
/// </summary>
internal sealed class ScriptedWinspool : IWinspoolApi, IDisposable
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorRpcUnavailable = 1722;
    public const int ErrorInvalidPrinterName = 1801;
    public const int ErrorDeviceNotConnected = 1167;

    private static readonly IntPtr Handle = new(0x5A5A);

    private readonly object _sync = new();
    private readonly List<(int Id, IntPtr Document, uint Status)> _jobs = [];
    private readonly List<IntPtr> _strings = [];
    private readonly List<byte> _received = [];
    private readonly List<string> _calls = [];
    private int _writeCalls;
    private int _enumDataCalls;

    // ---- Script -------------------------------------------------------------------------------------------------

    /// <summary>OpenPrinter's error; 0 opens the printer.</summary>
    public int OpenError { get; set; }

    public uint PrinterStatus { get; set; }

    public uint PrinterAttributes { get; set; }

    /// <summary>The id StartDocPrinter returns; 0 means no job was created.</summary>
    public int NextJobId { get; set; } = 7;

    public int StartDocError { get; set; } = 1804;

    /// <summary>StartPagePrinter's error; 0 succeeds.</summary>
    public int StartPageError { get; set; }

    /// <summary>
    /// What one WritePrinter call returns: (call number from 1, bytes offered) → (returned, bytes it reports written, last
    /// error). Null writes everything offered.
    /// </summary>
    public Func<int, int, (bool Ok, int Written, int Error)>? WriteScript { get; set; }

    public int EndPageError { get; set; }

    public int EndDocError { get; set; }

    public bool AbortSucceeds { get; set; } = true;

    /// <summary>When set, the size probe of EnumJobs (no buffer) fails with this error and reports nothing needed.</summary>
    public int? EnumProbeError { get; set; }

    /// <summary>When set, EnumJobs with a buffer fails with this error.</summary>
    public int? EnumDataError { get; set; }

    /// <summary>Runs before each EnumJobs call with a buffer (call number from 1): the queue can change between the two.</summary>
    public Action<int>? BeforeEnumData { get; set; }

    // ---- What happened ----------------------------------------------------------------------------------------------

    /// <summary>The bytes each WritePrinter call reported as written, in order.</summary>
    public byte[] Received
    {
        get
        {
            lock (_sync)
                return [.. _received];
        }
    }

    /// <summary>The native calls in order (WritePrinter, AbortPrinter, ...), plus "onJobCreated" when the test records it.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_sync)
                return [.. _calls];
        }
    }

    public int AbortCalls => Calls.Count(c => c == "AbortPrinter");

    public void Note(string call)
    {
        lock (_sync)
            _calls.Add(call);
    }

    public void AddJob(int id, string documentName, uint status = 0)
    {
        lock (_sync)
            _jobs.Add((id, Text(documentName), status));
    }

    /// <summary>Adds many jobs of another document at once (they share one name buffer).</summary>
    public void AddOtherJobs(int count, int firstId)
    {
        lock (_sync)
        {
            var name = Text("Someone else's document");
            for (var i = 0; i < count; i++)
                _jobs.Add((firstId + i, name, 0));
        }
    }

    public void RemoveJob(int id)
    {
        lock (_sync)
            _jobs.RemoveAll(j => j.Id == id);
    }

    // ---- IWinspoolApi -----------------------------------------------------------------------------------------------

    public bool PrinterIsListed(string printerName) => OpenError != ErrorInvalidPrinterName;

    public bool OpenPrinter(string printerName, out IntPtr printer, out int error)
    {
        Note("OpenPrinter");
        printer = OpenError == 0 ? Handle : IntPtr.Zero;
        error = OpenError;
        return OpenError == 0;
    }

    public void ClosePrinter(IntPtr printer) => Note("ClosePrinter");

    public int StartDocPrinter(IntPtr printer, string documentName, out int error)
    {
        Note("StartDocPrinter");
        error = NextJobId == 0 ? StartDocError : 0;
        return NextJobId;
    }

    public bool StartPagePrinter(IntPtr printer, out int error)
    {
        Note("StartPagePrinter");
        error = StartPageError;
        return StartPageError == 0;
    }

    public bool WritePrinter(IntPtr printer, IntPtr data, int count, out int written, out int error)
    {
        int call;
        lock (_sync)
        {
            _calls.Add("WritePrinter");
            call = ++_writeCalls;
        }

        var (ok, reported, lastError) = WriteScript?.Invoke(call, count) ?? (true, count, 0);
        // What Windows reports it took is what reached it (as far as the caller can know), whether or not the call failed.
        if (reported > 0 && reported <= count)
        {
            var bytes = new byte[reported];
            Marshal.Copy(data, bytes, 0, reported);
            lock (_sync)
                _received.AddRange(bytes);
        }

        written = reported;
        error = ok ? 0 : lastError;
        return ok;
    }

    public bool EndPagePrinter(IntPtr printer, out int error)
    {
        Note("EndPagePrinter");
        error = EndPageError;
        return EndPageError == 0;
    }

    public bool EndDocPrinter(IntPtr printer, out int error)
    {
        Note("EndDocPrinter");
        error = EndDocError;
        return EndDocError == 0;
    }

    public bool AbortPrinter(IntPtr printer)
    {
        Note("AbortPrinter");
        return AbortSucceeds;
    }

    public bool GetPrinter(IntPtr printer, int level, IntPtr buffer, int size, out int needed, out int error)
    {
        needed = Marshal.SizeOf<PRINTER_INFO_2W>();
        if (buffer == IntPtr.Zero || size < needed)
        {
            error = ErrorInsufficientBuffer;
            return false;
        }

        Marshal.StructureToPtr(new PRINTER_INFO_2W { Status = PrinterStatus, Attributes = PrinterAttributes }, buffer, false);
        error = 0;
        return true;
    }

    public bool GetJob(IntPtr printer, int jobId, int level, IntPtr buffer, int size, out int needed, out int error)
    {
        (int Id, IntPtr Document, uint Status) job;
        lock (_sync)
            job = _jobs.FirstOrDefault(j => j.Id == jobId);

        if (job.Id == 0)
        {
            needed = 0;
            error = ErrorInvalidParameter;
            return false;
        }

        needed = Marshal.SizeOf<JOB_INFO_1W>();
        if (buffer == IntPtr.Zero || size < needed)
        {
            error = ErrorInsufficientBuffer;
            return false;
        }

        Marshal.StructureToPtr(new JOB_INFO_1W { JobId = (uint)job.Id, pDocument = job.Document, Status = job.Status }, buffer, false);
        error = 0;
        return true;
    }

    public bool EnumJobs(IntPtr printer, int firstJob, int jobCount, int level, IntPtr buffer, int size, out int needed, out int returned, out int error)
    {
        returned = 0;
        if (buffer == IntPtr.Zero && EnumProbeError is { } probeError)
        {
            Note("EnumJobs(probe) failed");
            needed = 0;
            error = probeError;
            return false;
        }

        if (buffer != IntPtr.Zero)
        {
            int call;
            lock (_sync)
                call = ++_enumDataCalls;
            BeforeEnumData?.Invoke(call);
            if (EnumDataError is { } dataError)
            {
                Note("EnumJobs(data) failed");
                needed = 0;
                error = dataError;
                return false;
            }
        }

        List<(int Id, IntPtr Document, uint Status)> visible;
        lock (_sync)
            visible = _jobs.Skip(firstJob).Take(jobCount).ToList();

        var stride = Marshal.SizeOf<JOB_INFO_1W>();
        needed = visible.Count * stride;
        if (needed == 0)
        {
            // An empty queue: the call succeeds and needs no buffer.
            error = 0;
            return true;
        }

        if (buffer == IntPtr.Zero || size < needed)
        {
            error = ErrorInsufficientBuffer;
            return false;
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var job = visible[i];
            Marshal.StructureToPtr(
                new JOB_INFO_1W { JobId = (uint)job.Id, pDocument = job.Document, Status = job.Status },
                buffer + (i * stride),
                false);
        }

        returned = visible.Count;
        error = 0;
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var text in _strings)
                Marshal.FreeHGlobal(text);
            _strings.Clear();
            _jobs.Clear();
        }
    }

    private IntPtr Text(string value)
    {
        var text = Marshal.StringToHGlobalUni(value);
        _strings.Add(text);
        return text;
    }
}
