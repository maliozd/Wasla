using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Services;

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
    PrintingInProgress
}

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
/// </summary>
public sealed class PrintBridgeAutoSetupCoordinator
{
    private readonly WaslaPrintBridgeClient _client;
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly IPrintBridgeConnectionGuard _guard;
    private readonly ILogger<PrintBridgeAutoSetupCoordinator> _logger;

    public PrintBridgeAutoSetupCoordinator(
        WaslaPrintBridgeClient client,
        PrintBridgeSettingsStore store,
        PrintBridgeSettingsHolder holder,
        IPrintBridgeConnectionGuard guard,
        ILogger<PrintBridgeAutoSetupCoordinator> logger)
    {
        _client = client;
        _store = store;
        _holder = holder;
        _guard = guard;
        _logger = logger;
    }

    public async Task<PrintBridgeAutoSetupOutcome> ApplyAsync(
        PrintBridgeProtocolSetupRequest request,
        CancellationToken ct)
    {
        // 0. No job may be active, and none may start until the new connection is in place.
        var change = _guard.TryBeginConnectionChange();
        if (change is null)
        {
            _logger.LogInformation("Automatic setup refused because a print job is still being completed; nothing was changed.");
            return PrintBridgeAutoSetupOutcome.PrintingInProgress;
        }

        try
        {
            return await ApplyWhileJobsAreHeldAsync(request, change, ct).ConfigureAwait(false);
        }
        finally
        {
            change.Dispose();
        }
    }

    private async Task<PrintBridgeAutoSetupOutcome> ApplyWhileJobsAreHeldAsync(
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
        document.OrderHub.ServerUrl = config.ServerUrl.Trim();
        document.OrderHub.AgentToken = config.DeviceToken.Trim();

        if (!string.IsNullOrWhiteSpace(config.DeviceName))
        {
            document.PrintBridge.DisplayName = config.DeviceName.Trim();
            document.PrintBridge.ServerDeviceNameResolved = true;
        }

        if (config.InstallationId != Guid.Empty)
            document.PrintBridge.InstallationId = config.InstallationId.ToString("D");

        if (string.IsNullOrWhiteSpace(document.PrintBridge.MachineName))
            document.PrintBridge.MachineName = Environment.MachineName;

        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(document.OrderHub, out var errorKey))
        {
            // Do not log the token; only the validation key.
            _logger.LogWarning("Automatic setup produced invalid settings. ErrorKey={ErrorKey}", errorKey);
            return PrintBridgeAutoSetupOutcome.Failed;
        }

        _store.Save(document);
        _holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);

        // The new connection is in place: jobs may be claimed from it again (the lease is idempotent).
        change.Dispose();

        // 3. Run the existing reconnect/ping flow.
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

        // 4. Report completion (best effort; never throws).
        await _client.CompleteSetupAsync(config.ServerUrl, config.SessionId, config.CompletionCredential, connected, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Automatic Print Bridge setup applied. SessionId={SessionId}, Connected={Connected}",
            config.SessionId,
            connected);

        if (!connected)
            return PrintBridgeAutoSetupOutcome.SavedButUnverified;

        return PrintBridgeSettingsValidator.TryValidatePrinterAvailability(document.PrintBridge, out _)
            ? PrintBridgeAutoSetupOutcome.Connected
            : PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing;
    }
}
