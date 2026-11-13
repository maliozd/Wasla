using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using static Wasla.PrintBridge.Services.WaslaPrintBridgeClient;

namespace Wasla.PrintBridge.Setup;

public enum PrintBridgeAutoSetupOutcome
{
    /// <summary>Settings saved and the connection was verified.</summary>
    Connected,

    /// <summary>Connection verified, but a printer must still be selected before printing can start.</summary>
    ConnectedPrinterMissing,

    /// <summary>Settings saved, but the connection could not be verified.</summary>
    SavedButUnverified,

    /// <summary>The setup link was invalid or expired.</summary>
    InvalidOrExpired,

    /// <summary>Setup failed for another reason.</summary>
    Failed,

    /// <summary>
    /// Refused because a print job is still being completed. Nothing was exchanged, saved or changed, and the setup code
    /// was not used, so the same link can be opened again once printing has finished.
    /// </summary>
    PrintingInProgress,

    /// <summary>The user did not approve the move to another restaurant (Cancel, Escape, closing). Nothing was sent or changed.</summary>
    Cancelled,

    /// <summary>
    /// The saved connection could not be checked with its own server (unreachable, a server setup links cannot verify,
    /// or one without moves), so the link was not applied. Nothing was sent to the link's server and nothing changed.
    /// </summary>
    CurrentConnectionUnverified,

    /// <summary>The new restaurant has no room for another Print Bridge device. Nothing changed.</summary>
    DeviceLimitReached,

    /// <summary>This computer is also registered to a device the move may not retire. Nothing changed.</summary>
    InstallationConflict,

    /// <summary>The new restaurant's reconnect device is turned off, so the move would end on a device that cannot print. Nothing changed.</summary>
    DestinationDeviceDisabled,

    /// <summary>
    /// A move approved earlier was finished and saved first; the link just opened was not applied and can be opened
    /// again.
    /// </summary>
    PreviousChangeCompleted,

    /// <summary>
    /// A confirmed move may have been stored by the server, but its result is not saved here yet (no answer, or the
    /// settings could not be written). The pending change is kept and the identical move is repeated later.
    /// </summary>
    ChangePending,

    /// <summary>The link is not a setup link of a trusted Wasla server (WAS-61: decided by the service). Nothing was sent.</summary>
    InvalidLink
}

/// <summary>What the native confirmation shows: names verified by the server and server hosts. No secret.</summary>
public sealed record PrintBridgeConnectionTransferConsentRequest(
    string CurrentTenantName,
    string CurrentServer,
    string TargetTenantName,
    string TargetServer);

/// <summary>Asks the user, natively, whether to move Print Bridge. Anything but an explicit approval is a refusal.</summary>
public delegate Task<bool> PrintBridgeConnectionTransferConsent(
    PrintBridgeConnectionTransferConsentRequest request,
    CancellationToken ct);

/// <summary>
/// Executes the automatic Print Bridge configuration after a valid <c>wasla-printbridge://setup</c>
/// URI is received: exchange the one-time code, persist settings using the existing settings store,
/// run the existing health ping, and report completion. Reuses the existing persistence — it does
/// not introduce a competing settings system.
/// <para>
/// A setup link replaces the server and token, so it is applied only when no print job is active
/// (<see cref="IPrintBridgeConnectionGuard"/>); otherwise it is refused before the code is used. A job claimed from one
/// server is therefore never reported to another, and the new token is never kept for later.
/// </para>
/// <para>
/// WAS-91: a link never silently replaces a verified connection to another restaurant. With a saved connection, its own
/// server is asked first (read-only, with the saved token, which never goes to the link's server) whether the link
/// belongs to the same restaurant. The same restaurant, or a token the server already rejects, takes the ordinary
/// exchange, where the server refuses a computer still registered to another restaurant. Another restaurant needs the
/// user's native approval, and then the server-authorized move: its secrets are generated here and written to a pending
/// change before the move is requested, so any lost answer, restart or failed settings write ends in the same, saved
/// result when the identical move is repeated (<see cref="RecoverPendingChangeAsync"/>).
/// </para>
/// </summary>
public sealed class PrintBridgeAutoSetupCoordinator
{
    private readonly WaslaPrintBridgeClient _client;
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly IPrintBridgeConnectionGuard _guard;
    private readonly PrintBridgeServerTrustPolicy _trustPolicy;
    private readonly ILogger<PrintBridgeAutoSetupCoordinator> _logger;

    public PrintBridgeAutoSetupCoordinator(
        WaslaPrintBridgeClient client,
        PrintBridgeSettingsStore store,
        PrintBridgeSettingsHolder holder,
        IPrintBridgeConnectionGuard guard,
        PrintBridgeServerTrustPolicy trustPolicy,
        ILogger<PrintBridgeAutoSetupCoordinator> logger)
    {
        _client = client;
        _store = store;
        _holder = holder;
        _guard = guard;
        _trustPolicy = trustPolicy;
        _logger = logger;
    }

    /// <summary>Waits before repeating a move whose answer was lost; one attempt more than delays.</summary>
    internal IReadOnlyList<TimeSpan> TransferRetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>Bounds the network work before and, separately, after the confirmation; the user's decision has no limit.</summary>
    internal TimeSpan PhaseTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>Applies a link without a way to ask the user: a move to another restaurant is then refused (<see cref="PrintBridgeAutoSetupOutcome.Cancelled"/>).</summary>
    public Task<PrintBridgeAutoSetupOutcome> ApplyAsync(PrintBridgeProtocolSetupRequest request, CancellationToken ct) =>
        ApplyAsync(request, consent: null, ct);

    public async Task<PrintBridgeAutoSetupOutcome> ApplyAsync(
        PrintBridgeProtocolSetupRequest request,
        PrintBridgeConnectionTransferConsent? consent,
        CancellationToken ct)
    {
        // The parser already checked the link's server; a request built elsewhere is held to the same policy.
        if (!_trustPolicy.IsTrusted(request.ServerUrl))
        {
            _logger.LogWarning("Automatic setup refused: the setup link's server is not trusted; nothing was changed.");
            return PrintBridgeAutoSetupOutcome.InvalidOrExpired;
        }

        // 0. No job may be active, and none may start until the new connection is in place.
        var change = _guard.TryBeginConnectionChange();
        if (change is null)
        {
            _logger.LogInformation("Automatic setup refused because a print job is still being completed; nothing was changed.");
            return PrintBridgeAutoSetupOutcome.PrintingInProgress;
        }

        PreparedTransfer prepared;
        try
        {
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
            phase.CancelAfter(PhaseTimeout);

            // A move confirmed earlier is finished (or given up) before anything else changes the connection.
            var recovered = await RecoverWhileJobsAreHeldAsync(change, phase.Token, request.Code).ConfigureAwait(false);
            if (recovered is not null)
                return recovered.Value;

            var decision = await DecideWhileJobsAreHeldAsync(request, change, phase.Token).ConfigureAwait(false);
            if (decision.Outcome is { } settled)
                return settled;

            prepared = decision.Transfer!;
        }
        finally
        {
            // Released while the user decides: printing for the current restaurant goes on meanwhile.
            change.Dispose();
        }

        if (consent is null || !await consent(prepared.Consent, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Moving Print Bridge to another restaurant was not approved; nothing was sent or changed.");
            return PrintBridgeAutoSetupOutcome.Cancelled;
        }

        ct.ThrowIfCancellationRequested();

        // The same decision as before the question: a job may have started while the user decided.
        change = _guard.TryBeginConnectionChange();
        if (change is null)
        {
            _logger.LogInformation("Approved move refused because a print job is still being completed; nothing was changed.");
            return PrintBridgeAutoSetupOutcome.PrintingInProgress;
        }

        try
        {
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
            phase.CancelAfter(PhaseTimeout);
            return await TransferWhileJobsAreHeldAsync(request, prepared, change, ct, phase.Token).ConfigureAwait(false);
        }
        finally
        {
            change.Dispose();
        }
    }

    /// <summary>
    /// Finishes a move that was confirmed earlier but whose result is not saved yet (after a restart, a lost answer or a
    /// failed settings write). Returns null when there is none, when it was given up, or when a print job is active;
    /// otherwise the outcome to report.
    /// </summary>
    public async Task<PrintBridgeAutoSetupOutcome?> RecoverPendingChangeAsync(CancellationToken ct)
    {
        if (!_store.PendingConnectionChangeExists())
            return null;

        var change = _guard.TryBeginConnectionChange();
        if (change is null)
            return PrintBridgeAutoSetupOutcome.ChangePending;

        try
        {
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
            phase.CancelAfter(PhaseTimeout);
            return await RecoverWhileJobsAreHeldAsync(change, phase.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PrintBridgeAutoSetupOutcome.ChangePending;
        }
        finally
        {
            change.Dispose();
        }
    }

    /// <summary>
    /// Null when there is no pending move, or when it was given up or found stale (and deleted): the caller goes on.
    /// Otherwise the outcome: the move saved now, or <see cref="PrintBridgeAutoSetupOutcome.ChangePending"/> while its
    /// result is still not known.
    /// </summary>
    private async Task<PrintBridgeAutoSetupOutcome?> RecoverWhileJobsAreHeldAsync(
        IDisposable change,
        CancellationToken ct,
        string? openedLinkCode = null)
    {
        PrintBridgePendingConnectionChange? pending;
        try
        {
            pending = _store.LoadPendingConnectionChange();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by another program for now: kept, and tried again later. Nothing else may change the connection.
            _logger.LogWarning(ex, "The pending connection change could not be read now; it is kept and tried again.");
            return PrintBridgeAutoSetupOutcome.ChangePending;
        }

        if (pending is null)
            return null;

        var (connection, _, _) = _holder.Snapshot();
        if (string.Equals(connection.AgentToken, pending.NewToken, StringComparison.Ordinal))
        {
            // The result was saved already; only the pending file was left behind.
            TryDeletePendingChange();
            return null;
        }

        if (!IsSourceConnection(connection, pending))
        {
            // The connection was changed by hand meanwhile: that choice wins, the old move is not applied over it.
            _logger.LogInformation("A pending connection change no longer matches the saved connection; it was discarded.");
            TryDeletePendingChange();
            return null;
        }

        _logger.LogInformation("Repeating a confirmed connection change whose result was not saved.");
        var reply = await TransferWithRetriesAsync(pending, ct).ConfigureAwait(false);
        if (reply.Kind == SetupTransferReplyKind.Unknown)
            return PrintBridgeAutoSetupOutcome.ChangePending;

        if (reply.Kind != SetupTransferReplyKind.Ready)
        {
            // The server's own answer: the move was not stored (or was completed already). The settings stay as they are.
            _logger.LogInformation("A pending connection change was refused by the server; it was discarded. Reply={Reply}", reply.Kind);
            TryDeletePendingChange();
            return null;
        }

        var saved = await SaveTransferredConnectionAsync(pending, reply, change, ct).ConfigureAwait(false);

        // Finished while a different link was being opened: say so, rather than report that link as applied.
        return openedLinkCode is not null
               && saved != PrintBridgeAutoSetupOutcome.ChangePending
               && !string.Equals(openedLinkCode, pending.Code, StringComparison.Ordinal)
            ? PrintBridgeAutoSetupOutcome.PreviousChangeCompleted
            : saved;
    }

    /// <summary>
    /// With a saved connection, asks its own server about the link. Returns an outcome when the link was settled here
    /// (applied by the ordinary exchange, or refused), or the prepared move that needs the user's approval.
    /// </summary>
    private async Task<Decision> DecideWhileJobsAreHeldAsync(
        PrintBridgeProtocolSetupRequest request,
        IDisposable change,
        CancellationToken ct)
    {
        var (connection, bridge, _) = _holder.Snapshot();
        // Checked on a copy: the validator normalizes what it checks, and the saved options are shared.
        var saved = new WaslaOptions { ServerUrl = connection.ServerUrl, AgentToken = connection.AgentToken };
        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(saved, out _))
            return Decision.Settled(await ExchangeWhileJobsAreHeldAsync(request, change, ct).ConfigureAwait(false));

        if (!_trustPolicy.IsTrusted(connection.ServerUrl))
        {
            // The setup code is never sent to a server that setup links cannot verify, such as an address typed by hand.
            _logger.LogInformation("Automatic setup not applied: the saved server cannot be verified for a setup link; nothing was changed.");
            return Decision.Settled(PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified);
        }

        var preview = await _client
            .PreviewSetupTransferAsync(connection, bridge.InstallationId, request.ServerUrl, request.Code, ct)
            .ConfigureAwait(false);

        switch (preview.Kind)
        {
            case SetupTransferReplyKind.SameTenant:
            case SetupTransferReplyKind.SourceRejected:
                // The same restaurant: the existing reconnect. A token the server rejects is no verified connection to
                // keep; the server still refuses the exchange while this computer is registered to another restaurant.
                return Decision.Settled(await ExchangeWhileJobsAreHeldAsync(request, change, ct).ConfigureAwait(false));
            case SetupTransferReplyKind.Ready:
                return Decision.Move(new PreparedTransfer(
                    connection.ServerUrl,
                    connection.AgentToken,
                    bridge.InstallationId,
                    new PrintBridgeConnectionTransferConsentRequest(
                        preview.CurrentTenantName,
                        PrintBridgeServerTrustPolicy.DisplayHost(connection.ServerUrl),
                        preview.TargetTenantName,
                        PrintBridgeServerTrustPolicy.DisplayHost(request.ServerUrl))));
            default:
                _logger.LogInformation("Automatic setup not applied after the check with the saved server. Reply={Reply}", preview.Kind);
                return Decision.Settled(MapRefusal(preview.Kind));
        }
    }

    private async Task<PrintBridgeAutoSetupOutcome> TransferWhileJobsAreHeldAsync(
        PrintBridgeProtocolSetupRequest request,
        PreparedTransfer prepared,
        IDisposable change,
        CancellationToken lifetime,
        CancellationToken phase)
    {
        // The saved connection must still be the one the user agreed to leave.
        var (connection, bridge, _) = _holder.Snapshot();
        if (!string.Equals(connection.ServerUrl, prepared.SourceServerUrl, StringComparison.Ordinal)
            || !string.Equals(connection.AgentToken, prepared.SourceToken, StringComparison.Ordinal)
            || !string.Equals(bridge.InstallationId, prepared.InstallationId, StringComparison.Ordinal))
        {
            _logger.LogWarning("The connection changed while the move was being confirmed; nothing was sent or changed.");
            return PrintBridgeAutoSetupOutcome.Failed;
        }

        var pending = new PrintBridgePendingConnectionChange
        {
            SourceServerUrl = prepared.SourceServerUrl,
            SourceToken = prepared.SourceToken,
            InstallationId = prepared.InstallationId,
            SetupServerUrl = request.ServerUrl,
            Code = request.Code,
            NewToken = PrintBridgePendingConnectionChange.GenerateSecret(),
            CompletionCredential = PrintBridgePendingConnectionChange.GenerateSecret(),
            CreatedAtUtc = DateTime.UtcNow
        };

        // Nothing is requested before the pending change is on disk: the only working token is never given up without
        // a way to finish the move.
        try
        {
            _store.SavePendingConnectionChange(pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The pending connection change could not be written; the move was not requested.");
            return PrintBridgeAutoSetupOutcome.Failed;
        }

        SetupTransferReply reply;
        try
        {
            reply = await TransferWithRetriesAsync(pending, phase).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            return PrintBridgeAutoSetupOutcome.ChangePending;
        }

        if (reply.Kind == SetupTransferReplyKind.Unknown)
            return PrintBridgeAutoSetupOutcome.ChangePending;

        if (reply.Kind != SetupTransferReplyKind.Ready)
        {
            TryDeletePendingChange();
            _logger.LogInformation("The move to another restaurant was refused; nothing was changed. Reply={Reply}", reply.Kind);
            return MapRefusal(reply.Kind);
        }

        return await SaveTransferredConnectionAsync(pending, reply, change, phase).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests the move, repeating the identical request while no answer is known. Each repeat is safe: the server
    /// stores the move once and answers a repeat with the same result.
    /// </summary>
    private async Task<SetupTransferReply> TransferWithRetriesAsync(PrintBridgePendingConnectionChange pending, CancellationToken ct)
    {
        var delays = TransferRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            var reply = await _client.TransferSetupAsync(pending, ct).ConfigureAwait(false);
            if (reply.Kind != SetupTransferReplyKind.Unknown || attempt >= delays.Count)
                return reply;

            _logger.LogWarning("The move request got no answer; it is repeated. Attempt={Attempt}", attempt + 1);
            await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Saves the stored move: the new server and token replace the old ones in one settings write, then the pending
    /// change is deleted. A failed write keeps the pending change (and the old settings), so the move is finished later.
    /// </summary>
    private async Task<PrintBridgeAutoSetupOutcome> SaveTransferredConnectionAsync(
        PrintBridgePendingConnectionChange pending,
        SetupTransferReply reply,
        IDisposable change,
        CancellationToken ct)
    {
        var (_, bridge, ui) = _holder.Snapshot();
        var connection = new WaslaOptions
        {
            ServerUrl = reply.ServerUrl.Trim(),
            AgentToken = pending.NewToken
        };
        var updatedBridge = bridge.Clone();
        updatedBridge.DisplayName = reply.DeviceName.Trim();
        updatedBridge.ServerDeviceNameResolved = !string.IsNullOrWhiteSpace(reply.DeviceName);

        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(connection, out var errorKey))
        {
            _logger.LogWarning("The moved connection produced invalid settings. ErrorKey={ErrorKey}", errorKey);
            return PrintBridgeAutoSetupOutcome.ChangePending;
        }

        try
        {
            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                Wasla = connection,
                PrintBridge = updatedBridge,
                Ui = ui
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server stored the move, so the old token no longer works; the pending change finishes it later.
            _logger.LogWarning(ex, "The moved connection could not be saved; it is finished later from the pending change.");
            return PrintBridgeAutoSetupOutcome.ChangePending;
        }

        _holder.Replace(connection, updatedBridge, ui);
        TryDeletePendingChange();
        change.Dispose();
        _logger.LogInformation("Print Bridge moved to another restaurant and saved. SessionId={SessionId}", reply.SessionId);

        return await VerifyAndCompleteAsync(
                connection.ServerUrl,
                reply.SessionId,
                pending.CompletionCredential,
                updatedBridge,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<PrintBridgeAutoSetupOutcome> ExchangeWhileJobsAreHeldAsync(
        PrintBridgeProtocolSetupRequest request,
        IDisposable change,
        CancellationToken ct)
    {
        // 1. Exchange the one-time code over HTTPS for the real configuration (token never in the URI).
        var config = await _client.ExchangeSetupAsync(request.ServerUrl, request.Code, ct).ConfigureAwait(false);
        if (config is null)
            return PrintBridgeAutoSetupOutcome.InvalidOrExpired;

        // 2. Persist via the existing settings store + holder.
        var document = _store.Load();
        document.Wasla.ServerUrl = config.ServerUrl.Trim();
        document.Wasla.AgentToken = config.DeviceToken.Trim();

        if (!string.IsNullOrWhiteSpace(config.DeviceName))
        {
            document.PrintBridge.DisplayName = config.DeviceName.Trim();
            document.PrintBridge.ServerDeviceNameResolved = true;
        }

        if (config.InstallationId != Guid.Empty)
            document.PrintBridge.InstallationId = config.InstallationId.ToString("D");

        if (string.IsNullOrWhiteSpace(document.PrintBridge.MachineName))
            document.PrintBridge.MachineName = Environment.MachineName;

        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(document.Wasla, out var errorKey))
        {
            // Do not log the token; only the validation key.
            _logger.LogWarning("Automatic setup produced invalid settings. ErrorKey={ErrorKey}", errorKey);
            return PrintBridgeAutoSetupOutcome.Failed;
        }

        _store.Save(document);
        _holder.Replace(document.Wasla, document.PrintBridge, document.Ui);

        // The new connection is in place: jobs may be claimed from it again (the lease is idempotent).
        change.Dispose();

        // 3 and 4. Run the existing reconnect/ping flow and report completion.
        return await VerifyAndCompleteAsync(
                config.ServerUrl,
                config.SessionId,
                config.CompletionCredential,
                document.PrintBridge,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Runs the existing health ping on the saved connection, then reports completion (best effort).</summary>
    private async Task<PrintBridgeAutoSetupOutcome> VerifyAndCompleteAsync(
        string serverUrl,
        Guid sessionId,
        string completionCredential,
        PrintBridgeOptions bridge,
        CancellationToken ct)
    {
        var connected = false;
        try
        {
            await _client.TestHealthAsync(ct).ConfigureAwait(false);
            connected = true;
        }
        catch (PrintBridgeConnectionException)
        {
            connected = false;
        }
        catch (OperationCanceledException)
        {
            // The settings are saved already: a check cut short leaves them saved but unverified, never failed.
            connected = false;
        }

        await _client.CompleteSetupAsync(serverUrl, sessionId, completionCredential, connected, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Automatic Print Bridge setup applied. SessionId={SessionId}, Connected={Connected}",
            sessionId,
            connected);

        if (!connected)
            return PrintBridgeAutoSetupOutcome.SavedButUnverified;

        return PrintBridgeSettingsValidator.TryValidatePrinterAvailability(bridge, out _)
            ? PrintBridgeAutoSetupOutcome.Connected
            : PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing;
    }

    /// <summary>
    /// The saved connection is still the one the pending move leaves: the same server with its token, or with the token
    /// cleared after the server rejected it (the move retired it).
    /// </summary>
    private static bool IsSourceConnection(WaslaOptions connection, PrintBridgePendingConnectionChange pending) =>
        string.Equals(connection.ServerUrl.TrimEnd('/'), pending.SourceServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(connection.AgentToken)
            || string.Equals(connection.AgentToken, pending.SourceToken, StringComparison.Ordinal));

    private void TryDeletePendingChange()
    {
        try
        {
            _store.DeletePendingConnectionChange();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file is harmless: the next attempt sees the result already saved and deletes it.
            _logger.LogWarning(ex, "The pending connection change could not be deleted.");
        }
    }

    private static PrintBridgeAutoSetupOutcome MapRefusal(SetupTransferReplyKind kind) =>
        kind switch
        {
            SetupTransferReplyKind.InvalidOrExpired => PrintBridgeAutoSetupOutcome.InvalidOrExpired,
            SetupTransferReplyKind.PrintingInProgress => PrintBridgeAutoSetupOutcome.PrintingInProgress,
            SetupTransferReplyKind.DeviceLimitReached => PrintBridgeAutoSetupOutcome.DeviceLimitReached,
            SetupTransferReplyKind.InstallationConflict => PrintBridgeAutoSetupOutcome.InstallationConflict,
            SetupTransferReplyKind.DestinationDeviceDisabled => PrintBridgeAutoSetupOutcome.DestinationDeviceDisabled,
            SetupTransferReplyKind.SourceRejected or SetupTransferReplyKind.Unknown =>
                PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified,
            _ => PrintBridgeAutoSetupOutcome.Failed
        };

    private sealed record PreparedTransfer(
        string SourceServerUrl,
        string SourceToken,
        string InstallationId,
        PrintBridgeConnectionTransferConsentRequest Consent);

    private sealed record Decision(PrintBridgeAutoSetupOutcome? Outcome, PreparedTransfer? Transfer)
    {
        public static Decision Settled(PrintBridgeAutoSetupOutcome outcome) => new(outcome, null);

        public static Decision Move(PreparedTransfer transfer) => new(null, transfer);
    }
}
