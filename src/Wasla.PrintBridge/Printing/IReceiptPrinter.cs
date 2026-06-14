namespace Wasla.PrintBridge.Printing;

public interface IReceiptPrinter
{
    Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct);
}
