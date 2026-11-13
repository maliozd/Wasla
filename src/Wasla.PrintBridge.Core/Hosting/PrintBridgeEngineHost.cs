using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// The engine as the service runs it (WAS-61): the one <see cref="PrintBridgeRuntime"/>, the settings, history, setup
/// links and moves, and the work the tray app used to do around them (starting printing at start, finishing a pending
/// move, verifying a saved setup link). It implements <see cref="IPrintBridgeEngine"/>, which the service exposes over its
/// pipe; the app never creates one.
/// <para>
/// Every client-visible value is sanitized here: no token, no error text of a job, no raw exception message.
/// </para>
/// </summary>
public sealed class PrintBridgeEngineHost : IPrintBridgeEngine, IAsyncDisposable
{
    public const int MaxHistoryPageSize = 50;
    private const int MaxPendingVerifications = 16;
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SetupVerificationTimeout = TimeSpan.FromSeconds(30);

    private readonly PrintBridgeRuntime _runtime;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeAutoSetupCoordinator _coordinator;
    private readonly PrintBridgeServerTrustPolicy _trustPolicy;
    private readonly LocalPrintJobHistoryStore _history;
    private readonly ILogger<PrintBridgeEngineHost> _logger;
    private readonly Func<IReadOnlyList<string>> _listPrinters;
    private readonly ConcurrentDictionary<string, PendingVerification> _verifications = new(StringComparer.Ordinal);
    private readonly object _settingsWrite = new();
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _pendingChangeLoopRunning;
    private Task _pendingChangeLoop = Task.CompletedTask;

    public PrintBridgeEngineHost(
        PrintBridgeRuntime runtime,
        PrintBridgeSettingsHolder holder,
        PrintBridgeSettingsStore store,
        PrintBridgeAutoSetupCoordinator coordinator,
        PrintBridgeServerTrustPolicy trustPolicy,
        LocalPrintJobHistoryStore history,
        ILogger<PrintBridgeEngineHost> logger)
        : this(runtime, holder, store, coordinator, trustPolicy, history, logger, RawPrinterHelper.ListInstalledPrinters)
    {
    }

    internal PrintBridgeEngineHost(
        PrintBridgeRuntime runtime,
        PrintBridgeSettingsHolder holder,
        PrintBridgeSettingsStore store,
        PrintBridgeAutoSetupCoordinator coordinator,
        PrintBridgeServerTrustPolicy trustPolicy,
        LocalPrintJobHistoryStore history,
        ILogger<PrintBridgeEngineHost> logger,
        Func<IReadOnlyList<string>> listPrinters)
    {
        _runtime = runtime;
        _holder = holder;
        _store = store;
        _coordinator = coordinator;
        _trustPolicy = trustPolicy;
        _history = history;
        _logger = logger;
        _listPrinters = listPrinters;
        _runtime.StatusChanged += OnRuntimeStatusChanged;
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<PrintBridgeAutoSetupOutcome>? BackgroundSetupCompleted;

    /// <summary>How often a pending move is tried again while its server cannot be reached.</summary>
    internal TimeSpan PendingChangeRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsRunning => _runtime.IsRunning;

    public IPrintBridgeSettingsView Settings => _holder;

    /// <summary>The runtime, for the service's own lifecycle and for tests.</summary>
    internal PrintBridgeRuntime Runtime => _runtime;

    /// <summary>
    /// Called once by the service after it took the engine lock: starts printing when a connection and a printer are saved
    /// and the user did not stop it, and finishes a move confirmed before a restart (WAS-91).
    /// </summary>
    public void Activate()
    {
        var (connection, bridge, _) = _holder.Snapshot();
        if (bridge.PrintingStoppedByUser)
        {
            _logger.LogInformation("Printing was stopped by the user; the service does not start it on its own.");
        }
        else if (PrintBridgeSettingsValidator.TryValidate(
                     new WaslaOptions { ServerUrl = connection.ServerUrl, AgentToken = connection.AgentToken },
                     bridge.Clone(),
                     out var reason))
        {
            try
            {
                _runtime.Start();
            }
            catch (LocalizedApplicationException ex)
            {
                _logger.LogInformation("Printing was not started at service start. Reason={Reason}", ex.ResourceKey);
            }
        }
        else
        {
            _logger.LogInformation("Printing was not started at service start. Reason={Reason}", reason);
        }

        if (_store.PendingConnectionChangeExists())
            StartPendingConnectionChangeLoop();
    }

    public PrintBridgeRuntimeStatus GetStatus()
    {
        var status = _runtime.GetStatus();
        return new PrintBridgeRuntimeStatus
        {
            IsRunning = status.IsRunning,
            IsConnected = status.IsConnected,
            LastSuccessfulContactUtc = status.LastSuccessfulContactUtc,
            LastPollUtc = status.LastPollUtc,
            LastIssue = status.LastIssue is null ? null : status.LastIssue with { RawMessage = null },
            LastError = status.LastError,
            ServerUrl = status.ServerUrl,
            PrinterName = status.PrinterName,
            LocalDeviceName = status.LocalDeviceName,
            DisplayName = status.DisplayName,
            ServerDeviceNameResolved = status.ServerDeviceNameResolved,
            MachineName = status.MachineName,
            AppVersion = status.AppVersion,
            DryRun = status.DryRun,
            RecentJobs = status.RecentJobs.Select(LocalPrintJobFailure.ForClient).ToArray(),
            JobsTodayCount = status.JobsTodayCount,
            FailedTodayCount = status.FailedTodayCount,
            LastPrintTimeUtc = status.LastPrintTimeUtc,
            ServerConnectionStatus = status.ServerConnectionStatus,
            PrinterHealthStatus = status.PrinterHealthStatus,
            TrayIconState = status.TrayIconState,
            UncertainJobsCount = status.UncertainJobsCount
        };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_runtime.IsRunning)
        {
            SetStoppedByUser(false);
            return;
        }

        if (PrintBridgePollingGate.RequiresReconnectBeforeStart(_holder.Connection.AgentToken, _runtime.GetStatus().LastIssue))
            throw new LocalizedApplicationException("Message.ReconnectFromWebToContinue");

        // Start checks the printer with Windows; keep that off the caller's thread.
        await Task.Run(_runtime.Start, ct).ConfigureAwait(false);
        SetStoppedByUser(false);
    }

    public async Task StopAsync()
    {
        // Saved first, so a service stop or crash during the grace period cannot start printing again.
        SetStoppedByUser(true);
        await _runtime.StopAsync().ConfigureAwait(false);
    }

    public Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct) =>
        _runtime.TestConnectionAsync(ct);

    public async Task<PrintBridgeConnectionVerification> CheckConnectionAsync(PrintBridgeConnectionCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var token = candidate.KeepSavedToken ? _holder.Connection.AgentToken?.Trim() ?? string.Empty : candidate.Token?.Trim() ?? string.Empty;
        var options = new WaslaOptions { ServerUrl = candidate.ServerUrl?.Trim() ?? string.Empty, AgentToken = token };
        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(options, out var errorKey))
            throw new LocalizedApplicationException(errorKey!);

        var health = await _runtime.CheckConnectionAsync(options, ct).ConfigureAwait(false);

        PruneVerifications();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _verifications[id] = new PendingVerification(options, health, DateTime.UtcNow + VerificationLifetime);
        return new PrintBridgeConnectionVerification(id, health);
    }

    public async Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionAsync(
        PrintBridgeConnectionVerification verification,
        bool startListening,
        CancellationToken abandon)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (!_verifications.TryRemove(verification.VerificationId, out var pending) || pending.ExpiresAtUtc < DateTime.UtcNow)
            throw new LocalizedApplicationException("Shell.Setup.CheckExpired");

        var change = await _runtime.ApplyVerifiedConnectionAsync(pending.Options, pending.Health, startListening, abandon)
            .ConfigureAwait(false);
        if (_runtime.IsRunning)
            SetStoppedByUser(false);
        return change;
    }

    public Task ResetConnectionForReconnectAsync() => _runtime.ResetConnectionForReconnectAsync();

    public Task TestPrinterAsync(CancellationToken ct) => _runtime.TestPrinterAsync(ct);

    public Task<PrintBridgeHistoryPage> QueryPrintHistoryAsync(
        PrintHistoryDateFilter filter,
        string? orderSearch,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var size = Math.Clamp(pageSize, 1, MaxHistoryPageSize);
        var search = string.IsNullOrWhiteSpace(orderSearch) ? null : orderSearch.Trim();
        var jobs = _history.Query(filter, search);
        var total = jobs.Count;
        var lastPage = total == 0 ? 0 : (total - 1) / size;
        var current = Math.Clamp(page, 0, lastPage);
        var rows = jobs.Skip(current * size).Take(size).Select(LocalPrintJobFailure.ForClient).ToArray();
        return Task.FromResult(new PrintBridgeHistoryPage(total, current, rows));
    }

    public Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct) =>
        _runtime.ReprintJobAsync(jobId, ct);

    public Task ResolveUncertainJobAsync(Guid jobId, bool printed, CancellationToken ct) =>
        _runtime.ResolveUncertainJobAsync(jobId, printed, ct);

    public async Task<IReadOnlyList<string>> ListPrintersAsync(CancellationToken ct)
    {
        // Spooler enumeration can block on network printers.
        var printers = await Task.Run(_listPrinters, ct).ConfigureAwait(false);
        return printers
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string?> SavePrinterAsync(string printerName, CancellationToken ct)
    {
        var name = printerName?.Trim() ?? string.Empty;
        var installed = await ListPrintersAsync(ct).ConfigureAwait(false);
        if (!installed.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            return "Shell.Printer.NotInstalled";

        string? errorKey = null;
        var saved = UpdateBridge(bridge =>
        {
            // A new options object: a job already being printed keeps the printer it captured when it was claimed.
            bridge.PrinterMode = "WindowsPrinter";
            bridge.PrinterName = name;
            return PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out errorKey);
        });
        return saved ? null : errorKey ?? "Shell.Op.Failed";
    }

    public Task<string?> SaveOperationalSettingsAsync(PrintBridgeOperationalSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? errorKey = null;
        // The engine reads these for every poll and job, so they apply from its next check without a restart.
        var saved = UpdateBridge(bridge =>
        {
            bridge.DryRun = settings.TestMode;
            bridge.IdlePollIntervalSeconds = settings.IdlePollSeconds;
            bridge.BusyPollIntervalSeconds = settings.BusyPollSeconds;
            bridge.ErrorPollIntervalSeconds = settings.ErrorPollSeconds;
            return PrintBridgeSettingsValidator.TryValidateAdvancedBehaviorSettings(bridge, out errorKey);
        });
        return Task.FromResult(saved ? null : errorKey ?? "Shell.Op.Failed");
    }

    public Task SaveLanguageAsync(string culture, CancellationToken ct)
    {
        if (culture is not ("tr-TR" or "en-US" or "ar-SA" or "ru-RU"))
            throw new LocalizedApplicationException("Shell.Op.Failed");

        lock (_settingsWrite)
        {
            _store.SaveLanguage(culture);
            var ui = _holder.Ui.Clone();
            ui.Language = culture;
            _holder.ReplaceUi(ui);
        }

        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    public async Task<PrintBridgeAutoSetupOutcome> ApplySetupLinkAsync(
        string link,
        PrintBridgeConnectionTransferConsent consent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!PrintBridgeProtocolUri.TryParseSetup(link, _trustPolicy, out var request, out _) || request is null)
            return PrintBridgeAutoSetupOutcome.InvalidLink;

        // One setup at a time, as the tray did: a second link while one (or a pending move) runs is refused, not queued.
        if (!await _setupGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new PrintBridgeEngineBusyException();

        PrintBridgeAutoSetupOutcome outcome;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            outcome = await _coordinator.ApplyAsync(request, consent, linked.Token).ConfigureAwait(false);
            if (IsSaved(outcome))
                await VerifyAndResumeAsync().ConfigureAwait(false);
        }
        finally
        {
            _setupGate.Release();
        }

        // A confirmed move whose result is not saved yet is finished in the background (WAS-91).
        if (outcome == PrintBridgeAutoSetupOutcome.ChangePending)
            StartPendingConnectionChangeLoop();

        RaiseStatusChanged();
        return outcome;
    }

    /// <summary>Whether an outcome saved a connection (the app's DescribeAutoSetupOutcome "Saved").</summary>
    public static bool IsSaved(PrintBridgeAutoSetupOutcome outcome) =>
        outcome is PrintBridgeAutoSetupOutcome.Connected
            or PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing
            or PrintBridgeAutoSetupOutcome.SavedButUnverified
            or PrintBridgeAutoSetupOutcome.PreviousChangeCompleted;

    /// <summary>The service is stopping: abandons setup work and stops printing (WAS-56 grace) without marking a user stop.</summary>
    public async Task ShutdownAsync()
    {
        if (!_lifetime.IsCancellationRequested)
            await _lifetime.CancelAsync().ConfigureAwait(false);

        await _runtime.StopAsync().ConfigureAwait(false);
        try
        {
            await _pendingChangeLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _runtime.StatusChanged -= OnRuntimeStatusChanged;
        _lifetime.Dispose();
    }

    private async Task VerifyAndResumeAsync()
    {
        using var verify = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        verify.CancelAfter(SetupVerificationTimeout);
        try
        {
            await _runtime.VerifyAndResumeAsync(verify.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (_runtime.IsRunning)
            SetStoppedByUser(false);
    }

    /// <summary>
    /// Finishes a confirmed move whose result is not saved yet (WAS-91), now and then every
    /// <see cref="PendingChangeRetryInterval"/> while the server cannot be reached. One loop at a time; a setup link running
    /// meanwhile finishes it itself first.
    /// </summary>
    private void StartPendingConnectionChangeLoop()
    {
        if (_lifetime.IsCancellationRequested || Interlocked.Exchange(ref _pendingChangeLoopRunning, 1) == 1)
            return;

        _pendingChangeLoop = Task.Run(RunAsync);

        async Task RunAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    if (await _setupGate.WaitAsync(0).ConfigureAwait(false))
                    {
                        PrintBridgeAutoSetupOutcome? outcome;
                        try
                        {
                            outcome = await _coordinator.RecoverPendingChangeAsync(_lifetime.Token).ConfigureAwait(false);
                            if (outcome is { } finished && finished != PrintBridgeAutoSetupOutcome.ChangePending && IsSaved(finished))
                                await VerifyAndResumeAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Finishing a pending connection change failed; it is tried again later.");
                            outcome = PrintBridgeAutoSetupOutcome.ChangePending;
                        }
                        finally
                        {
                            _setupGate.Release();
                        }

                        if (outcome is null)
                            return;

                        if (outcome != PrintBridgeAutoSetupOutcome.ChangePending)
                        {
                            RaiseStatusChanged();
                            BackgroundSetupCompleted?.Invoke(this, outcome.Value);
                            return;
                        }
                    }

                    await Task.Delay(PendingChangeRetryInterval, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _pendingChangeLoopRunning, 0);
            }
        }
    }

    /// <summary>Changes a copy of the printing options and saves it, file first; returns false when the copy is refused.</summary>
    private bool UpdateBridge(Func<PrintBridgeOptions, bool> change)
    {
        lock (_settingsWrite)
        {
            var (connection, bridge, ui) = _holder.Snapshot();
            var updated = bridge.Clone();
            if (!change(updated))
                return false;

            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument { Wasla = connection, PrintBridge = updated, Ui = ui });
            _holder.Replace(connection, updated, ui);
        }

        RaiseStatusChanged();
        return true;
    }

    private void SetStoppedByUser(bool stopped)
    {
        if (_holder.Bridge.PrintingStoppedByUser == stopped)
            return;

        try
        {
            UpdateBridge(bridge =>
            {
                bridge.PrintingStoppedByUser = stopped;
                return true;
            });
            _logger.LogInformation("Printing stopped by the user: {Stopped}", stopped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Whether printing was stopped by the user could not be saved.");
        }
    }

    private void PruneVerifications()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, pending) in _verifications)
        {
            if (pending.ExpiresAtUtc < now)
                _verifications.TryRemove(id, out _);
        }

        while (_verifications.Count >= MaxPendingVerifications)
        {
            var oldest = _verifications.OrderBy(v => v.Value.ExpiresAtUtc).FirstOrDefault();
            if (oldest.Key is null)
                break;
            _verifications.TryRemove(oldest.Key, out _);
        }
    }

    private void OnRuntimeStatusChanged(object? sender, EventArgs e) => RaiseStatusChanged();

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private sealed record PendingVerification(
        WaslaOptions Options,
        WaslaPrintBridgeClient.PrintBridgeHealthResult Health,
        DateTime ExpiresAtUtc);
}

/// <summary>Another setup link or a pending move is being applied; the request is refused, not queued.</summary>
public sealed class PrintBridgeEngineBusyException : Exception
{
    public PrintBridgeEngineBusyException()
        : base("Another setup is in progress.")
    {
    }
}
