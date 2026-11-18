using System.Text;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Tests;

/// <summary>A test printer that records (or holds, or fails) the copies the engine prints.</summary>
internal interface ITestReceiptPrinter
{
    Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct);
}

/// <summary>
/// The spooler boundary (WAS-216) for tests that only care about copies: each submission is one copy handed to
/// <see cref="ITestReceiptPrinter"/>. A copy the printer refuses (throws) never became a Windows job; every accepted job
/// leaves the queue at once, as on a printer that takes the data immediately. Scenarios that need the queue itself use
/// <see cref="ScriptedPrintSpooler"/>.
/// </summary>
internal sealed class RecordingPrinterSpooler(ITestReceiptPrinter printer) : IPrintSpooler
{
    private int _nextJobId = 100;

    /// <summary>
    /// As before WAS-216 for these tests: a printer installed in Windows (read only) is ready, any other name is not found.
    /// Output never goes to it; it goes to the recording printer.
    /// </summary>
    public PrinterAvailability GetAvailability(string printerName)
    {
        foreach (string installed in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(installed, printerName, StringComparison.OrdinalIgnoreCase))
                return PrinterAvailability.Ready;
        }

        return PrinterAvailability.NotFound;
    }

    public SpoolSubmitResult Submit(string printerName, string documentName, byte[] data, Action<int> onJobCreated)
    {
        try
        {
            printer.PrintAsync(printerName, Encoding.UTF8.GetString(data), 1, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return SpoolSubmitResult.NotCreated(ex.Message);
        }

        var jobId = Interlocked.Increment(ref _nextJobId);
        onJobCreated(jobId);
        return SpoolSubmitResult.Spooled(jobId, data.Length);
    }

    public SpoolJobObservation Observe(string printerName, int jobId, string documentName) => SpoolJobObservation.Gone;

    public SpoolJobLookup Find(string printerName, string documentName) => new(SpoolJobLookupState.NotFound);
}
