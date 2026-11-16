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

    private const int MaxEnumeratedJobs = 4096;

    public PrinterAvailability GetAvailability(string printerName)
    {
        // "Not found" keeps its meaning from before WAS-216: not among the printers this account can list.
        if (!RawPrinterHelper.PrinterExists(printerName))
            return PrinterAvailability.NotFound;

        if (!OpenPrinterW(printerName, out var printer, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorInvalidPrinterName
                ? PrinterAvailability.NotFound
                : new PrinterAvailability(PrinterAvailabilityState.Error, Win32Error: error);
        }

        try
        {
            if (!TryGet(
                    (IntPtr buffer, int size, out int needed) => GetPrinterW(printer, 2, buffer, size, out needed),
                    out var info,
                    out var error))
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
            ClosePrinter(printer);
        }
    }

    public SpoolSubmitResult Submit(string printerName, string documentName, byte[] data, Action<int> onJobCreated)
    {
        if (!OpenPrinterW(printerName, out var printer, IntPtr.Zero))
        {
            var openError = Marshal.GetLastWin32Error();
            return SpoolSubmitResult.NotCreated($"Unable to open printer '{printerName}' (Win32 error {openError}).", openError);
        }

        try
        {
            var document = new DOC_INFO_1W { pDocName = documentName, pOutputFile = null, pDatatype = "RAW" };
            var jobId = StartDocPrinterW(printer, 1, document);
            if (jobId == 0)
            {
                var startError = Marshal.GetLastWin32Error();
                return SpoolSubmitResult.NotCreated($"Unable to start print job on '{printerName}' (Win32 error {startError}).", startError);
            }

            // From here a Windows job exists: whatever fails, the result says so and the job is not called "not created".
            long written = 0;
            string? failure = null;
            var win32Error = 0;
            try
            {
                onJobCreated(jobId);

                if (!StartPagePrinter(printer))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    failure = $"Unable to start print page on '{printerName}' (Win32 error {win32Error}).";
                }
                else
                {
                    written = WriteAll(printer, data, out failure, out win32Error);
                    if (!EndPagePrinter(printer) && failure is null)
                    {
                        win32Error = Marshal.GetLastWin32Error();
                        failure = $"Unable to end print page on '{printerName}' (Win32 error {win32Error}).";
                    }
                }

                if (failure is null)
                {
                    if (EndDocPrinter(printer))
                        return SpoolSubmitResult.Spooled(jobId, written);

                    win32Error = Marshal.GetLastWin32Error();
                    failure = $"Unable to finish print job on '{printerName}' (Win32 error {win32Error}).";
                }
            }
            catch (Exception ex)
            {
                failure ??= $"Print job on '{printerName}' failed: {ex.GetType().Name}.";
            }

            // Best effort: AbortPrinter deletes the unfinished job's spool data. It does not prove that nothing printed, since
            // a printer set to start printing at once may already have received part of it.
            var aborted = AbortPrinter(printer);
            return SpoolSubmitResult.Incomplete(jobId, written, aborted, failure, win32Error);
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    public SpoolJobObservation Observe(string printerName, int jobId, string documentName)
    {
        if (!OpenPrinterW(printerName, out var printer, IntPtr.Zero))
        {
            var openError = Marshal.GetLastWin32Error();
            return new SpoolJobObservation(
                openError == ErrorInvalidPrinterName ? SpoolJobState.QueueMissing : SpoolJobState.Unknown,
                Win32Error: openError);
        }

        try
        {
            if (!TryGet(
                    (IntPtr buffer, int size, out int needed) => GetJobW(printer, jobId, 1, buffer, size, out needed),
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
            ClosePrinter(printer);
        }
    }

    public SpoolJobLookup Find(string printerName, string documentName)
    {
        if (!OpenPrinterW(printerName, out var printer, IntPtr.Zero))
        {
            var openError = Marshal.GetLastWin32Error();
            return new SpoolJobLookup(
                openError == ErrorInvalidPrinterName ? SpoolJobLookupState.QueueMissing : SpoolJobLookupState.Unknown,
                Win32Error: openError);
        }

        try
        {
            EnumJobsW(printer, 0, MaxEnumeratedJobs, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return new SpoolJobLookup(SpoolJobLookupState.NotFound);

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobsW(printer, 0, MaxEnumeratedJobs, 1, buffer, needed, out _, out var returned))
                    return new SpoolJobLookup(SpoolJobLookupState.Unknown, Win32Error: Marshal.GetLastWin32Error());

                var stride = Marshal.SizeOf<JOB_INFO_1W>();
                for (var i = 0; i < returned; i++)
                {
                    var job = Marshal.PtrToStructure<JOB_INFO_1W>(buffer + (i * stride));
                    if (string.Equals(Marshal.PtrToStringUni(job.pDocument), documentName, StringComparison.Ordinal))
                        return new SpoolJobLookup(SpoolJobLookupState.Found, (int)job.JobId);
                }

                return new SpoolJobLookup(SpoolJobLookupState.NotFound);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    /// <summary>
    /// Writes every byte, continuing after a partial write. A write that fails, or <see cref="MaxZeroProgressWrites"/>
    /// writes in a row that accept nothing, end the job with an error. Returns the bytes Windows accepted.
    /// </summary>
    private static long WriteAll(IntPtr printer, byte[] data, out string? failure, out int win32Error)
    {
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
                if (!WritePrinter(printer, start + offset, data.Length - offset, out var written))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    failure = $"Unable to write to printer (Win32 error {win32Error}) after {offset} of {data.Length} bytes.";
                    return offset;
                }

                if (written < 0 || written > data.Length - offset)
                {
                    failure = $"Unable to write to printer: Windows reported {written} bytes written of {data.Length - offset}.";
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

    private delegate bool InfoCall(IntPtr buffer, int size, out int needed);

    /// <summary>Calls a winspool getter twice: once for the size, once for the data. The caller frees the buffer.</summary>
    private static bool TryGet(InfoCall call, out IntPtr buffer, out int error)
    {
        buffer = IntPtr.Zero;
        if (call(IntPtr.Zero, 0, out var needed) || needed <= 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
            return false;

        buffer = Marshal.AllocHGlobal(needed);
        if (call(buffer, needed, out _))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        return false;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    /// <summary>Returns the Windows job id, or 0 when no job was created.</summary>
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinterW(IntPtr hPrinter, int level, [In] DOC_INFO_1W pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBuf, int cbBuf, out int pcWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinterW(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetJobW(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobsW(
        IntPtr hPrinter,
        int firstJob,
        int noJobs,
        int level,
        IntPtr pJob,
        int cbBuf,
        out int pcbNeeded,
        out int pcReturned);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOC_INFO_1W
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_2W
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOB_INFO_1W
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
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
