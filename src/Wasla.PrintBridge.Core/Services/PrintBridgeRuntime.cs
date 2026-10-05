using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeRuntime : IDisposable, IPrintBridgeEngine, IPrintBridgeConnectionGuard
{
    private const int MaxRecentJobs = 50;

    private readonly WaslaPrintBridgeClient _client;
    private readonly ReceiptFormatter _formatter;
    private readonly IReceiptPrinter _printer;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeDeviceMetadataSync _deviceMetadataSync;
    private readonly LocalPrintJobHistoryStore _historyStore;
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

    public PrintBridgeRuntime(
        WaslaPrintBridgeClient client,
        ReceiptFormatter formatter,
        IReceiptPrinter printer,
        PrintBridgeSettingsHolder holder,
        PrintBridgeSettingsStore store,
        PrintBridgeDeviceMetadataSync deviceMetadataSync,
        LocalPrintJobHistoryStore historyStore,
        AppVersionInfo appVersion,
        ILogger<PrintBridgeRuntime> logger)
    {
        _client = client;
        _formatter = formatter;
        _printer = printer;
        _holder = holder;
        _store = store;
        _deviceMetadataSync = deviceMetadataSync;
        _historyStore = historyStore;
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

    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
    }

    public PrintBridgeRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            var (hub, bridge, _) = _holder.Snapshot();
            var connectionOptions = new WaslaOptions
            {
                ServerUrl = hub.ServerUrl,
                AgentToken = hub.AgentToken
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
                ServerUrl = hub.ServerUrl,
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
                PrinterHealthStatus = WindowsPrinterHealth.Resolve(bridge),
                TrayIconState = trayIconState
            };
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_isRunning)
                return;

            var (hub, bridge, _) = _holder.Snapshot();
            if (!PrintBridgeSettingsValidator.TryValidatePrintingReadiness(hub, bridge, out var errorKey))
                throw new LocalizedApplicationException(errorKey!);

            if (!PrintBridgeSettingsValidator.TryValidatePrinterAvailability(bridge, out errorKey))
                throw new LocalizedApplicationException(errorKey!);

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

        var (hub, bridge, ui) = _holder.Snapshot();
        var connection = new WaslaOptions
        {
            ServerUrl = verified.ServerUrl,
            AgentToken = verified.AgentToken
        };
        var updatedBridge = bridge.Clone();
        var tokenChanged = !string.Equals(hub.AgentToken?.Trim(), connection.AgentToken, StringComparison.Ordinal);
        if (tokenChanged)
        {
            updatedBridge.DisplayName = string.Empty;
            updatedBridge.ServerDeviceNameResolved = false;
        }

        try
        {
            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = connection,
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

        if (!PrintBridgeSettingsValidator.TryValidatePrinterAvailability(bridge, out var errorKey))
            throw new LocalizedApplicationException(errorKey!);

        var printedAt = DateTime.Now.ToString("G", Wasla.Application.Printing.GregorianCulture.For(System.Globalization.CultureInfo.CurrentCulture));
        var receipt = $"Wasla Print Bridge{Environment.NewLine}Test print{Environment.NewLine}{printedAt}";
        await _printer.PrintAsync(bridge.PrinterName, receipt, 1, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

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
        _logger.LogInformation("Wasla Print Bridge background loop started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var (hub, bridge, _) = _holder.Snapshot();
            // One connection for the whole cycle: its poll, and every request of the jobs it claims (WAS-58).
            var connection = new WaslaOptions { ServerUrl = hub.ServerUrl, AgentToken = hub.AgentToken };
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

                var jobs = await _client.GetPendingJobsAsync(stoppingToken, connection).ConfigureAwait(false);
                _logger.LogInformation("Polling result: pending job count={Count}", jobs.Count);

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
                            await ProcessJobAsync(job, connection).ConfigureAwait(false);
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
        var saved = _holder.OrderHub;
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
    private async Task ProcessJobAsync(WaslaPrintBridgeClient.PendingPrintJobDto job, WaslaOptions connection)
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

        try
        {
            var (_, bridge, _) = _holder.Snapshot();
            var receipt = _formatter.Format(job.PayloadJson);

            if (bridge.DryRun)
            {
                // TEMPORARY: remove after the manual receipt-content verification.
                var receiptDirectory = Path.Combine(PrintBridgePaths.ProgramDataLogDirectory, "test-receipts");
                Directory.CreateDirectory(receiptDirectory);
                var receiptPath = Path.Combine(receiptDirectory, $"receipt-{job.Id:N}.txt");
                await File.WriteAllTextAsync(receiptPath, receipt, System.Text.Encoding.UTF8, ct)
                    .ConfigureAwait(false);
                _logger.LogInformation("DryRun receipt saved. Path={ReceiptPath}", receiptPath);

                _logger.LogInformation("DryRun: no physical print was sent. JobId={JobId}", job.Id);
                UpdateRecentJob(job.Id, LocalPrintJobStatus.Printed, statusNote: "Dry run");
            }
            else
            {
                _logger.LogInformation(
                    "Print send attempted. JobId={JobId}, PrinterName={PrinterName}, CopyCount={CopyCount}",
                    job.Id,
                    bridge.PrinterName,
                    job.CopyCount);
                await _printer.PrintAsync(bridge.PrinterName, receipt, job.CopyCount, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Print job submitted to Windows queue. JobId={JobId}, PrinterName={PrinterName}, CharacterCount={CharacterCount}, CopyCount={CopyCount}",
                    job.Id,
                    bridge.PrinterName,
                    receipt.Length,
                    job.CopyCount);
                UpdateRecentJob(job.Id, LocalPrintJobStatus.Printed, statusNote: "Windows accepted the print job");
            }
        }
        catch (Exception ex)
        {
            // The receipt was not printed: this is a real failure, reported once.
            _logger.LogWarning(ex, "Print failed. JobId={JobId}", job.Id);
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, ex.Message);

            try
            {
                var failed = await _client.MarkFailedAsync(job.Id, ex.Message, ct, connection).ConfigureAwait(false);
                _logger.LogInformation(
                    "mark-failed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
                    job.Id,
                    failed.Success,
                    failed.Skipped,
                    failed.Result);
            }
            catch (Exception markEx)
            {
                _logger.LogWarning(markEx, "Failed to mark job as failed. JobId={JobId}", job.Id);
                throw;
            }

            return;
        }

        var printed = await ReportPrintedAsync(job.Id, connection).ConfigureAwait(false);
        if (!printed.Success && !printed.Skipped)
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, $"mark-printed: {printed.Result}");

        _logger.LogInformation(
            "mark-printed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
            job.Id,
            printed.Success,
            printed.Skipped,
            printed.Result);
    }

    /// <summary>
    /// Sends mark-printed, repeating it after a server or network failure (<see cref="PrintedReportRetryDelays"/>). If
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

    /// <summary>The server could not be reached, timed out or failed (5xx); rejected credentials are not transient.</summary>
    private static bool IsTransient(PrintBridgeConnectionException ex) =>
        ex.IssueCode == PrintBridgeRuntimeIssueCode.ServerUnreachable || ex.StatusCode >= 500;

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
            var index = _recentJobs.FindIndex(j => j.JobId == jobId);
            if (index < 0)
            {
                _recentJobs.Insert(0, new LocalPrintJobRecord
                {
                    JobId = jobId,
                    Status = status,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastAttemptAtUtc = DateTime.UtcNow,
                    ErrorMessage = errorMessage,
                    StatusNote = statusNote
                });
            }
            else
            {
                var existing = _recentJobs[index];
                existing.Status = status;
                existing.LastAttemptAtUtc = DateTime.UtcNow;
                existing.ErrorMessage = errorMessage;
                if (statusNote is not null)
                    existing.StatusNote = statusNote;
                if (status == LocalPrintJobStatus.Printed)
                    existing.PrintedAtUtc = DateTime.UtcNow;
            }

            if (_recentJobs.Count > MaxRecentJobs)
                _recentJobs.RemoveRange(MaxRecentJobs, _recentJobs.Count - MaxRecentJobs);

            var persisted = index >= 0 ? _recentJobs[index] : _recentJobs[0];
            PersistToHistory(persisted);
        }

        RaiseStatusChanged();
    }

    private void PersistToHistory(LocalPrintJobRecord record)
    {
        if (record.Status is LocalPrintJobStatus.Received or LocalPrintJobStatus.Printing)
            return;

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
            var (hub, bridge, ui) = _holder.Snapshot();
            PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(hub, bridge);
            _holder.Replace(hub, bridge, ui);
            _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = hub,
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
