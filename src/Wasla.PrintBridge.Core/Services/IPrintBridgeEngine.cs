using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// The operations a desktop shell may ask the engine to perform. Every member delegates to the existing
/// <see cref="PrintBridgeRuntime"/> behavior; polling, claiming, retry, history and token handling stay inside
/// the engine. <see cref="Start"/> is idempotent, so repeated requests can never create a second polling loop.
/// </summary>
public interface IPrintBridgeEngine : IPrintBridgeStatusSource
{
    bool IsRunning { get; }

    void Start();

    Task StopAsync();

    Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct);

    /// <summary>Checks an unsaved server URL and token without changing settings or engine state.</summary>
    Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> CheckConnectionAsync(WaslaOptions candidate, CancellationToken ct);

    /// <summary>Saves a connection that <see cref="CheckConnectionAsync"/> verified and makes it the active one.</summary>
    Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionAsync(
        WaslaOptions verified,
        WaslaPrintBridgeClient.PrintBridgeHealthResult health,
        bool startListening,
        CancellationToken abandon);

    Task ResetConnectionForReconnectAsync();

    Task TestPrinterAsync(CancellationToken ct);

    IReadOnlyList<LocalPrintJobRecord> GetPrintHistory(PrintHistoryDateFilter filter, string? orderSearch);

    Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct);
}
