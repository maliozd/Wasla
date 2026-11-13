using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// The service's engine host over given settings, for tests that need what the service persists (printer, operational
/// settings) without a server: no request is sent and nothing is printed. It lists exactly <paramref name="printers"/>.
/// </summary>
internal static class TestEngineHost
{
    public static PrintBridgeEngineHost Create(
        PrintBridgeSettingsStore store,
        PrintBridgeSettingsHolder holder,
        IReadOnlyList<string>? printers = null)
    {
        var client = new WaslaPrintBridgeClient(
            new HttpClient(),
            holder,
            new PrintBridgeSetupHttpClientFactory(),
            NullLogger<WaslaPrintBridgeClient>.Instance,
            "1.0.0.0");
        var history = new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance);
        var runtime = new PrintBridgeRuntime(
            client,
            new ReceiptFormatter(),
            new NoReceiptPrinter(),
            holder,
            store,
            new PrintBridgeDeviceMetadataSync(store, holder, NullLogger<PrintBridgeDeviceMetadataSync>.Instance),
            history,
            new AppVersionInfo(typeof(TestEngineHost).Assembly),
            NullLogger<PrintBridgeRuntime>.Instance);
        var trust = PrintBridgeServerTrustPolicy.ForTests("localhost");
        var listed = printers ?? ["Microsoft Print to PDF", "POS-58"];
        return new PrintBridgeEngineHost(
            runtime,
            holder,
            store,
            new PrintBridgeAutoSetupCoordinator(client, store, holder, runtime, trust, NullLogger<PrintBridgeAutoSetupCoordinator>.Instance),
            trust,
            history,
            NullLogger<PrintBridgeEngineHost>.Instance,
            () => listed);
    }

    private sealed class NoReceiptPrinter : IReceiptPrinter
    {
        public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct) =>
            throw new InvalidOperationException("These tests never print.");
    }
}
