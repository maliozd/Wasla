using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OrderHub.PrintBridge.Printing;

[SupportedOSPlatform("windows")]
public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
    public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows printer mode is only supported on Windows.");

        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("PrinterName is required when DryRun is disabled.");

        if (!RawPrinterHelper.PrinterExists(printerName))
            throw new InvalidOperationException($"Printer not found: {printerName}");

        var safeCopies = Math.Clamp(copyCount, 1, 3);
        var payload = Encoding.UTF8.GetBytes(text);

        for (var copy = 0; copy < safeCopies; copy++)
        {
            ct.ThrowIfCancellationRequested();
            RawPrinterHelper.SendBytesToPrinter(printerName, payload);
        }

        return Task.CompletedTask;
    }
}

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

    public static void SendBytesToPrinter(string printerName, byte[] bytes)
    {
        var docInfo = new DOCINFOA
        {
            pDocName = "Wasla Receipt",
            pDataType = "RAW"
        };

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"Unable to open printer '{printerName}'.");

        try
        {
            if (!StartDocPrinter(hPrinter, 1, docInfo))
                throw new InvalidOperationException($"Unable to start print job on '{printerName}'.");

            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new InvalidOperationException($"Unable to start print page on '{printerName}'.");

                try
                {
                    if (!WritePrinter(hPrinter, bytes, bytes.Length, out _))
                        throw new InvalidOperationException($"Unable to write to printer '{printerName}'.");
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName = string.Empty;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile = string.Empty;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType = string.Empty;
    }
}
