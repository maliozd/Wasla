using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeRuntime : IDisposable
{
    private const int MaxRecentJobs = 50;

    private readonly WaslaPrintBridgeClient _client;
    private readonly ReceiptFormatter _formatter;
    private readonly IReceiptPrinter _printer;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly PrintBridgeDeviceMetadataSync _deviceMetadataSync;
    private readonly LocalPrintJobHistoryStore _historyStore;
    private readonly AppVersionInfo _appVersion;
    private readonly ILogger<PrintBridgeRuntime> _logger;
    private readonly object _sync = new();
    private readonly List<LocalPrintJobRecord> _recentJobs = [];

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isRunning;
    private DateTime? _lastSuccessfulContactUtc;
    private DateTime? _lastPollUtc;
    private string? _lastError;

    public PrintBridgeRuntime(
        WaslaPrintBridgeClient client,
        ReceiptFormatter formatter,
        IReceiptPrinter printer,
        PrintBridgeSettingsHolder holder,
        PrintBridgeDeviceMetadataSync deviceMetadataSync,
        LocalPrintJobHistoryStore historyStore,
        AppVersionInfo appVersion,
        ILogger<PrintBridgeRuntime> logger)
    {
        _client = client;
        _formatter = formatter;
        _printer = printer;
        _holder = holder;
        _deviceMetadataSync = deviceMetadataSync;
        _historyStore = historyStore;
        _appVersion = appVersion;
        _logger = logger;
    }

    public event EventHandler? StatusChanged;

    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
    }

    public PrintBridgeRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            var (hub, bridge, _) = _holder.Snapshot();
            var recentJobs = _recentJobs.Select(CloneRecord).ToList();
            var isConnected = _lastSuccessfulContactUtc.HasValue
                && DateTime.UtcNow - _lastSuccessfulContactUtc.Value <= TimeSpan.FromSeconds(60);
            var trayIconState = PrintBridgeRuntimeStatus.ResolveTrayIconState(_isRunning, isConnected, recentJobs);
            var today = DateTime.Today;

            return new PrintBridgeRuntimeStatus
            {
                IsRunning = _isRunning,
                IsConnected = isConnected,
                LastSuccessfulContactUtc = _lastSuccessfulContactUtc,
                LastPollUtc = _lastPollUtc,
                LastError = _lastError,
                BaseUrl = hub.BaseUrl,
                PrinterName = bridge.PrinterName,
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
                    _isRunning,
                    isConnected,
                    _lastError),
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
            if (!PrintBridgeSettingsValidator.TryValidate(hub, bridge, out var errorKey))
                throw new LocalizedApplicationException(errorKey!);

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        _logger.LogInformation("Print Bridge polling started.");
        RaiseStatusChanged();
    }

    public async Task StopAsync()
    {
        Task? loopTask;
        CancellationTokenSource? cts;

        lock (_sync)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
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
        _deviceMetadataSync.ApplyFromHealth(health);
        lock (_sync)
        {
            _lastSuccessfulContactUtc = DateTime.UtcNow;
            _lastError = null;
        }

        RaiseStatusChanged();
        return health;
    }

    public async Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            return await ValidateConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (PrintBridgeConnectionException ex) when (ex.IsTokenAuthFailure)
        {
            _deviceMetadataSync.MarkUnresolved();
            lock (_sync)
                _lastError = GetUserErrorMessage(ex);
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

        if (string.IsNullOrWhiteSpace(bridge.PrinterName))
            throw new LocalizedApplicationException("Error.PrinterRequired");

        var receipt = $"Wasla Print Bridge{Environment.NewLine}Test print{Environment.NewLine}{DateTime.Now:G}";
        await _printer.PrintAsync(bridge.PrinterName, receipt, 1, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderHub Print Bridge background loop started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var (_, bridge, _) = _holder.Snapshot();
            var waitSeconds = Math.Max(1, bridge.IdlePollIntervalSeconds);
            var hadJobs = false;
            var hadError = false;

            lock (_sync)
                _lastPollUtc = DateTime.UtcNow;

            RaiseStatusChanged();

            try
            {
                var health = await _client.TestHealthAsync(stoppingToken).ConfigureAwait(false);
                _deviceMetadataSync.ApplyFromHealth(health);

                lock (_sync)
                {
                    _lastSuccessfulContactUtc = DateTime.UtcNow;
                    _lastError = null;
                }

                var jobs = await _client.GetPendingJobsAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Polling result: pending job count={Count}", jobs.Count);

                if (jobs.Count > 0)
                {
                    hadJobs = true;
                    foreach (var job in jobs)
                    {
                        RegisterJobReceived(job);
                        await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                hadError = true;
                if (ex is PrintBridgeConnectionException connectionEx && connectionEx.IsTokenAuthFailure)
                    _deviceMetadataSync.MarkUnresolved();

                lock (_sync)
                    _lastError = GetUserErrorMessage(ex);

                _logger.LogWarning(ex, "Print Bridge polling/processing failed.");
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

        _logger.LogInformation("OrderHub Print Bridge background loop stopped.");
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

    private async Task ProcessJobAsync(WaslaPrintBridgeClient.PendingPrintJobDto job, CancellationToken ct)
    {
        _logger.LogInformation("Job claim attempted. JobId={JobId}, OrderId={OrderId}", job.Id, job.OrderId);

        WaslaPrintBridgeClient.PrintJobActionResult claim;
        try
        {
            claim = await _client.MarkPrintingAsync(job.Id, ct).ConfigureAwait(false);
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

            var printed = await _client.MarkPrintedAsync(job.Id, ct).ConfigureAwait(false);
            if (!printed.Success && !printed.Skipped)
                UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, $"mark-printed: {printed.Result}");

            _logger.LogInformation(
                "mark-printed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
                job.Id,
                printed.Success,
                printed.Skipped,
                printed.Result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print failed. JobId={JobId}", job.Id);
            UpdateRecentJob(job.Id, LocalPrintJobStatus.Failed, ex.Message);

            try
            {
                var failed = await _client.MarkFailedAsync(job.Id, ex.Message, ct).ConfigureAwait(false);
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
        }
    }

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

    private static string GetUserErrorMessage(Exception ex) =>
        ex is PrintBridgeConnectionException connectionEx
            ? connectionEx.UserMessageKey
            : ex is LocalizedApplicationException localized
                ? localized.ResourceKey
                : ex.Message;
}
