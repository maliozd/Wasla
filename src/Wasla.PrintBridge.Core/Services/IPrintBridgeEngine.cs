using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// Everything the app may ask of the engine (WAS-61). The engine runs in the Print Bridge service; the app reaches it
/// through <c>PrintBridgeServiceClient</c> over the local pipe, and the service implements it with
/// <see cref="Hosting.PrintBridgeEngineHost"/>. Polling, claiming, printing, retries, history, settings and the device
/// token stay behind this interface; no member returns the token.
/// </summary>
public interface IPrintBridgeEngine : IPrintBridgeStatusSource
{
    bool IsRunning { get; }

    /// <summary>The saved settings without secrets, as the engine uses them.</summary>
    IPrintBridgeSettingsView Settings { get; }

    /// <summary>Raised when a confirmed move finished in the background (no setup link was open).</summary>
    event EventHandler<PrintBridgeAutoSetupOutcome>? BackgroundSetupCompleted;

    /// <summary>Starts printing (idempotent) and clears "stopped by the user".</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>The user's explicit Stop: stops printing (WAS-56) and keeps it stopped across service restarts.</summary>
    Task StopAsync();

    Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct);

    /// <summary>Checks an unsaved server URL and token without changing settings or engine state.</summary>
    Task<PrintBridgeConnectionVerification> CheckConnectionAsync(PrintBridgeConnectionCandidate candidate, CancellationToken ct);

    /// <summary>Saves a connection that <see cref="CheckConnectionAsync"/> verified and makes it the active one.</summary>
    Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionAsync(
        PrintBridgeConnectionVerification verification,
        bool startListening,
        CancellationToken abandon);

    Task ResetConnectionForReconnectAsync();

    Task TestPrinterAsync(CancellationToken ct);

    Task<PrintBridgeHistoryPage> QueryPrintHistoryAsync(
        PrintHistoryDateFilter filter,
        string? orderSearch,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// The user's answer for a job whose paper outcome is unknown (WAS-61): it printed, or it did not (so Wasla can print
    /// it again). Reported with the connection that claimed it; nothing is printed.
    /// </summary>
    Task ResolveUncertainJobAsync(Guid jobId, bool printed, CancellationToken ct);

    /// <summary>The printers the engine can print to (for the service: those installed for the machine).</summary>
    Task<IReadOnlyList<string>> ListPrintersAsync(CancellationToken ct);

    /// <summary>Saves the printer. Returns null when saved, otherwise the resource key of the reason.</summary>
    Task<string?> SavePrinterAsync(string printerName, CancellationToken ct);

    /// <summary>Saves test mode and the poll intervals. Returns null when saved, otherwise the resource key of the reason.</summary>
    Task<string?> SaveOperationalSettingsAsync(PrintBridgeOperationalSettings settings, CancellationToken ct);

    Task SaveLanguageAsync(string culture, CancellationToken ct);

    /// <summary>
    /// Applies a <c>wasla-printbridge://setup</c> link, asking <paramref name="consent"/> before a move to another
    /// restaurant, then verifies a saved connection and resumes printing.
    /// </summary>
    Task<PrintBridgeAutoSetupOutcome> ApplySetupLinkAsync(
        string link,
        PrintBridgeConnectionTransferConsent consent,
        CancellationToken ct);
}

/// <summary>The saved settings a client may know. The device token is only ever "saved" or "not saved".</summary>
public interface IPrintBridgeSettingsView
{
    string ServerUrl { get; }

    bool HasSavedToken { get; }

    PrintBridgeOptions Bridge { get; }

    UiOptions Ui { get; }

    /// <summary>Whether the saved printer is chosen and installed (test mode does not make a missing printer usable).</summary>
    bool IsPrinterUsable { get; }
}

/// <summary>A server URL and token to check. <see cref="KeepSavedToken"/> checks the saved token, which the client never knows.</summary>
public sealed record PrintBridgeConnectionCandidate(string ServerUrl, string Token, bool KeepSavedToken)
{
    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"{nameof(PrintBridgeConnectionCandidate)} {{ ServerUrl = {ServerUrl}, KeepSavedToken = {KeepSavedToken} }}";
}

/// <summary>A candidate the server accepted, kept by the engine under <see cref="VerificationId"/> until it is applied.</summary>
public sealed record PrintBridgeConnectionVerification(
    string VerificationId,
    WaslaPrintBridgeClient.PrintBridgeHealthResult Health);

public sealed record PrintBridgeOperationalSettings(bool TestMode, int IdlePollSeconds, int BusyPollSeconds, int ErrorPollSeconds);

/// <summary>
/// One page of local history. Rows from the engine carry a <see cref="LocalPrintJobRecord.FailureCategory"/> and no
/// error text.
/// </summary>
public sealed record PrintBridgeHistoryPage(int Total, int Page, IReadOnlyList<LocalPrintJobRecord> Rows);
