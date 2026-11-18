using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wasla.PrintBridge.Printing;

/// <summary>
/// The winspool calls <see cref="WindowsPrintSpooler"/> makes, one member per native call (WAS-216). Each member returns
/// what the native function returned and the thread's last Win32 error read right after it, so the spooler's handling of
/// every native result (failures, byte counts, enumeration sizes) can be exercised deterministically.
/// </summary>
internal interface IWinspoolApi
{
    /// <summary>Whether the printer is among those this account can list (System.Drawing's installed printers).</summary>
    bool PrinterIsListed(string printerName);

    bool OpenPrinter(string printerName, out IntPtr printer, out int error);

    void ClosePrinter(IntPtr printer);

    /// <summary>StartDocPrinterW level 1, RAW. Returns the Windows job id, or 0 when no job was created.</summary>
    int StartDocPrinter(IntPtr printer, string documentName, out int error);

    bool StartPagePrinter(IntPtr printer, out int error);

    bool WritePrinter(IntPtr printer, IntPtr data, int count, out int written, out int error);

    bool EndPagePrinter(IntPtr printer, out int error);

    bool EndDocPrinter(IntPtr printer, out int error);

    bool AbortPrinter(IntPtr printer);

    bool GetPrinter(IntPtr printer, int level, IntPtr buffer, int size, out int needed, out int error);

    bool GetJob(IntPtr printer, int jobId, int level, IntPtr buffer, int size, out int needed, out int error);

    bool EnumJobs(IntPtr printer, int firstJob, int jobCount, int level, IntPtr buffer, int size, out int needed, out int returned, out int error);
}

/// <summary>The real winspool.drv (Unicode).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinspoolApi : IWinspoolApi
{
    public static WinspoolApi Instance { get; } = new();

    private WinspoolApi()
    {
    }

    public bool PrinterIsListed(string printerName) => RawPrinterHelper.PrinterExists(printerName);

    public bool OpenPrinter(string printerName, out IntPtr printer, out int error)
    {
        var ok = OpenPrinterW(printerName, out printer, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public void ClosePrinter(IntPtr printer) => ClosePrinterNative(printer);

    public int StartDocPrinter(IntPtr printer, string documentName, out int error)
    {
        var document = new DOC_INFO_1W { pDocName = documentName, pOutputFile = null, pDatatype = "RAW" };
        var jobId = StartDocPrinterW(printer, 1, document);
        error = jobId == 0 ? Marshal.GetLastWin32Error() : 0;
        return jobId;
    }

    public bool StartPagePrinter(IntPtr printer, out int error) => Result(StartPagePrinterNative(printer), out error);

    public bool WritePrinter(IntPtr printer, IntPtr data, int count, out int written, out int error) =>
        Result(WritePrinterNative(printer, data, count, out written), out error);

    public bool EndPagePrinter(IntPtr printer, out int error) => Result(EndPagePrinterNative(printer), out error);

    public bool EndDocPrinter(IntPtr printer, out int error) => Result(EndDocPrinterNative(printer), out error);

    public bool AbortPrinter(IntPtr printer) => AbortPrinterNative(printer);

    public bool GetPrinter(IntPtr printer, int level, IntPtr buffer, int size, out int needed, out int error) =>
        Result(GetPrinterW(printer, level, buffer, size, out needed), out error);

    public bool GetJob(IntPtr printer, int jobId, int level, IntPtr buffer, int size, out int needed, out int error) =>
        Result(GetJobW(printer, jobId, level, buffer, size, out needed), out error);

    public bool EnumJobs(IntPtr printer, int firstJob, int jobCount, int level, IntPtr buffer, int size, out int needed, out int returned, out int error) =>
        Result(EnumJobsW(printer, firstJob, jobCount, level, buffer, size, out needed, out returned), out error);

    private static bool Result(bool ok, out int error)
    {
        // Read at once: any later call may overwrite the thread's last error.
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinterNative(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinterW(IntPtr hPrinter, int level, [In] DOC_INFO_1W pDocInfo);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinterNative(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinterNative(IntPtr hPrinter, IntPtr pBuf, int cbBuf, out int pcWritten);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinterNative(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinterNative(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "AbortPrinter", SetLastError = true)]
    private static extern bool AbortPrinterNative(IntPtr hPrinter);

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
}

[StructLayout(LayoutKind.Sequential)]
internal struct PRINTER_INFO_2W
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
internal struct JOB_INFO_1W
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
internal struct SYSTEMTIME
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
