using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wasla.PrintBridge.Printing;

/// <summary>
/// The Windows spooler through winspool (WAS-216). RAW jobs need no desktop and no GDI device context, so this works in
/// the service's session 0. Each call opens its own printer handle and closes it before it returns.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrintSpooler : IPrintSpooler
{
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidPrinterName = 1801;

    /// <summary>How many writes in a row may accept no byte before the job is given up.</summary>
    private const int MaxZeroProgressWrites = 3;

    /// <summary>The most jobs one EnumJobs call lists. A queue that fills it may hold more.</summary>
    private const int MaxEnumeratedJobs = 4096;

    /// <summary>How often a queue that grows between the size probe and the read is listed again.</summary>
    private const int MaxEnumerationAttempts = 3;

    private readonly IWinspoolApi _winspool;

    public WindowsPrintSpooler()
        : this(WinspoolApi.Instance)
    {
    }

    /// <summary>For tests: the spooler's handling of each native result, against scripted winspool calls.</summary>
    internal WindowsPrintSpooler(IWinspoolApi winspool) => _winspool = winspool;

    public PrinterAvailability GetAvailability(string printerName)
    {
        // "Not found" keeps its meaning from before WAS-216: not among the printers this account can list.
        if (!_winspool.PrinterIsListed(printerName))
            return PrinterAvailability.NotFound;

        if (!_winspool.OpenPrinter(printerName, out var printer, out var error))
        {
            return error == ErrorInvalidPrinterName
                ? PrinterAvailability.NotFound
                : new PrinterAvailability(PrinterAvailabilityState.Error, Win32Error: error);
        }

        try
        {
            if (!TryGet(
                    (IntPtr buffer, int size, out int needed, out int callError) => _winspool.GetPrinter(printer, 2, buffer, size, out needed, out callError),
                    out var info,
                    out error))
            {
                return new PrinterAvailability(PrinterAvailabilityState.Error, Win32Error: error);
            }

            try
            {
                var printerInfo = Marshal.PtrToStructure<PRINTER_INFO_2W>(info);
                return PrinterAvailability.FromWindows(printerInfo.Status, printerInfo.Attributes);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            _winspool.ClosePrinter(printer);
        }
    }

    public SpoolSubmitResult Submit(string printerName, string documentName, byte[] data, Action<int> onJobCreated)
    {
        if (!_winspool.OpenPrinter(printerName, out var printer, out var openError))
        {
            return SpoolSubmitResult.NotCreated($"Unable to open printer '{printerName}' (Win32 error {openError}).", openError);
        }

        try
        {
            var jobId = _winspool.StartDocPrinter(printer, documentName, out var startError);
            if (jobId == 0)
            {
                return SpoolSubmitResult.NotCreated($"Unable to start print job on '{printerName}' (Win32 error {startError}).", startError);
            }

            // From here a Windows job exists: whatever fails, the result says so and the job is not called "not created".
            long written = 0;
            string? failure = null;
            var win32Error = 0;
            var writing = false;
            var writeUncertain = false;
            try
            {
                onJobCreated(jobId);

                if (!_winspool.StartPagePrinter(printer, out win32Error))
                {
                    failure = $"Unable to start print page on '{printerName}' (Win32 error {win32Error}).";
                }
                else
                {
                    writing = true;
                    written = WriteAll(printer, data, out writeUncertain, out failure, out win32Error);
                    writing = false;
                    if (!_winspool.EndPagePrinter(printer, out var endPageError) && failure is null)
                    {
                        win32Error = endPageError;
                        failure = $"Unable to end print page on '{printerName}' (Win32 error {win32Error}).";
                    }
                }

                if (failure is null)
                {
                    if (_winspool.EndDocPrinter(printer, out var endDocError))
                        return SpoolSubmitResult.Spooled(jobId, written);

                    win32Error = endDocError;
                    failure = $"Unable to finish print job on '{printerName}' (Win32 error {win32Error}).";
                }
            }
            catch (Exception ex)
            {
                failure ??= $"Print job on '{printerName}' failed: {ex.GetType().Name}.";
                // Thrown in the middle of the writes: what Windows took is not known.
                writeUncertain |= writing;
            }

            // Best effort: AbortPrinter deletes the unfinished job's spool data. It does not prove that nothing printed, since
            // a printer set to start printing at once may already have received part of it.
            var aborted = _winspool.AbortPrinter(printer);
            return SpoolSubmitResult.Incomplete(jobId, written, aborted, failure, win32Error, writeUncertain);
        }
        finally
        {
            _winspool.ClosePrinter(printer);
        }
    }

    public SpoolJobObservation Observe(string printerName, int jobId, string documentName)
    {
        if (!_winspool.OpenPrinter(printerName, out var printer, out var openError))
        {
            return new SpoolJobObservation(
                openError == ErrorInvalidPrinterName ? SpoolJobState.QueueMissing : SpoolJobState.Unknown,
                Win32Error: openError);
        }

        try
        {
            if (!TryGet(
                    (IntPtr buffer, int size, out int needed, out int callError) => _winspool.GetJob(printer, jobId, 1, buffer, size, out needed, out callError),
                    out var info,
                    out var error))
            {
                // GetJob answers ERROR_INVALID_PARAMETER for an id that is not in the queue.
                return error == ErrorInvalidParameter
                    ? SpoolJobObservation.Gone
                    : new SpoolJobObservation(SpoolJobState.Unknown, Win32Error: error);
            }

            try
            {
                var job = Marshal.PtrToStructure<JOB_INFO_1W>(info);
                var name = Marshal.PtrToStringUni(job.pDocument);
                // Job ids are reused: a job with this id but another name is not ours, so ours has left the queue.
                return string.Equals(name, documentName, StringComparison.Ordinal)
                    ? SpoolJobObservation.InQueue(job.Status)
                    : SpoolJobObservation.Gone;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            _winspool.ClosePrinter(printer);
        }
    }

    public SpoolJobLookup Find(string printerName, string documentName)
    {
        if (!_winspool.OpenPrinter(printerName, out var printer, out var openError))
        {
            return new SpoolJobLookup(
                openError == ErrorInvalidPrinterName ? SpoolJobLookupState.QueueMissing : SpoolJobLookupState.Unknown,
                Win32Error: openError);
        }

        try
        {
            // NotFound only from a queue Windows listed completely; any failure or incomplete listing is Unknown, because
            // "not found" lets the user answer "It did not print" while the copy might still print (WAS-216 review).
            var error = 0;
            for (var attempt = 0; attempt < MaxEnumerationAttempts; attempt++)
            {
                if (_winspool.EnumJobs(printer, 0, MaxEnumeratedJobs, 1, IntPtr.Zero, 0, out var needed, out var probed, out error))
                {
                    // Success without a buffer: Windows listed an empty queue.
                    return needed == 0 && probed == 0
                        ? new SpoolJobLookup(SpoolJobLookupState.NotFound)
                        : new SpoolJobLookup(SpoolJobLookupState.Unknown);
                }

                if (error != ErrorInsufficientBuffer || needed <= 0)
                    return new SpoolJobLookup(SpoolJobLookupState.Unknown, Win32Error: error);

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!_winspool.EnumJobs(printer, 0, MaxEnumeratedJobs, 1, buffer, needed, out _, out var returned, out error))
                    {
                        // The queue grew between the two calls: list it again with the new size.
                        if (error == ErrorInsufficientBuffer)
                            continue;
                        return new SpoolJobLookup(SpoolJobLookupState.Unknown, Win32Error: error);
                    }

                    var stride = Marshal.SizeOf<JOB_INFO_1W>();
                    if (returned < 0 || (long)returned * stride > needed)
                        return new SpoolJobLookup(SpoolJobLookupState.Unknown);

                    for (var i = 0; i < returned; i++)
                    {
                        var job = Marshal.PtrToStructure<JOB_INFO_1W>(buffer + (i * stride));
                        if (string.Equals(Marshal.PtrToStringUni(job.pDocument), documentName, StringComparison.Ordinal))
                            return new SpoolJobLookup(SpoolJobLookupState.Found, (int)job.JobId);
                    }

                    // A full page may not be the whole queue: the copy could be among the jobs Windows did not return.
                    return returned >= MaxEnumeratedJobs
                        ? new SpoolJobLookup(SpoolJobLookupState.Unknown)
                        : new SpoolJobLookup(SpoolJobLookupState.NotFound);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return new SpoolJobLookup(SpoolJobLookupState.Unknown, Win32Error: error);
        }
        finally
        {
            _winspool.ClosePrinter(printer);
        }
    }

    /// <summary>
    /// Writes every byte, continuing after a partial write. A write that fails, or <see cref="MaxZeroProgressWrites"/>
    /// writes in a row that accept nothing, end the job with an error. Returns the bytes Windows reported taken, including
    /// those a failing call reported. <paramref name="uncertain"/>: a call failed or reported an impossible count, so
    /// Windows may have taken or passed on data it did not report (WAS-216 review). Only calls that succeeded and reported
    /// zero bytes are taken as having passed nothing on.
    /// </summary>
    private long WriteAll(IntPtr printer, byte[] data, out bool uncertain, out string? failure, out int win32Error)
    {
        uncertain = false;
        failure = null;
        win32Error = 0;
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var start = handle.AddrOfPinnedObject();
            var offset = 0;
            var zeroWrites = 0;
            while (offset < data.Length)
            {
                var remaining = data.Length - offset;
                if (!_winspool.WritePrinter(printer, start + offset, remaining, out var written, out win32Error))
                {
                    // A failing call may still report bytes it took; a direct or despooling port may have sent more.
                    uncertain = true;
                    var taken = written > 0 && written <= remaining ? written : 0;
                    failure = $"Unable to write to printer (Win32 error {win32Error}) after {offset + taken} of {data.Length} bytes.";
                    return offset + taken;
                }

                if (written < 0 || written > remaining)
                {
                    uncertain = true;
                    failure = $"Unable to write to printer: Windows reported {written} bytes written of {remaining}.";
                    return offset;
                }

                if (written == 0)
                {
                    if (++zeroWrites >= MaxZeroProgressWrites)
                    {
                        failure = $"Unable to write to printer: no progress after {offset} of {data.Length} bytes.";
                        return offset;
                    }

                    Thread.Sleep(50);
                    continue;
                }

                zeroWrites = 0;
                offset += written;
            }

            return offset;
        }
        finally
        {
            handle.Free();
        }
    }

    private delegate bool InfoCall(IntPtr buffer, int size, out int needed, out int error);

    /// <summary>Calls a winspool getter twice: once for the size, once for the data. The caller frees the buffer.</summary>
    private static bool TryGet(InfoCall call, out IntPtr buffer, out int error)
    {
        buffer = IntPtr.Zero;
        if (call(IntPtr.Zero, 0, out var needed, out error) || needed <= 0)
            return false;

        if (error != ErrorInsufficientBuffer)
            return false;

        buffer = Marshal.AllocHGlobal(needed);
        if (call(buffer, needed, out _, out error))
            return true;

        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        return false;
    }
}

/// <summary>Printer existence and listing, as the service sees them (machine-wide printers only).</summary>
[SupportedOSPlatform("windows")]
internal static class RawPrinterHelper
{
    public static bool PrinterExists(string printerName)
    {
        foreach (var installed in ListInstalledPrinters())
        {
            if (string.Equals(installed, printerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ListInstalledPrinters()
    {
        var printers = new List<string>();
        foreach (string printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            printers.Add(printer);
        return printers;
    }
}
