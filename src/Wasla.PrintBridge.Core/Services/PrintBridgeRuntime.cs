using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeRuntime : IDisposable, IPrintBridgeStatusSource, IPrintBridgeConnectionGuard
{
    private const int MaxRecentJobs = 50;

    private readonly WaslaPrintBridgeClient _client;
    private readonly ReceiptFormatter _formatter;
    private readonly IPrintSpooler _spooler;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeDeviceMetadataSync _deviceMetadataSync;
    private readonly LocalPrintJobHistoryStore _historyStore;
    private readonly PrintBridgeJobJournal _journal;
    private readonly AppVersionInfo _appVersion;
    private readonly ILogger<PrintBridgeRuntime> _logger;
    private readonly object _sync = new();
    private readonly List<LocalPrintJobRecord> _recentJobs = [];

    private PollingLoop? _loop;
    private Task _lastLoopTask = Task.CompletedTask;
    private bool _isRunning;
    private DateTime? _lastSuccessfulContactUtc;
    private DateTime? _lastPollUtc;
    private PrintBridgeRuntimeIssue? _lastIssue;

    // Guarded by _sync: jobs between their claim request and their last report, and connection changes in progress;
    // while any change is in progress the loop leaves new jobs pending.
    private int _jobsInProgress;
    private int _connectionChanges;

    // WAS-216: the last printer availability Windows reported, read by GetStatus without a spooler call under _sync.
    private readonly object _availabilitySync = new();
    private (string Printer, PrinterAvailability Value, DateTime AtUtc)? _availability;
    private (string Printer, Task Task)? _availabilityRefresh;
    private PrinterAvailabilityState? _lastLoggedAvailability;

    public PrintBridgeRuntime(
        WaslaPrintBridgeClient client,
        ReceiptFormatter formatter,
        IPrintSpooler spooler,
        PrintBridgeSettingsHolder holder,
        PrintBridgeSettingsStore store,
        PrintBridgeDeviceMetadataSync deviceMetadataSync,
        LocalPrintJobHistoryStore historyStore,
        AppVersionInfo appVersion,
        ILogger<PrintBridgeRuntime> logger,
        PrintBridgeJobJournal? journal = null)
    {
        _client = client;
        _formatter = formatter;
        _spooler = spooler;
        _holder = holder;
        _store = store;
        _deviceMetadataSync = deviceMetadataSync;
        _historyStore = historyStore;
        _journal = journal ?? new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance);
        _appVersion = appVersion;
        _logger = logger;
    }

    public event EventHandler? StatusChanged;

    /// <summary>Test-only: awaited during a connection change while new jobs are held, just before listening stops.</summary>
    internal Func<Task>? WhileJobsAreHeldForTests { get; set; }

    /// <summary>
    /// How long <see cref="StopAsync"/> waits for a print job that was already claimed. Such a job is not cancelled; if it
    /// takes longer, Stop returns and the job finishes in the background.
    /// </summary>
    internal TimeSpan StopGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits before repeating mark-printed after a server or network failure; one attempt more than delays.</summary>
    internal IReadOnlyList<TimeSpan> PrintedReportRetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// WAS-216: how long a job's own cycle watches its Windows jobs leave the queue before it hands them to the journal,
    /// which keeps watching on every later cycle. Bounded so a printer that holds a job does not hold the engine.
    /// </summary>
    internal TimeSpan SubmissionTrackingWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>WAS-216: how often the queue is read while a job's own cycle watches it.</summary>
    internal TimeSpan SubmissionTrackingInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>WAS-216: how old a printer availability may be before the status reads it again.</summary>
    internal TimeSpan AvailabilityMaxAge { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>WAS-216: how long a status request waits for a fresh availability before it shows the last one known.</summary>
    internal TimeSpan AvailabilityStatusWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Local history note of a receipt Windows finished sending to the printer (WAS-216; not proof of paper).</summary>
    public const string SentToPrinterNote = "Sent to the printer";

    /// <summary>Local history note of a receipt whose Windows job is still in the print queue (WAS-216).</summary>
    public const string InWindowsQueueNote = "In the Windows print queue";

    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
    }

    public PrintBridgeRuntimeStatus GetStatus()
    {
        // WAS-216: read before taking the lock; a slow spooler delays this status at most AvailabilityStatusWait and
        // never holds the engine's lock.
        var printerHealth = ResolvePrinterHealth(_holder.Snapshot().Bridge);
        lock (_sync)
        {
            var (current, bridge, _) = _holder.Snapshot();
            var connectionOptions = new WaslaOptions
            {
                ServerUrl = current.ServerUrl,
                AgentToken = current.AgentToken
            };
            var isConnectionConfigured = PrintBridgeSettingsValidator.TryValidateConnectionSettings(connectionOptions, out _);
            var recentJobs = _recentJobs.Select(CloneRecord).ToList();
            var hasRecentSuccessfulContact = _lastSuccessfulContactUtc.HasValue
                && DateTime.UtcNow - _lastSuccessfulContactUtc.Value <= TimeSpan.FromSeconds(60);
            var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
                hasRecentSuccessfulContact,
                _lastIssue);
            var trayIconState = PrintBridgeRuntimeStatus.ResolveTrayIconState(_isRunning, isConnected, recentJobs, _lastIssue);
            var today = DateTime.Today;

            return new PrintBridgeRuntimeStatus
            {
                IsRunning = _isRunning,
                IsConnected = isConnected,
                LastSuccessfulContactUtc = _lastSuccessfulContactUtc,
                LastPollUtc = _lastPollUtc,
                LastIssue = _lastIssue,
                LastError = _lastIssue?.EffectiveResourceKey,
                ServerUrl = current.ServerUrl,
                PrinterName = bridge.PrinterName,
                LocalDeviceName = bridge.DisplayName?.Trim() ?? string.Empty,
                DisplayName = bridge.DisplayName?.Trim() ?? string.Empty,
                ServerDeviceNameResolved = bridge.ServerDeviceNameResolved,
                MachineName = string.IsNullOrWhiteSpace(bridge.MachineName)
                    ? Environment.MachineName
                    : bridge.MachineName,
                AppVersion = _appVersion.Display,
                DryRun = bridge.DryRun,
                RecentJobs = recentJobs,
                JobsTodayCount = GetJobsTodayCount(today, recentJobs),
                FailedTodayCount = GetFailedTodayCount(today, recentJobs),
                LastPrintTimeUtc = _historyStore.GetLastPrintTimeUtc(),
                ServerConnectionStatus = PrintBridgeRuntimeStatus.ResolveServerConnectionStatus(
                    isConnectionConfigured,
                    _isRunning,
                    isConnected,
                    _lastIssue),
                PrinterHealthStatus = printerHealth,
                TrayIconState = trayIconState,
                UncertainJobsCount = _historyStore.CountWithStatus(LocalPrintJobStatus.Uncertain)
            };
        }
    }

    public void Start()
    {
        if (IsRunning)
            return;

        var (current, bridge, _) = _holder.Snapshot();
        if (!PrintBridgeSettingsValidator.TryValidatePrintingReadiness(current, bridge, out var errorKey))
            throw new LocalizedApplicationException(errorKey!);

        // Only a missing printer keeps listening from starting. An offline, paused or failing one starts it: jobs are not
        // claimed until Windows reports it ready (WAS-216). Checked before the lock, which a spooler call never holds.
        // A fresh read (not the status's bounded wait): starting is not a status request and must not guess. As before
        // WAS-216, a saved printer that Windows does not have refuses the start, also in test mode.
        if (!string.IsNullOrWhiteSpace(bridge.PrinterName)
            && ReadAvailability(bridge.PrinterName.Trim()).State == PrinterAvailabilityState.NotFound)
        {
            throw new LocalizedApplicationException("Error.PrinterUnavailable");
        }

        lock (_sync)
        {
            if (_isRunning)
                return;

            var loop = new PollingLoop();
            var previous = _lastLoopTask;
            loop.Task = Task.Run(() => RunAfterAsync(previous, loop));
            _loop = loop;
            _lastLoopTask = loop.Task;
            _isRunning = true;
        }

        _logger.LogInformation("Print Bridge polling started.");
        RaiseStatusChanged();
    }

    /// <summary>
    /// Stops listening. Polls and waits end at once and no new job is claimed. A job that was already claimed is not
    /// cancelled, because cancelling it between its claim and its last report can report a printed receipt as failed
    /// (WAS-56): it finishes printing and reporting, and Stop waits for it up to <see cref="StopGracePeriod"/>.
    /// </summary>
    public async Task StopAsync()
    {
        PollingLoop? loop;

        lock (_sync)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            loop = _loop;
            _loop = null;
            if (loop is not null)
                loop.StopRequested = true;
        }

        if (loop is not null)
        {
            try
            {
                await loop.Cancellation.CancelAsync().ConfigureAwait(false);
                await loop.Task.WaitAsync(StopGracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Print Bridge stopped listening while a print job is still finishing; it completes in the background.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Print Bridge loop ended with an error.");
            }
        }

        _logger.LogInformation("Print Bridge polling stopped.");
        RaiseStatusChanged();
    }

    public async Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> ValidateConnectionAsync(CancellationToken ct)
    {
        var health = await _client.TestHealthAsync(ct).ConfigureAwait(false);
        RecordVerifiedContact(health);
        return health;
    }

    /// <summary>
    /// Checks a server URL and device token that are not saved yet. Neither the settings nor the engine state
    /// change, so a rejected candidate never clears the saved token or marks the saved connection as failed.
    /// </summary>
    public Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> CheckConnectionAsync(WaslaOptions candidate, CancellationToken ct) =>
        _client.TestHealthAsync(candidate, ct);

    /// <summary>
    /// Makes a connection verified with <see cref="CheckConnectionAsync"/> the saved one. Listening stops while the
    /// settings change. The settings file is written before the in-memory settings are replaced, so a failed write
    /// changes nothing. A token change clears the server-assigned device name, and the verified contact is recorded so
    /// the status is connected at once. Listening resumes when it was running before, or when
    /// <paramref name="startListening"/> asks for it and printing is ready. Once <paramref name="abandon"/> is cancelled
    /// (the app is closing) nothing is written and listening is not restarted.
    /// <para>
    /// Stopping cancels the polling loop, and cancelling a job between its claim and its last report is the known Stop
    /// race (WAS-56): a printed job could be reported failed, and its remaining reports would use the new connection.
    /// So while a job is in progress the change is refused with <see cref="PrintBridgeConnectionChange.PrintingInProgress"/>
    /// before anything is stopped or written, and from that check until the loop has stopped no new job is claimed.
    /// </para>
    /// </summary>
    public async Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionAsync(
        WaslaOptions verified,
        WaslaPrintBridgeClient.PrintBridgeHealthResult health,
        bool startListening,
        CancellationToken abandon)
    {
        var change = TryBeginConnectionChange();
        if (change is null)
            return PrintBridgeConnectionChange.PrintingInProgress;

        try
        {
            return await ApplyVerifiedConnectionCoreAsync(verified, health, startListening, abandon, change).ConfigureAwait(false);
        }
        finally
        {
            change.Dispose();
        }
    }

    /// <inheritdoc />
    public IDisposable? TryBeginConnectionChange()
    {
        lock (_sync)
        {
            if (_jobsInProgress > 0)
                return null;

            _connectionChanges++;
        }

        return new ConnectionChangeLease(this);
    }

    private void EndConnectionChange()
    {
        lock (_sync)
            _connectionChanges--;
    }

    private async Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionCoreAsync(
        WaslaOptions verified,
        WaslaPrintBridgeClient.PrintBridgeHealthResult health,
        bool startListening,
        CancellationToken abandon,
        IDisposable change)
    {
        if (WhileJobsAreHeldForTests is { } whileHeld)
            await whileHeld().ConfigureAwait(false);

        var wasRunning = IsRunning;
        if (wasRunning)
            await StopAsync().ConfigureAwait(false);

        if (abandon.IsCancellationRequested)
            return PrintBridgeConnectionChange.Abandoned;

        var (current, bridge, ui) = _holder.Snapshot();
        var connection = new WaslaOptions
        {
            ServerUrl = verified.ServerUrl,
            AgentToken = verified.AgentToken
        };
        var updatedBridge = bridge.Clone();
        var tokenChanged = !string.Equals(current.AgentToken?.Trim(), connection.AgentToken, StringComparison.Ordinal);
        if (tokenChanged)
        {
            updatedBridge.DisplayName = string.Empty;
            updatedBridge.ServerDeviceNameResolved = false;
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
            _logger.LogWarning(ex, "Print Bridge connection settings could not be saved; the previous connection is kept.");
            change.Dispose();
            if (wasRunning)
                TryStartListening();
            return PrintBridgeConnectionChange.SaveFailed;
        }

        _holder.Replace(connection, updatedBridge, ui);
        _logger.LogInformation("Print Bridge connection settings saved after verification. TokenChanged={TokenChanged}", tokenChanged);
        RecordVerifiedContact(health);
        change.Dispose();

        if ((wasRunning || startListening) && !abandon.IsCancellationRequested)
            TryStartListening();

        return IsRunning
            ? PrintBridgeConnectionChange.AppliedListening
            : PrintBridgeConnectionChange.AppliedNotListening;
    }

    /// <summary>
    /// For settings saved outside the engine (automatic setup from a setup link): verifies the saved connection,
    /// records a failure so the status shows it, and resumes listening when the connection is usable.
    /// Returns whether the connection was verified.
    /// </summary>
    public async Task<bool> VerifyAndResumeAsync(CancellationToken ct)
    {
        try
        {
            await ValidateConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge connection verification after setup failed.");
            RecordConnectionFailure(ex);
            return false;
        }

        if (PrintBridgeRuntimeStatus.ShouldReportConnectionSuccess(GetStatus()))
        {
            try
            {
                if (!IsRunning)
                    Start();
            }
            catch (Exception ex)
            {
                RecordConnectionFailure(ex);
                _logger.LogWarning(ex, "Print Bridge polling could not be started after setup.");
            }
        }

        return true;
    }

    public void RecordConnectionFailure(Exception ex, bool applyCredentialFailureFallback = true)
    {
        var issue = ApplyFailure(ex, applyCredentialFailureFallback);

        if (issue.ShouldStopPolling)
            SuspendPollingAfterTerminalIssue();
        RaiseStatusChanged();
    }

    public async Task ResetConnectionForReconnectAsync()
    {
        if (_isRunning)
            await StopAsync().ConfigureAwait(false);

        ClearTokenForReconnectRequired();
        _deviceMetadataSync.MarkUnresolved();

        lock (_sync)
        {
            _lastSuccessfulContactUtc = null;
            _lastIssue = PrintBridgeConnectionReset.CreateReconnectRequiredIssue();
        }

        _logger.LogInformation("Print Bridge connection reset by user. Local token cleared; reconnect required.");
        RaiseStatusChanged();
    }

    public async Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            return await ValidateConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (PrintBridgeConnectionException ex) when (ex.IsTokenAuthFailure)
        {
            var issue = ApplyFailure(ex, applyCredentialFailureFallback: true);
            if (issue.ShouldStopPolling)
                SuspendPollingAfterTerminalIssue();
            RaiseStatusChanged();
            throw;
        }
    }

    public IReadOnlyList<LocalPrintJobRecord> GetPrintHistory(
        PrintHistoryDateFilter filter,
        string? orderSearch) =>
        _historyStore.Query(filter, orderSearch);

    public async Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct)
    {
        var entry = _historyStore.FindByJobId(jobId);
        if (entry is null || entry.Status != LocalPrintJobStatus.Printed)
            throw new LocalizedApplicationException("Reprint.NotAllowed");

        var result = await _client.RequestReprintAsync(jobId, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new LocalizedApplicationException(result.MessageKey);

        _logger.LogInformation(
            "Reprint requested. SourceJobId={SourceJobId}, NewJobId={NewJobId}",
            jobId,
            result.NewPrintJobId);

        return result;
    }

    public async Task TestPrinterAsync(CancellationToken ct)
    {
        var (_, bridge, _) = _holder.Snapshot();
        if (bridge.DryRun)
            throw new LocalizedApplicationException("Error.DryRunEnabled");

        if (!PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out var errorKey))
            throw new LocalizedApplicationException(errorKey!);

        var printerName = bridge.PrinterName.Trim();
        var availability = await RefreshAvailabilityAsync(printerName).ConfigureAwait(false);
        if (!availability.IsReady)
            throw new LocalizedApplicationException("Error.PrinterUnavailable");

        var printedAt = DateTime.Now.ToString("G", Wasla.Application.Printing.GregorianCulture.For(TestPrintCulture()));
        var receipt = $"Wasla Print Bridge{Environment.NewLine}Test print{Environment.NewLine}{printedAt}";
        var data = System.Text.Encoding.UTF8.GetBytes(receipt);
        // A test print is not a Wasla job: it is handed to Windows once and not tracked or reported.
        var result = await Task.Run(() => _spooler.Submit(printerName, "Wasla Test Print", data, static _ => { }), ct).ConfigureAwait(false);
        if (result.Outcome != SpoolSubmitOutcome.Spooled)
            throw new InvalidOperationException(result.Error ?? "Unable to write to printer.");
    }

    /// <summary>The selected printer's health for the status, from the cached availability (WAS-216).</summary>
    private PrinterHealthStatus ResolvePrinterHealth(PrintBridgeOptions bridge)
    {
        if (bridge.DryRun)
            return PrinterHealthStatus.DryRun;
        if (string.IsNullOrWhiteSpace(bridge.PrinterName))
            return PrinterHealthStatus.NotConfigured;

        return CurrentAvailability(bridge.PrinterName.Trim()) is { } availability
            ? ToHealth(availability)
            // The first check of this printer has not answered within AvailabilityStatusWait: the spooler is not responding.
            : PrinterHealthStatus.Error;
    }

    private static PrinterHealthStatus ToHealth(PrinterAvailability availability) => availability.State switch
    {
        PrinterAvailabilityState.Ready => PrinterHealthStatus.Ready,
        PrinterAvailabilityState.NotFound => PrinterHealthStatus.NotFound,
        PrinterAvailabilityState.Offline => PrinterHealthStatus.Offline,
        PrinterAvailabilityState.Paused => PrinterHealthStatus.Paused,
        _ => PrinterHealthStatus.Error
    };

    /// <summary>
    /// The availability a status shows: the cached one while it is fresh; otherwise one refresh (shared by concurrent
    /// callers) that the caller waits for at most <see cref="AvailabilityStatusWait"/>, falling back to the last one known.
    /// </summary>
    private PrinterAvailability? CurrentAvailability(string printerName)
    {
        Task refresh;
        lock (_availabilitySync)
        {
            if (_availability is { } cached
                && string.Equals(cached.Printer, printerName, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached.AtUtc < AvailabilityMaxAge)
            {
                return cached.Value;
            }

            if (_availabilityRefresh is { } running
                && !running.Task.IsCompleted
                && string.Equals(running.Printer, printerName, StringComparison.OrdinalIgnoreCase))
            {
                refresh = running.Task;
            }
            else
            {
                refresh = Task.Run(() => ReadAvailability(printerName));
                _availabilityRefresh = (printerName, refresh);
            }
        }

        try
        {
            refresh.Wait(AvailabilityStatusWait);
        }
        catch (AggregateException)
        {
            // ReadAvailability never throws; nothing to do.
        }

        lock (_availabilitySync)
        {
            return _availability is { } known && string.Equals(known.Printer, printerName, StringComparison.OrdinalIgnoreCase)
                ? known.Value
                : null;
        }
    }

    /// <summary>A fresh availability for the engine's own decisions (claiming and submitting); updates the cache.</summary>
    private Task<PrinterAvailability> RefreshAvailabilityAsync(string printerName) =>
        Task.Run(() => ReadAvailability(printerName));

    private PrinterAvailability ReadAvailability(string printerName)
    {
        PrinterAvailability value;
        try
        {
            value = _spooler.GetAvailability(printerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The printer queue could not be read. PrinterName={PrinterName}", printerName);
            value = new PrinterAvailability(PrinterAvailabilityState.Error);
        }

        lock (_availabilitySync)
            _availability = (printerName, value, DateTime.UtcNow);
        return value;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The saved app language, so the service (whose own culture is the machine's) dates a test print as the app did
    /// before WAS-61; the current culture when no supported language is saved.
    /// </summary>
    private System.Globalization.CultureInfo TestPrintCulture()
    {
        var language = _holder.Ui.Language?.Trim();
        if (language is "tr-TR" or "en-US" or "ar-SA" or "ru-RU")
            return System.Globalization.CultureInfo.GetCultureInfo(language);

        return System.Globalization.CultureInfo.CurrentCulture;
    }

    /// <summary>
    /// Settles the jobs an earlier run left in the journal (WAS-61), with the connection of this cycle, before new jobs are
    /// claimed. Only jobs claimed with this connection are reported (WAS-58). A job never printed is reported failed and a
    /// printed one printed (a repeat is answered skipped). One whose paper outcome is unknown (the process ended while the
    /// receipt was being sent to the printer) becomes <see cref="PrintJournalStage.Uncertain"/>: never printed again, never
    /// reported on its own, and shown in the app as needing a check until the user says whether it printed
    /// (<see cref="ResolveUncertainJobAsync"/>). The first cycle after a start settles every entry; later cycles retry an
    /// entry whose report failed for a network or server reason only after <see cref="JournalRetryInterval"/>. A failing
    /// report never stops polling. Runs on the loop, so no job of this engine is active meanwhile.
    /// <para>
    /// Settling an entry finishes the job it belongs to, so it counts as a job in progress (<see cref="TryBeginJob"/>): none
    /// starts while a connection change or an update's pause (WAS-63) holds the engine, and both wait for one that runs.
    /// </para>
    /// </summary>
    private async Task ResumeJournalAsync(PollingLoop loop, WaslaOptions connection, bool firstCycle)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _journal.Entries)
        {
            var belongs = entry.BelongsTo(connection);
            // A receipt still in the Windows queue (WAS-216) is looked at on every cycle: it is a local check, and its
            // report should follow soon after Windows finishes it.
            var inWindowsQueue = entry is { Stage: PrintJournalStage.Submitting, Submission: not null };
            if (belongs
                && (entry.Stage == PrintJournalStage.Uncertain
                    || (!firstCycle && !inWindowsQueue && now - entry.UpdatedAtUtc < JournalRetryInterval)))
            {
                continue;
            }

            if (!TryBeginJob(loop, connection))
                return;

            try
            {
                if (!belongs)
                {
                    // The token that claimed it is gone, and WAS-58 forbids reporting with another one: Wasla keeps the job
                    // in Printing, where it is resolved. An uncertain job stays visible locally, without the resolve action.
                    _logger.LogWarning(
                        "A job claimed with an earlier connection was not reported; that connection was replaced. JobId={JobId}, Stage={Stage}",
                        entry.JobId,
                        entry.Stage);
                    _journal.Remove(entry.JobId);
                    if (entry.Stage is PrintJournalStage.Uncertain or PrintJournalStage.Submitting)
                        RecordUncertain(entry.JobId, resolvableHere: false);
                    continue;
                }

                await SettleJournalEntryAsync(entry, connection).ConfigureAwait(false);
            }
            catch (PrintBridgeConnectionException ex) when (IsTransient(ex))
            {
                // The server may have stored the report or not: the entry stays (its time renewed, at the stage settling
                // reached) and is reported again after the retry interval. New jobs are still polled.
                var latest = _journal.Entries.FirstOrDefault(e => e.JobId == entry.JobId) ?? entry;
                _journal.Record(latest.JobId, latest.Stage, connection, latest.Submission);
                _logger.LogWarning(
                    ex,
                    "An interrupted job could not be reported yet; it is tried again later. JobId={JobId}, Stage={Stage}",
                    latest.JobId,
                    latest.Stage);
                return;
            }
            finally
            {
                EndJob();
            }
        }
    }

    private async Task SettleJournalEntryAsync(PrintJournalEntry entry, WaslaOptions connection)
    {
        if (entry is { Stage: PrintJournalStage.Submitting, Submission: { } submission })
        {
            await SettleSubmissionAsync(entry.JobId, connection, submission).ConfigureAwait(false);
            return;
        }

        switch (entry.Stage)
        {
            case PrintJournalStage.Submitting:
                _logger.LogWarning(
                    "Print Bridge stopped while a receipt was being sent to the printer; whether it printed is unknown. It is not printed again; it waits for the user's check. JobId={JobId}",
                    entry.JobId);
                _journal.Record(entry.JobId, PrintJournalStage.Uncertain, connection);
                RecordUncertain(entry.JobId, resolvableHere: true);
                break;

            case PrintJournalStage.NotPrinted:
            {
                var failed = await _client.MarkFailedAsync(
                    entry.JobId,
                    "Print Bridge stopped before this receipt was printed.",
                    CancellationToken.None,
                    connection).ConfigureAwait(false);
                _logger.LogInformation(
                    "Interrupted job reported failed (not printed). JobId={JobId}, Success={Success}, Skipped={Skipped}",
                    entry.JobId,
                    failed.Success,
                    failed.Skipped);
                _journal.Remove(entry.JobId);
                break;
            }

            case PrintJournalStage.Printed:
            {
                var printed = await _client.MarkPrintedAsync(entry.JobId, CancellationToken.None, connection).ConfigureAwait(false);
                _logger.LogInformation(
                    "Printed job reported after an interruption. JobId={JobId}, Success={Success}, Skipped={Skipped}",
                    entry.JobId,
                    printed.Success,
                    printed.Skipped);
                _journal.Remove(entry.JobId);
                break;
            }
        }
    }

    /// <summary>The job is shown in history as needing a check; it was claimed and possibly printed before an interruption.</summary>
    private void RecordUncertain(Guid jobId, bool resolvableHere)
    {
        var existing = _historyStore.FindByJobId(jobId);
        var record = new LocalPrintJobRecord
        {
            JobId = jobId,
            OrderId = existing?.OrderId ?? Guid.Empty,
            OrderDisplay = existing?.OrderDisplay,
            Platform = existing?.Platform,
            JobType = existing?.JobType,
            PrinterName = existing?.PrinterName,
            Status = LocalPrintJobStatus.Uncertain,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            LastAttemptAtUtc = DateTime.UtcNow,
            StatusNote = resolvableHere ? null : UncertainResolveInWaslaNote
        };
        _historyStore.Record(record);
        RaiseStatusChanged();
    }

    /// <summary>History note of an uncertain job that only Wasla can resolve (its connection was replaced).</summary>
    public const string UncertainResolveInWaslaNote = "Resolve in Wasla";

    /// <summary>
    /// WAS-216: a fresh look at the selected printer before polling. New jobs are claimed only when Windows reports no
    /// problem; test mode prints to a file and is always ready. A change is logged once.
    /// </summary>
    private async Task<bool> IsPrinterReadyForNewJobsAsync(PrintBridgeOptions bridge)
    {
        if (bridge.DryRun || string.IsNullOrWhiteSpace(bridge.PrinterName))
            return true;

        var availability = await RefreshAvailabilityAsync(bridge.PrinterName.Trim()).ConfigureAwait(false);
        if (_lastLoggedAvailability != availability.State)
        {
            _lastLoggedAvailability = availability.State;
            if (availability.IsReady)
            {
                _logger.LogInformation("Windows reports the printer ready; new jobs are claimed again. PrinterName={PrinterName}", bridge.PrinterName);
            }
            else
            {
                _logger.LogWarning(
                    "Windows reports the printer unavailable; new jobs stay pending until it is ready. PrinterName={PrinterName}, State={State}, Status=0x{Status:X8}, Attributes=0x{Attributes:X8}, Win32Error={Win32Error}",
                    bridge.PrinterName,
                    availability.State,
                    availability.Status,
                    availability.Attributes,
                    availability.Win32Error);
            }
        }

        return availability.IsReady;
    }

    /// <summary>WAS-216: the job is shown as needing the user's check; its order details are kept.</summary>
    private void MarkUncertainLocally(Guid jobId, string? reason)
    {
        lock (_sync)
        {
            var record = FindOrSeedRecentJob(jobId);
            record.Status = LocalPrintJobStatus.Uncertain;
            record.LastAttemptAtUtc = DateTime.UtcNow;
            record.ErrorMessage = reason;
            record.StatusNote = null;
            PersistToHistory(record);
        }

        RaiseStatusChanged();
    }

    /// <summary>WAS-216: the job waits in the Windows print queue; shown once (and again after a restart).</summary>
    private void ShowInWindowsQueue(Guid jobId)
    {
        lock (_sync)
        {
            var existing = _recentJobs.FirstOrDefault(j => j.JobId == jobId);
            if (existing is { Status: LocalPrintJobStatus.Printing, StatusNote: InWindowsQueueNote })
                return;
        }

        UpdateRecentJob(jobId, LocalPrintJobStatus.Printing, statusNote: InWindowsQueueNote);
    }

    /// <summary>The recent record of a job, or one rebuilt from history (a job of an earlier run). Called under <c>_sync</c>.</summary>
    private LocalPrintJobRecord FindOrSeedRecentJob(Guid jobId)
    {
        var existing = _recentJobs.FirstOrDefault(j => j.JobId == jobId);
        if (existing is not null)
            return existing;

        var stored = _historyStore.FindByJobId(jobId);
        var record = stored is null
            ? new LocalPrintJobRecord { JobId = jobId, CreatedAtUtc = DateTime.UtcNow, LastAttemptAtUtc = DateTime.UtcNow }
            : CloneRecord(stored);
        _recentJobs.Insert(0, record);
        if (_recentJobs.Count > MaxRecentJobs)
            _recentJobs.RemoveRange(MaxRecentJobs, _recentJobs.Count - MaxRecentJobs);
        return record;
    }

    /// <summary>
    /// WAS-216: whether a Windows job of this receipt can still print. Answers null when every job has left the queue,
    /// otherwise the resource key that explains why the receipt cannot be called "not printed" yet.
    /// </summary>
    private string? CheckNothingLeftInQueue(PrintJournalSubmission submission)
    {
        foreach (var copy in submission.Copies)
        {
            if (copy.Outcome == PrintCopyOutcome.NotSubmitted)
                continue;

            if (copy.WindowsJobId is { } windowsJobId)
            {
                var observation = _spooler.Observe(submission.PrinterName, windowsJobId, copy.DocumentName);
                if (observation.State == SpoolJobState.InQueue)
                    return "History.Uncertain.StillQueued";
                if (observation.State == SpoolJobState.Unknown)
                    return "History.Uncertain.QueueNotChecked";
            }
            else
            {
                var lookup = _spooler.Find(submission.PrinterName, copy.DocumentName);
                if (lookup.State == SpoolJobLookupState.Found)
                    return "History.Uncertain.StillQueued";
                if (lookup.State == SpoolJobLookupState.Unknown)
                    return "History.Uncertain.QueueNotChecked";
            }
        }

        return null;
    }

    /// <summary>
    /// The user checked the printer for a job whose paper outcome was unknown (WAS-61) and says whether it printed. The
    /// answer is reported with the connection that claimed the job, as any report (WAS-58): printed → <c>mark-printed</c>;
    /// not printed → <c>mark-failed</c>, so Wasla can print it again. Nothing is printed here. While it is reported, a
    /// connection change waits, as for a job in progress.
    /// </summary>
    public async Task ResolveUncertainJobAsync(Guid jobId, bool printed, CancellationToken ct)
    {
        var entry = _journal.Entries.FirstOrDefault(e => e.JobId == jobId && e.Stage == PrintJournalStage.Uncertain);
        var (current, _, _) = _holder.Snapshot();
        var connection = new WaslaOptions { ServerUrl = current.ServerUrl, AgentToken = current.AgentToken };
        if (entry is null || !entry.BelongsTo(connection))
            throw new LocalizedApplicationException("History.Uncertain.NotResolvable");

        // WAS-216: "it did not print" lets Wasla print the receipt again. While one of its Windows jobs is still in the
        // queue it can still print, and both would come out; so that answer waits until the queue no longer holds it.
        // "It printed" creates nothing new and is always accepted.
        if (!printed && entry.Submission is { } submission)
        {
            string? refusal;
            try
            {
                refusal = await Task.Run(() => CheckNothingLeftInQueue(submission), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The Windows print queue could not be checked. JobId={JobId}", jobId);
                refusal = "History.Uncertain.QueueNotChecked";
            }

            if (refusal is not null)
                throw new LocalizedApplicationException(refusal);
        }

        lock (_sync)
        {
            if (_connectionChanges > 0 || !IsSavedConnection(connection))
                throw new LocalizedApplicationException("History.Uncertain.NotResolvable");
            _jobsInProgress++;
        }

        try
        {
            var result = printed
                ? await _client.MarkPrintedAsync(jobId, ct, connection).ConfigureAwait(false)
                : await _client.MarkFailedAsync(jobId, "Not printed (checked by the user after an interruption).", ct, connection).ConfigureAwait(false);
            _logger.LogInformation(
                "Uncertain job resolved by the user. JobId={JobId}, Printed={Printed}, Success={Success}, Skipped={Skipped}",
                jobId,
                printed,
                result.Success,
                result.Skipped);
        }
        finally
        {
            EndJob();
        }

        _journal.Remove(jobId);
        var existing = _historyStore.FindByJobId(jobId);
        if (existing is not null)
        {
            existing.Status = printed ? LocalPrintJobStatus.Printed : LocalPrintJobStatus.Failed;
            existing.LastAttemptAtUtc = DateTime.UtcNow;
            existing.PrintedAtUtc = printed ? DateTime.UtcNow : existing.PrintedAtUtc;
            existing.StatusNote = printed ? "Confirmed printed by the user" : null;
            existing.ErrorMessage = printed ? null : "Not printed (checked by the user)";
            _historyStore.Record(existing);
        }

        RaiseStatusChanged();
    }

    /// <summary>How long a journal entry whose report failed waits before it is reported again within one run.</summary>
    internal TimeSpan JournalRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Runs a loop once the previous one has ended. A loop stopped while it finishes a claimed job may still be running,
    /// and two loops must never process jobs at the same time.
    /// </summary>
    private async Task RunAfterAsync(Task previous, PollingLoop loop)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Its own Stop already logged how it ended.
        }

        try
        {
            await RunLoopAsync(loop).ConfigureAwait(false);
        }
        finally
        {
            loop.Cancellation.Dispose();
        }
    }

    private async Task RunLoopAsync(PollingLoop loop)
    {
        var stoppingToken = loop.Cancellation.Token;
        var firstCycle = true;
        _logger.LogInformation("Wasla Print Bridge background loop started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var (current, bridge, _) = _holder.Snapshot();
            // One connection for the whole cycle: its poll, and every request of the jobs it claims (WAS-58).
            var connection = new WaslaOptions { ServerUrl = current.ServerUrl, AgentToken = current.AgentToken };
            var waitSeconds = Math.Max(1, bridge.IdlePollIntervalSeconds);
            var hadJobs = false;
            var hadError = false;

            lock (_sync)
                _lastPollUtc = DateTime.UtcNow;

            RaiseStatusChanged();

            try
            {
                var health = await _client.TestHealthAsync(connection, stoppingToken).ConfigureAwait(false);
                if (IsSavedConnection(connection))
                {
                    _deviceMetadataSync.ApplyFromHealth(health);
                    lock (_sync)
                    {
                        _lastSuccessfulContactUtc = DateTime.UtcNow;
                        _lastIssue = null;
                    }
                }

                // Jobs an earlier run claimed but did not finish reporting (WAS-61) are settled before new ones are claimed.
                await ResumeJournalAsync(loop, connection, firstCycle).ConfigureAwait(false);
                firstCycle = false;

                // WAS-216: while Windows reports the printer unavailable, new jobs stay Pending on the server, where they
                // are still printed later; claiming them now would only leave them waiting in Windows.
                IReadOnlyList<WaslaPrintBridgeClient.PendingPrintJobDto> jobs = [];
                if (await IsPrinterReadyForNewJobsAsync(bridge).ConfigureAwait(false))
                {
                    jobs = await _client.GetPendingJobsAsync(stoppingToken, connection).ConfigureAwait(false);
                    _logger.LogInformation("Polling result: pending job count={Count}", jobs.Count);
                }

                if (jobs.Count > 0)
                {
                    hadJobs = true;
                    foreach (var job in jobs)
                    {
                        // Stop, a connection change in progress, or a connection that changed since this poll: leave
                        // the job pending on the server that offered it.
                        if (!TryBeginJob(loop, connection))
                            break;

                        try
                        {
                            RegisterJobReceived(job);
                            // A job that is about to be claimed runs to its end without the loop's cancellation, and
                            // every one of its requests uses this cycle's connection.
                            // Stop only shortens how long its Windows jobs are watched here (WAS-216); the journal
                            // keeps watching them.
                            await ProcessJobAsync(job, connection, stoppingToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            EndJob();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!IsSavedConnection(connection))
            {
                // The connection was replaced during this cycle; a failure of the old one says nothing about the new.
                hadError = true;
                _logger.LogWarning(ex, "Print Bridge request to a replaced connection failed; the saved connection is not affected.");
            }
            catch (Exception ex)
            {
                hadError = true;
                var issue = ApplyFailure(ex, applyCredentialFailureFallback: true);

                _logger.LogWarning(ex, "Print Bridge polling/processing failed.");
                if (issue.ShouldStopPolling)
                {
                    SuspendPollingAfterTerminalIssue();
                    break;
                }
            }

            RaiseStatusChanged();

            var currentBridge = _holder.Snapshot().Bridge;
            waitSeconds = hadError
                ? Math.Max(1, currentBridge.ErrorPollIntervalSeconds)
                : hadJobs
                    ? Math.Max(1, currentBridge.BusyPollIntervalSeconds)
                    : Math.Max(1, currentBridge.IdlePollIntervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Wasla Print Bridge background loop stopped.");
    }

    /// <summary>
    /// The one place a job becomes active. Under the same lock as <see cref="TryBeginConnectionChange"/>, so a job and a
    /// connection change can never both proceed: the job starts only when no change is in progress and the saved
    /// connection is still the one that offered it.
    /// </summary>
    private bool TryBeginJob(PollingLoop loop, WaslaOptions connection)
    {
        lock (_sync)
        {
            if (_connectionChanges > 0 || loop.StopRequested || !IsSavedConnection(connection))
                return false;

            _jobsInProgress++;
            return true;
        }
    }

    private bool IsSavedConnection(WaslaOptions connection)
    {
        var saved = _holder.Connection;
        return string.Equals(saved.ServerUrl, connection.ServerUrl, StringComparison.Ordinal)
            && string.Equals(saved.AgentToken, connection.AgentToken, StringComparison.Ordinal);
    }

    private void EndJob()
    {
        lock (_sync)
            _jobsInProgress--;
    }

    private void RegisterJobReceived(WaslaPrintBridgeClient.PendingPrintJobDto job)
    {
        var (_, bridge, _) = _holder.Snapshot();
        UpsertRecentJob(new LocalPrintJobRecord
        {
            JobId = job.Id,
            OrderId = job.OrderId,
            OrderDisplay = ReceiptPayloadReader.TryGetOrderDisplay(job.PayloadJson),
            Platform = ReceiptPayloadReader.TryGetPlatform(job.PayloadJson),
            JobType = job.Type,
            PrinterName = bridge.PrinterName,
            Status = LocalPrintJobStatus.Received,
            CreatedAtUtc = job.CreatedAtUtc,
            LastAttemptAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Claims, prints and reports one job. Stop does not cancel it (see <see cref="StopAsync"/>); each step is bounded by
    /// its own limits: the HTTP client timeout, the spooler call and a fixed number of printed-report attempts. A job is
    /// reported failed only when its receipt was not printed. Once printed, it stays printed: mark-printed is repeated
    /// after a server or network failure (the server ignores a repeat), and mark-failed is never sent for it.
    /// Every request uses <paramref name="connection"/>, the connection that offered the job, so no report ever goes to a
    /// server or token the job was not claimed from (WAS-58).
    /// </summary>
    private async Task ProcessJobAsync(
        WaslaPrintBridgeClient.PendingPrintJobDto job,
        WaslaOptions connection,
        CancellationToken stopTracking)
    {
        var ct = CancellationToken.None;
        _logger.LogInformation("Job claim attempted. JobId={JobId}, OrderId={OrderId}", job.Id, job.OrderId);

        WaslaPrintBridgeClient.PrintJobActionResult claim;
        try
        {
            claim = await _client.MarkPrintingAsync(job.Id, ct, connection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, ex.Message);
            _logger.LogWarning(ex, "Job claim request failed. JobId={JobId}", job.Id);
            throw;
        }

        if (claim.Skipped)
        {
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Skipped, "Job is no longer pending.");
            _logger.LogInformation("Job claim skipped because job is no longer Pending. JobId={JobId}", job.Id);
            return;
        }

        if (!claim.Success)
        {
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, $"Claim failed: {claim.Result}");
            _logger.LogWarning("Job claim failed. JobId={JobId}, Result={Result}", job.Id, claim.Result);
            return;
        }

        UpdateRecentJob(job.Id, LocalPrintJobStatus.Printing, statusNote: null);
        _logger.LogInformation("Job claim succeeded. JobId={JobId}", job.Id);
        _journal.Record(job.Id, PrintJournalStage.NotPrinted, connection);

        // Only what happened to the receipt's data (or, in test mode, writing the receipt) decides its outcome. Updating
        // the status afterwards is outside, so nothing after the receipt was sent can report it failed (WAS-56, WAS-59).
        // WAS-216: once any copy may have reached the printer, it is never reported failed or sent again.
        PrintJournalSubmission? submission = null;
        string? failure = null;
        var dryRunWritten = false;
        try
        {
            var (_, bridge, _) = _holder.Snapshot();
            var receipt = _formatter.Format(job.PayloadJson);

            if (bridge.DryRun)
            {
                // From here until the file is written, a crash leaves the outcome unknown: such a job is never printed again.
                _journal.Record(job.Id, PrintJournalStage.Submitting, connection);
                // TEMPORARY: remove after the manual receipt-content verification.
                var receiptDirectory = Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "test-receipts");
                Directory.CreateDirectory(receiptDirectory);
                var receiptPath = Path.Combine(receiptDirectory, $"receipt-{job.Id:N}.txt");
                await File.WriteAllTextAsync(receiptPath, receipt, System.Text.Encoding.UTF8, ct)
                    .ConfigureAwait(false);
                _logger.LogInformation("DryRun receipt saved. Path={ReceiptPath}", receiptPath);

                _logger.LogInformation("DryRun: no physical print was sent. JobId={JobId}", job.Id);
                dryRunWritten = true;
            }
            else
            {
                var printerName = bridge.PrinterName.Trim();
                // Availability can change between the poll and this claim: checked again before anything is handed to Windows.
                var availability = await RefreshAvailabilityAsync(printerName).ConfigureAwait(false);
                if (!availability.IsReady)
                {
                    failure = $"Printer not available: {availability.State} (status 0x{availability.Status:X8}, attributes 0x{availability.Attributes:X8}).";
                }
                else
                {
                    _logger.LogInformation(
                        "Print send attempted. JobId={JobId}, PrinterName={PrinterName}, CopyCount={CopyCount}",
                        job.Id,
                        printerName,
                        job.CopyCount);
                    (submission, failure) = await SubmitCopiesAsync(
                        job.Id,
                        connection,
                        printerName,
                        System.Text.Encoding.UTF8.GetBytes(receipt),
                        job.CopyCount).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            // A copy recorded in the journal may have been handed to Windows: never a "not printed" guess then.
            submission = _journal.Entries.FirstOrDefault(e => e.JobId == job.Id)?.Submission;
            _logger.LogWarning(
                ex,
                "Print failed. JobId={JobId}, HandedToWindows={HandedToWindows}",
                job.Id,
                submission is not null);
        }

        if (dryRunWritten && failure is null)
        {
            // Reported outside the try above: a report that keeps failing never turns the written receipt into a failure.
            await ReportSentAsync(job.Id, connection, live: true, note: "Dry run").ConfigureAwait(false);
            return;
        }

        if (submission is null)
        {
            await ReportNotPrintedAsync(job.Id, connection, failure ?? "Not printed.", live: true).ConfigureAwait(false);
            return;
        }

        submission = await TrackSubmissionAsync(job.Id, connection, submission, stopTracking).ConfigureAwait(false);
        await FinishSubmissionAsync(job.Id, connection, submission, failure, live: true).ConfigureAwait(false);
    }

    /// <summary>
    /// WAS-216: hands one RAW job per copy to Windows, in order, each recorded in the journal before it is created (its
    /// unique document name) and again as soon as Windows returns its id, before any byte is written. A copy that fails
    /// stops the rest. Returns the submission and the first failure's text. Never throws: whatever happened is in the
    /// submission, so a copy that may have reached the printer is never mistaken for one that did not.
    /// </summary>
    private async Task<(PrintJournalSubmission Submission, string? Failure)> SubmitCopiesAsync(
        Guid jobId,
        WaslaOptions connection,
        string printerName,
        byte[] data,
        int copyCount)
    {
        var copies = Math.Clamp(copyCount, 1, 3);
        var submissionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var submission = new PrintJournalSubmission(printerName, submissionId, copies, []);
        string? failure = null;

        for (var number = 1; number <= copies; number++)
        {
            var copy = new PrintJournalCopy(number, PrintJournalSubmission.DocumentName(submissionId, number, copies), DateTime.UtcNow);
            submission = submission.With(copy);
            // Intent first: a crash from here on is an interrupted copy, found again by its document name.
            _journal.Record(jobId, PrintJournalStage.Submitting, connection, submission);

            var started = submission;
            SpoolSubmitResult result;
            try
            {
                result = await Task.Run(() => _spooler.Submit(printerName, copy.DocumentName, data, windowsJobId =>
                {
                    started = started.With(copy with { WindowsJobId = windowsJobId });
                    _journal.Record(jobId, PrintJournalStage.Submitting, connection, started);
                })).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The spooler boundary does not throw once a job exists; if it did anyway, a job with a known id may hold data.
                var knownId = started.Copies.First(c => c.Number == number).WindowsJobId;
                _logger.LogWarning(ex, "Handing a copy to Windows threw. JobId={JobId}, Copy={Copy}", jobId, number);
                result = knownId is { } id
                    ? SpoolSubmitResult.Incomplete(id, bytesWritten: -1, aborted: false, ex.Message)
                    : SpoolSubmitResult.NotCreated(ex.Message);
            }

            copy = PrintSubmissionTracker.AfterSubmit(started.Copies.First(c => c.Number == number), result);
            submission = started.With(copy);
            _journal.Record(jobId, PrintJournalStage.Submitting, connection, submission);

            if (result.Outcome == SpoolSubmitOutcome.Spooled)
            {
                _logger.LogInformation(
                    "Copy handed to Windows. JobId={JobId}, Copy={Copy}/{Copies}, WindowsJobId={WindowsJobId}, Bytes={Bytes}",
                    jobId,
                    number,
                    copies,
                    result.JobId,
                    result.BytesWritten);
                continue;
            }

            failure = result.Error;
            _logger.LogWarning(
                "Copy not handed to Windows completely. JobId={JobId}, Copy={Copy}/{Copies}, Outcome={Outcome}, WindowsJobId={WindowsJobId}, BytesWritten={BytesWritten}, Aborted={Aborted}, Win32Error={Win32Error}, Error={Error}, CopyOutcome={CopyOutcome}",
                jobId,
                number,
                copies,
                result.Outcome,
                result.JobId,
                result.BytesWritten,
                result.Aborted,
                result.Win32Error,
                result.Error,
                copy.Outcome);
            break;
        }

        return (submission, failure);
    }

    /// <summary>
    /// WAS-216: watches the receipt's Windows jobs for at most <see cref="SubmissionTrackingWindow"/> (less when the loop
    /// is stopped). Whatever is still undecided then stays in the journal, which later cycles keep watching.
    /// </summary>
    private async Task<PrintJournalSubmission> TrackSubmissionAsync(
        Guid jobId,
        WaslaOptions connection,
        PrintJournalSubmission submission,
        CancellationToken stopTracking)
    {
        var until = DateTime.UtcNow + SubmissionTrackingWindow;
        while (true)
        {
            submission = await ObserveSubmissionAsync(jobId, connection, submission).ConfigureAwait(false);
            if (PrintSubmissionTracker.Decide(submission) != PrintSubmissionVerdict.Waiting
                || DateTime.UtcNow >= until
                || stopTracking.IsCancellationRequested)
            {
                return submission;
            }

            try
            {
                await Task.Delay(SubmissionTrackingInterval, stopTracking).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return submission;
            }
        }
    }

    /// <summary>One look at every copy that is still in the queue, and at the printer; the journal follows any change.</summary>
    private async Task<PrintJournalSubmission> ObserveSubmissionAsync(Guid jobId, WaslaOptions connection, PrintJournalSubmission submission)
    {
        var waiting = submission.Copies.Where(PrintSubmissionTracker.NeedsObservation).ToList();
        if (waiting.Count == 0)
            return submission;

        List<(PrintJournalCopy Copy, SpoolJobObservation Observation)> observations;
        PrinterAvailability printer;
        try
        {
            (observations, printer) = await Task.Run(() =>
            {
                var seen = waiting
                    .Select(c => (c, _spooler.Observe(submission.PrinterName, c.WindowsJobId!.Value, c.DocumentName)))
                    .ToList();
                return (seen, ReadAvailability(submission.PrinterName));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is learned; the copies keep waiting.
            _logger.LogWarning(ex, "The Windows print queue could not be read. JobId={JobId}", jobId);
            return submission;
        }

        var changed = false;
        foreach (var (copy, observation) in observations)
        {
            var next = PrintSubmissionTracker.Observe(copy, observation, printer);
            if (next == copy)
                continue;

            changed = true;
            submission = submission.With(next);
            if (next.Outcome is not null)
            {
                _logger.LogInformation(
                    "Windows job left the queue or ended. JobId={JobId}, Copy={Copy}, WindowsJobId={WindowsJobId}, Observation={Observation}, Status=0x{Status:X8}, Outcome={Outcome}, SeenPrinting={SeenPrinting}, SeenProblem={SeenProblem}, SeenDeleting={SeenDeleting}",
                    jobId,
                    next.Number,
                    next.WindowsJobId,
                    observation.State,
                    observation.Status,
                    next.Outcome,
                    next.SeenPrinting,
                    next.SeenProblem,
                    next.SeenDeleting);
            }
        }

        if (changed)
            _journal.Record(jobId, PrintJournalStage.Submitting, connection, submission);
        return submission;
    }

    /// <summary>
    /// WAS-216: a receipt handed to Windows by an earlier cycle or run. A copy that run ended in the middle of may exist in
    /// the queue (looked for by its document name when its id was never recorded) and may have printed in part.
    /// </summary>
    private async Task SettleSubmissionAsync(Guid jobId, WaslaOptions connection, PrintJournalSubmission submission)
    {
        var interrupted = submission.Copies.Where(PrintSubmissionTracker.WasInterrupted).ToList();
        foreach (var copy in interrupted)
        {
            SpoolJobLookup? lookup = null;
            if (copy.WindowsJobId is null)
            {
                try
                {
                    lookup = await Task.Run(() => _spooler.Find(submission.PrinterName, copy.DocumentName)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The Windows print queue could not be searched. JobId={JobId}", jobId);
                }
            }

            var recovered = PrintSubmissionTracker.Recover(copy, lookup);
            _logger.LogWarning(
                "Print Bridge stopped while a copy was being handed to Windows; whether it printed is unknown. It is not printed again; it waits for the user's check. JobId={JobId}, Copy={Copy}, WindowsJobId={WindowsJobId}, Lookup={Lookup}",
                jobId,
                copy.Number,
                recovered.WindowsJobId,
                lookup?.State);
            submission = submission.With(recovered);
        }

        if (interrupted.Count > 0)
            _journal.Record(jobId, PrintJournalStage.Submitting, connection, submission);

        submission = await ObserveSubmissionAsync(jobId, connection, submission).ConfigureAwait(false);
        await FinishSubmissionAsync(jobId, connection, submission, failure: null, live: false).ConfigureAwait(false);
    }

    /// <summary>
    /// WAS-216: acts on what Windows' evidence says about the whole receipt. Sent → <c>mark-printed</c> (not proof of
    /// paper); nothing submitted → <c>mark-failed</c>; unknown → waits for the user's check, never reported or printed
    /// again on its own; still queued → stays in the journal and is shown as waiting in the Windows queue.
    /// </summary>
    private async Task FinishSubmissionAsync(
        Guid jobId,
        WaslaOptions connection,
        PrintJournalSubmission submission,
        string? failure,
        bool live)
    {
        switch (PrintSubmissionTracker.Decide(submission))
        {
            case PrintSubmissionVerdict.Sent:
                await ReportSentAsync(jobId, connection, live, SentToPrinterNote).ConfigureAwait(false);
                break;

            case PrintSubmissionVerdict.NotPrinted:
                await ReportNotPrintedAsync(jobId, connection, failure ?? "Not printed.", live).ConfigureAwait(false);
                break;

            case PrintSubmissionVerdict.Uncertain:
                _journal.Record(jobId, PrintJournalStage.Uncertain, connection, submission);
                _logger.LogWarning(
                    "Whether the receipt printed is unknown; it is not printed again and waits for the user's check. JobId={JobId}, Copies={Copies}",
                    jobId,
                    string.Join(", ", submission.Copies.OrderBy(c => c.Number).Select(c => $"{c.Number}:{c.Outcome?.ToString() ?? "Waiting"}")));
                MarkUncertainLocally(jobId, failure);
                break;

            default:
                // Still in the Windows queue: it may print later. The server keeps the job in Printing meanwhile.
                ShowInWindowsQueue(jobId);
                break;
        }
    }

    /// <summary>Windows finished sending the receipt (or, in test mode, wrote it): <c>mark-printed</c>, repeated after a server or network failure.</summary>
    private async Task ReportSentAsync(Guid jobId, WaslaOptions connection, bool live, string note)
    {
        _journal.Record(jobId, PrintJournalStage.Printed, connection);
        UpdateRecentJob(jobId, LocalPrintJobStatus.Printed, statusNote: note);

        // If every attempt fails, the exception reaches the loop and the journal keeps the job for a later report.
        var printed = live
            ? await ReportPrintedAsync(jobId, connection).ConfigureAwait(false)
            : await _client.MarkPrintedAsync(jobId, CancellationToken.None, connection).ConfigureAwait(false);
        _journal.Remove(jobId);
        if (!printed.Success && !printed.Skipped)
            UpdateRecentJob(jobId, LocalPrintJobStatus.Failed, $"mark-printed: {printed.Result}");

        _logger.LogInformation(
            "mark-printed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
            jobId,
            printed.Success,
            printed.Skipped,
            printed.Result);
    }

    /// <summary>No data of the receipt is in any Windows job: a real failure, reported once so Wasla can print it again.</summary>
    private async Task ReportNotPrintedAsync(Guid jobId, WaslaOptions connection, string failure, bool live)
    {
        _logger.LogWarning("Receipt not printed. JobId={JobId}, Reason={Reason}", jobId, failure);
        _journal.Record(jobId, PrintJournalStage.NotPrinted, connection);
        UpdateRecentJob(jobId, LocalPrintJobStatus.Failed, failure);

        try
        {
            var failed = await _client.MarkFailedAsync(jobId, failure, CancellationToken.None, connection).ConfigureAwait(false);
            _logger.LogInformation(
                "mark-failed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
                jobId,
                failed.Success,
                failed.Skipped,
                failed.Result);
            _journal.Remove(jobId);
        }
        catch (Exception markEx) when (live)
        {
            _logger.LogWarning(markEx, "Failed to mark job as failed. JobId={JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Sends mark-printed, repeating it after a server or network failure or a redirect (<see cref="PrintedReportRetryDelays"/>). If
    /// every attempt fails, the exception reaches the polling loop, which records the connection problem; the job stays
    /// printed and is never reported failed.
    /// </summary>
    private async Task<WaslaPrintBridgeClient.PrintJobActionResult> ReportPrintedAsync(Guid jobId, WaslaOptions connection)
    {
        var delays = PrintedReportRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.MarkPrintedAsync(jobId, CancellationToken.None, connection).ConfigureAwait(false);
            }
            catch (PrintBridgeConnectionException ex) when (attempt < delays.Count && IsTransient(ex))
            {
                _logger.LogWarning(
                    ex,
                    "mark-printed failed; the printed job is reported again. JobId={JobId}, Attempt={Attempt}",
                    jobId,
                    attempt + 1);
                await Task.Delay(delays[attempt]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The server could not be reached, timed out, failed (5xx) or answered with a redirect, which is never followed
    /// (WAS-191): the report may or may not have been stored, and repeating it to the same server is safe. Rejected
    /// credentials are not transient.
    /// </summary>
    private static bool IsTransient(PrintBridgeConnectionException ex) =>
        ex.IssueCode == PrintBridgeRuntimeIssueCode.ServerUnreachable || ex.StatusCode is >= 500 or (>= 300 and < 400);

    private void UpsertRecentJob(LocalPrintJobRecord record)
    {
        lock (_sync)
        {
            _recentJobs.RemoveAll(j => j.JobId == record.JobId);
            _recentJobs.Insert(0, record);
            if (_recentJobs.Count > MaxRecentJobs)
                _recentJobs.RemoveRange(MaxRecentJobs, _recentJobs.Count - MaxRecentJobs);
        }

        RaiseStatusChanged();
    }

    private void UpdateRecentJob(
        Guid jobId,
        LocalPrintJobStatus status,
        string? errorMessage = null,
        string? statusNote = null)
    {
        lock (_sync)
        {
            // A job of an earlier run (WAS-216: still in the Windows queue at a restart) keeps its order details.
            var existing = FindOrSeedRecentJob(jobId);
            existing.Status = status;
            existing.LastAttemptAtUtc = DateTime.UtcNow;
            existing.ErrorMessage = errorMessage;
            if (statusNote is not null)
                existing.StatusNote = statusNote;
            else if (status != LocalPrintJobStatus.Printing && existing.StatusNote == InWindowsQueueNote)
                existing.StatusNote = null;
            if (status == LocalPrintJobStatus.Printed)
                existing.PrintedAtUtc = DateTime.UtcNow;

            PersistToHistory(existing);
        }

        RaiseStatusChanged();
    }

    private void PersistToHistory(LocalPrintJobRecord record)
    {
        // A job waiting in the Windows queue (WAS-216) is kept, so History shows it across a restart.
        if (record.Status is LocalPrintJobStatus.Received
            || (record.Status == LocalPrintJobStatus.Printing && record.StatusNote != InWindowsQueueNote))
        {
            return;
        }

        _historyStore.Record(CloneRecord(record));
    }

    private static LocalPrintJobRecord CloneRecord(LocalPrintJobRecord source) =>
        new()
        {
            JobId = source.JobId,
            OrderId = source.OrderId,
            OrderDisplay = source.OrderDisplay,
            Platform = source.Platform,
            JobType = source.JobType,
            PrinterName = source.PrinterName,
            Status = source.Status,
            CreatedAtUtc = source.CreatedAtUtc,
            LastAttemptAtUtc = source.LastAttemptAtUtc,
            PrintedAtUtc = source.PrintedAtUtc,
            ErrorMessage = source.ErrorMessage,
            StatusNote = source.StatusNote
        };

    private int GetJobsTodayCount(DateTime today, IReadOnlyList<LocalPrintJobRecord> recentJobs)
    {
        var count = _historyStore.CountForLocalDate(today);
        foreach (var job in recentJobs)
        {
            if (ToLocalDate(job.DisplayTimeUtc) != today)
                continue;

            if (_historyStore.FindByJobId(job.JobId) is not null)
                continue;

            count++;
        }

        return count;
    }

    private int GetFailedTodayCount(DateTime today, IReadOnlyList<LocalPrintJobRecord> recentJobs)
    {
        var count = _historyStore.CountForLocalDate(today, LocalPrintJobStatus.Failed);
        foreach (var job in recentJobs)
        {
            if (job.Status == LocalPrintJobStatus.Failed && ToLocalDate(job.DisplayTimeUtc) == today)
            {
                if (_historyStore.FindByJobId(job.JobId) is null)
                    count++;
            }
        }

        return count;
    }

    private static DateTime ToLocalDate(DateTime utc) => utc.ToLocalTime().Date;

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void RecordVerifiedContact(WaslaPrintBridgeClient.PrintBridgeHealthResult health)
    {
        _deviceMetadataSync.ApplyFromHealth(health);
        lock (_sync)
        {
            _lastSuccessfulContactUtc = DateTime.UtcNow;
            _lastIssue = null;
        }

        RaiseStatusChanged();
    }

    /// <summary>Starts listening when printing is ready; a missing or unavailable printer only leaves it stopped.</summary>
    private void TryStartListening()
    {
        try
        {
            Start();
        }
        catch (LocalizedApplicationException ex)
        {
            _logger.LogInformation("Print Bridge listening was not started. Reason={Reason}", ex.ResourceKey);
        }
    }

    private PrintBridgeRuntimeIssue ApplyFailure(Exception ex, bool applyCredentialFailureFallback)
    {
        var issue = ToRuntimeIssue(ex);

        if (issue.Code is PrintBridgeRuntimeIssueCode.ReconnectRequired
            or PrintBridgeRuntimeIssueCode.DisabledByAdmin
            or PrintBridgeRuntimeIssueCode.DuplicateInstallation)
        {
            _deviceMetadataSync.MarkUnresolved();
        }

        if (applyCredentialFailureFallback && issue.ShouldClearToken)
            ClearTokenForReconnectRequired();

        lock (_sync)
            _lastIssue = issue;

        return issue;
    }

    private void ClearTokenForReconnectRequired()
    {
        try
        {
            var (current, bridge, ui) = _holder.Snapshot();
            PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(current, bridge);
            _holder.Replace(current, bridge, ui);
            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                Wasla = current,
                PrintBridge = bridge,
                Ui = ui
            });
            _logger.LogInformation("Print Bridge local token cleared after reconnect-required authentication failure.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to persist Print Bridge token clearing after authentication failure.");
        }
    }

    /// <summary>Ends listening after a terminal issue. As with Stop, a job already claimed is not cancelled.</summary>
    private void SuspendPollingAfterTerminalIssue()
    {
        PollingLoop? loop;
        lock (_sync)
        {
            _isRunning = false;
            loop = _loop;
            _loop = null;
            if (loop is not null)
                loop.StopRequested = true;
        }

        try
        {
            loop?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends a connection change once; disposing it again does nothing.</summary>
    private sealed class ConnectionChangeLease(PrintBridgeRuntime runtime) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                runtime.EndConnectionChange();
        }
    }

    /// <summary>One run of the polling loop.</summary>
    private sealed class PollingLoop
    {
        /// <summary>Ends the loop's waits and polls; never passed to a claimed job.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Guarded by the runtime's lock. Once set, the loop claims no new job.</summary>
        public bool StopRequested { get; set; }
    }

    private static PrintBridgeRuntimeIssue ToRuntimeIssue(Exception ex) =>
        ex switch
        {
            PrintBridgeConnectionException connectionEx => new(
                connectionEx.IssueCode,
                connectionEx.UserMessageKey,
                connectionEx.FormatArgs),
            LocalizedApplicationException localized => PrintBridgeRuntimeIssue.FromResource(
                localized.ResourceKey,
                localized.Args),
            _ => PrintBridgeRuntimeIssue.FromRaw(ex.Message)
        };
}
