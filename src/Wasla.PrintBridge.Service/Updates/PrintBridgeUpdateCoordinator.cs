using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Updates;

/// <summary>
/// The service's side of an update (WAS-63, docs/integrations/print-bridge-updates.md): asks the Wasla server for its release,
/// downloads and verifies the setup, pauses printing at a safe point, keeps the attempt in
/// <c>updates\update-state.json</c>, and after a restart reconciles it: an interrupted attempt is dropped, the previous version
/// running again means the update was not applied, and the new version keeps printing paused until Windows Installer has
/// committed it. One attempt at a time for the whole computer.
/// </summary>
internal sealed class PrintBridgeUpdateCoordinator : IAsyncDisposable
{
    /// <summary>How long the pause waits for a print job in progress to finish; it is never cut short.</summary>
    internal static TimeSpan PrintingWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long printing stays paused for a setup nobody runs (the previous version still running).</summary>
    internal static TimeSpan GateLifetime { get; set; } = TimeSpan.FromMinutes(15);

    internal static TimeSpan CommitPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long no installation may run while Windows Installer reports another version before the new version gives up
    /// waiting for its commit, records <see cref="PrintBridgeUpdateFailure.UnexpectedVersion"/> and prints.
    /// </summary>
    internal static TimeSpan CommitIdleLimit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often a staged setup still in use is deleted again, and for how long: the setup that ran from it (Burn's
    /// original process) keeps its file in use until it exits, after the installation has committed or rolled back.
    /// </summary>
    internal static TimeSpan CleanupRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan CleanupLimit { get; set; } = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly PrintBridgeUpdateStateStore _store;
    private readonly Version _installedVersion;
    private readonly PrintBridgePublisher? _publisher;
    private readonly IPrintBridgeInstalledProduct _product;
    private readonly ILogger<PrintBridgeUpdateCoordinator> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private PrintBridgeUpdateRecord? _record;
    private PrintBridgeEngineHost? _engine;
    private WaslaPrintBridgeClient? _client;
    private CancellationTokenSource? _attempt;
    private Task _attemptTask = Task.CompletedTask;
    private Task _commitTask = Task.CompletedTask;
    private CancellationTokenSource? _cleanup;
    private Task _cleanupTask = Task.CompletedTask;
    private IDisposable? _lease;
    private Timer? _expiry;
    private int? _downloadPercent;
    private bool _awaitingCommit;

    public PrintBridgeUpdateCoordinator(
        PrintBridgeUpdateStateStore store,
        Version installedVersion,
        PrintBridgePublisher? publisher,
        IPrintBridgeInstalledProduct product,
        ILogger<PrintBridgeUpdateCoordinator> logger)
    {
        _store = store;
        _installedVersion = installedVersion;
        _publisher = publisher;
        _product = product;
        _logger = logger;
    }

    /// <summary>Raised from any thread when the update state may have changed.</summary>
    public event EventHandler? Changed;

    public Version InstalledVersion => _installedVersion;

    /// <summary>
    /// True from the moment printing is about to be paused until the pause ends or the new version is committed: commands
    /// that change the connection, the settings or printing are refused meanwhile.
    /// </summary>
    public bool PausesChanges
    {
        get
        {
            lock (_sync)
                return _awaitingCommit || _record is { Stage: PrintBridgeUpdateStage.WaitingForPrinting or PrintBridgeUpdateStage.Ready };
        }
    }

    public IpcUpdate Snapshot()
    {
        lock (_sync)
        {
            var record = _record;
            var ready = record is { Stage: PrintBridgeUpdateStage.Ready } && !_awaitingCommit
                        && PrintBridgeReleaseVersion.TryParse(record.ToVersion, out _);
            Version? to = null;
            if (ready)
                PrintBridgeReleaseVersion.TryParse(record!.ToVersion, out to);

            return new IpcUpdate(
                PrintBridgeReleaseVersion.Format(_installedVersion),
                _publisher is not null,
                record?.AttemptId,
                record?.Stage,
                record?.FromVersion,
                record?.ToVersion,
                record is { Stage: PrintBridgeUpdateStage.Downloading } ? _downloadPercent : null,
                record?.Failure,
                ready ? PrintBridgeUpdateStateStore.StagedSetupPath(to!) : null,
                ready ? record!.SetupBytes : null,
                ready ? record!.SetupSha256 : null);
        }
    }

    /// <summary>
    /// Called once by the service after it built the engine and before it activates it. Returns false when this is the new
    /// version of an update that Windows Installer has not committed yet: the engine must not print, settle the journal or
    /// write anything until then, so <paramref name="activateLater"/> runs at the commit instead.
    /// </summary>
    public bool Reconcile(PrintBridgeEngineHost engine, WaslaPrintBridgeClient client, Action activateLater)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(activateLater);
        lock (_sync)
        {
            _engine = engine;
            _client = client;
        }

        var record = _store.Load();
        if (record is null)
        {
            // A staged file without a record (an unreadable record was set aside) belongs to no attempt.
            DeleteStagedFilesSoon();
            return true;
        }

        switch (record.Stage)
        {
            case PrintBridgeUpdateStage.Downloading or PrintBridgeUpdateStage.Verifying or PrintBridgeUpdateStage.WaitingForPrinting:
                _logger.LogWarning("An update to {Version} was interrupted at {Stage}; it is dropped.", record.ToVersion, record.Stage);
                DeleteStagedFilesSoon();
                Record(record.Finish(PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Interrupted));
                return true;

            case PrintBridgeUpdateStage.Ready:
                PrintBridgeReleaseVersion.TryParse(record.ToVersion, out var to);
                PrintBridgeReleaseVersion.TryParse(record.FromVersion, out var from);
                if (to == _installedVersion)
                {
                    lock (_sync)
                    {
                        _record = record;
                        _awaitingCommit = true;
                    }

                    _logger.LogInformation(
                        "This is version {Version} of an update from {From}; printing resumes once Windows Installer has committed it.",
                        record.ToVersion,
                        record.FromVersion);
                    _commitTask = Task.Run(() => WaitForCommitAsync(activateLater, _lifetime.Token));
                    Changed?.Invoke(this, EventArgs.Empty);
                    return false;
                }

                // After a rollback the setup that ran from the file may still be finishing: it is deleted once free.
                DeleteStagedFilesSoon();
                if (from == _installedVersion)
                {
                    _logger.LogWarning(
                        "The update to {Version} was not applied (the setup did not run, was cancelled, failed or rolled back); version {Installed} is running.",
                        record.ToVersion,
                        record.FromVersion);
                    Record(record.Finish(PrintBridgeUpdateStage.NotApplied));
                }
                else
                {
                    _logger.LogError(
                        "After an update from {From} to {To}, version {Installed} is running.",
                        record.FromVersion,
                        record.ToVersion,
                        PrintBridgeReleaseVersion.Format(_installedVersion));
                    Record(record.Finish(PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.UnexpectedVersion));
                }

                return true;

            default:
                // A finished attempt is shown as it ended; a file left beside it belongs to no attempt.
                DeleteStagedFilesSoon();
                lock (_sync)
                    _record = record;
                return true;
        }
    }

    /// <summary>The installed version and the newer release the server offers, if any.</summary>
    public async Task<IpcUpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        if (_publisher is null)
            throw new LocalizedApplicationException("Update.Error.NotSigned");
        var client = _client ?? throw new LocalizedApplicationException("Update.Error.NotReady");

        PrintBridgeRelease? release;
        try
        {
            release = await client.GetLatestReleaseAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("The server's release description is not valid; no update is offered.");
            throw new LocalizedApplicationException("Update.Error.InvalidRelease");
        }

        var available = release is not null && release.Version > _installedVersion
            ? PrintBridgeReleaseVersion.Format(release.Version)
            : null;
        return new IpcUpdateCheckResult(PrintBridgeReleaseVersion.Format(_installedVersion), available);
    }

    /// <summary>
    /// Starts an attempt to update to <paramref name="version"/> and returns its id at once; its progress and result are in
    /// <see cref="Snapshot"/>. A second attempt while one runs (or one awaits its commit) is refused.
    /// </summary>
    public Guid Prepare(Version version)
    {
        if (_publisher is null)
            throw new LocalizedApplicationException("Update.Error.NotSigned");

        PrintBridgeEngineHost engine;
        WaslaPrintBridgeClient client;
        PrintBridgeUpdateRecord record;
        lock (_sync)
        {
            if (_engine is null || _client is null)
                throw new LocalizedApplicationException("Update.Error.NotReady");
            if (version <= _installedVersion)
                throw new LocalizedApplicationException("Update.Error.NotNewer");
            if (_awaitingCommit || _record is { IsFinished: false })
                throw new PrintBridgeEngineBusyException();

            engine = _engine;
            client = _client;
            record = new PrintBridgeUpdateRecord(
                PrintBridgeUpdateRecord.CurrentSchemaVersion,
                Guid.NewGuid(),
                PrintBridgeReleaseVersion.Format(_installedVersion),
                PrintBridgeReleaseVersion.Format(version),
                0,
                string.Empty,
                PrintBridgeUpdateStage.Downloading,
                DateTime.UtcNow,
                ReadyAtUtc: null,
                FinishedAtUtc: null,
                Failure: null);
            try
            {
                _store.Save(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The update state could not be written; no update is started.");
                throw new LocalizedApplicationException("Update.Error.Storage");
            }

            _record = record;
            _downloadPercent = 0;
            _cleanup?.Cancel();
            _attempt?.Dispose();
            _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _attempt.Token;
            _attemptTask = Task.Run(() => RunAttemptAsync(record, version, engine, client, token), CancellationToken.None);
        }

        _logger.LogInformation("An update from {From} to {To} was started.", record.FromVersion, record.ToVersion);
        Changed?.Invoke(this, EventArgs.Empty);
        return record.AttemptId;
    }

    /// <summary>
    /// Cancels <paramref name="attemptId"/> unless it is finished or awaiting its commit: a download stops, the staged setup is
    /// deleted, and printing resumes. Repeating it does nothing.
    /// </summary>
    public async Task CancelAsync(Guid attemptId, PrintBridgeUpdateFailure? reason = null)
    {
        CancellationTokenSource? attempt;
        IDisposable? lease;
        Task running;
        lock (_sync)
        {
            if (_record is not { IsFinished: false } current || current.AttemptId != attemptId || _awaitingCommit)
                return;

            attempt = _attempt;
            lease = _lease;
            _lease = null;
            _expiry?.Dispose();
            _expiry = null;
            Publish(current.Finish(PrintBridgeUpdateStage.Cancelled, reason));
            running = _attemptTask;
        }

        if (attempt is not null)
            await attempt.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
        }

        lease?.Dispose();
        DeleteStagedFilesSoon();
        _logger.LogInformation("The update attempt was cancelled{Reason}; printing is no longer paused for it.", reason is null ? string.Empty : $" ({reason})");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The app that started <paramref name="attemptId"/> disconnected: an attempt not yet ready is cancelled.</summary>
    public Task ConnectionClosedAsync(Guid attemptId)
    {
        lock (_sync)
        {
            if (_record is not { IsFinished: false } current || current.AttemptId != attemptId
                || current.Stage == PrintBridgeUpdateStage.Ready)
                return Task.CompletedTask;
        }

        return CancelAsync(attemptId);
    }

    /// <summary>The service is stopping: background work ends; the durable state stays for the next start.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
            await _lifetime.CancelAsync().ConfigureAwait(false);

        Timer? expiry;
        lock (_sync)
        {
            expiry = _expiry;
            _expiry = null;
        }

        if (expiry is not null)
            await expiry.DisposeAsync().ConfigureAwait(false);

        foreach (var task in new[] { _attemptTask, _commitTask, _cleanupTask })
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAttemptAsync(
        PrintBridgeUpdateRecord record,
        Version version,
        PrintBridgeEngineHost engine,
        WaslaPrintBridgeClient client,
        CancellationToken ct)
    {
        var staged = PrintBridgeUpdateStateStore.StagedSetupPath(version);
        var partial = PrintBridgeUpdateStateStore.PartialSetupPath(version);
        try
        {
            // The release must still be the one the user confirmed.
            var release = await client.GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (release is null || release.Version != version)
            {
                Fail(record, PrintBridgeUpdateFailure.VersionMismatch);
                return;
            }

            record = Advance(record with { SetupBytes = release.SetupBytes, SetupSha256 = release.SetupSha256 });

            _store.DeleteStagedFiles();
            Directory.CreateDirectory(PrintBridgePaths.UpdatesDirectory);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var progress = new DownloadProgress(this, release.SetupBytes);
                await client.DownloadReleaseSetupAsync(release, file, progress, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(partial, staged, overwrite: true);

            record = Advance(record with { Stage = PrintBridgeUpdateStage.Verifying });
            var verification = PrintBridgeSetupVerifier.Verify(staged, release, _publisher!, _installedVersion);
            if (verification != PrintBridgeSetupVerification.Verified)
            {
                _logger.LogWarning("The downloaded setup of {Version} was refused: {Check}. It is deleted and never run.", record.ToVersion, verification);
                _store.DeleteStagedFiles();
                Fail(record, PrintBridgeSetupVerifier.ToFailure(verification));
                return;
            }

            record = Advance(record with { Stage = PrintBridgeUpdateStage.WaitingForPrinting });
            var lease = await engine.TryBeginUpdateAsync(PrintingWait, ct).ConfigureAwait(false);
            if (lease is null)
            {
                _logger.LogInformation("A print job did not finish within {Seconds} s; the update to {Version} was not started.", PrintingWait.TotalSeconds, record.ToVersion);
                _store.DeleteStagedFiles();
                Fail(record, PrintBridgeUpdateFailure.PrintingInProgress);
                return;
            }

            lock (_sync)
            {
                if (_record?.AttemptId != record.AttemptId || _record.IsFinished || ct.IsCancellationRequested)
                {
                    lease.Dispose();
                    return;
                }

                _lease = lease;
            }

            record = Advance(record with { Stage = PrintBridgeUpdateStage.Ready, ReadyAtUtc = DateTime.UtcNow });
            ScheduleExpiry(record.AttemptId);
            _logger.LogInformation("The verified setup of {Version} is ready; printing is paused until it has run.", record.ToVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled (recorded by CancelAsync) or the service is stopping: the next start drops what is left.
            TryDelete(partial);
        }
        catch (PrintBridgeConnectionException ex)
        {
            _logger.LogWarning("The setup of {Version} could not be downloaded: {Issue}.", record.ToVersion, ex.IssueCode);
            TryDelete(partial);
            Fail(record, PrintBridgeUpdateFailure.Download);
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("The server's answer for the setup of {Version} does not match its release description.", record.ToVersion);
            TryDelete(partial);
            _store.DeleteStagedFiles();
            Fail(record, PrintBridgeUpdateFailure.SizeMismatch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The update to {Version} could not use the updates folder.", record.ToVersion);
            TryDelete(partial);
            _store.DeleteStagedFiles();
            Fail(record, PrintBridgeUpdateFailure.Storage);
        }
        catch (Exception ex) when (ex is SupersededException)
        {
            TryDelete(partial);
        }
    }

    private async Task WaitForCommitAsync(Action activate, CancellationToken ct)
    {
        DateTime? idleSince = null;
        PrintBridgeUpdateStage outcome;
        PrintBridgeUpdateFailure? failure = null;
        while (true)
        {
            bool installing;
            Version? registered = null;
            try
            {
                installing = _product.InstallerIsRunning();
                if (!installing)
                    registered = _product.InstalledVersion();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
            {
                installing = false;
            }

            if (!installing && registered == _installedVersion)
            {
                outcome = PrintBridgeUpdateStage.Succeeded;
                _logger.LogInformation("Windows Installer committed version {Version}; printing resumes.", PrintBridgeReleaseVersion.Format(_installedVersion));
                break;
            }

            if (installing)
            {
                idleSince = null;
            }
            else
            {
                idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - idleSince >= CommitIdleLimit)
                {
                    outcome = PrintBridgeUpdateStage.Failed;
                    failure = PrintBridgeUpdateFailure.UnexpectedVersion;
                    _logger.LogError(
                        "Windows Installer reports version {Registered} while {Version} is running; printing resumes without a confirmed commit.",
                        registered is null ? "none" : PrintBridgeReleaseVersion.Format(registered),
                        PrintBridgeReleaseVersion.Format(_installedVersion));
                    break;
                }
            }

            try
            {
                await Task.Delay(CommitPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The service stops (a rollback stops this version): the state stays Ready for the next start.
                return;
            }
        }

        lock (_sync)
        {
            _awaitingCommit = false;
            if (_record is { } awaiting)
                Publish(awaiting.Finish(outcome, failure));
        }

        DeleteStagedFilesSoon();
        Changed?.Invoke(this, EventArgs.Empty);
        activate();
    }

    /// <summary>
    /// Deletes the staged files now and, if one is still in use, again every <see cref="CleanupRetryInterval"/> for up to
    /// <see cref="CleanupLimit"/>. A new attempt or the service's stop ends the retries; the next start deletes what is left.
    /// </summary>
    private void DeleteStagedFilesSoon()
    {
        CancellationToken token;
        lock (_sync)
        {
            _cleanup?.Cancel();
            _cleanup?.Dispose();
            _cleanup = null;
            if (_store.DeleteStagedFiles())
                return;

            _cleanup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _cleanup.Token;
            var previous = _cleanupTask;
            _cleanupTask = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await RetryAsync(token).ConfigureAwait(false);
            });
        }

        async Task RetryAsync(CancellationToken ct)
        {
            var until = DateTime.UtcNow + CleanupLimit;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    await Task.Delay(CleanupRetryInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // Every cleanup starts after an attempt finished; under the lock a new attempt starts (and cancels this), so
                // nothing of a new attempt is deleted here.
                lock (_sync)
                {
                    if (ct.IsCancellationRequested || _record is { IsFinished: false } || _awaitingCommit)
                        return;
                    if (_store.DeleteStagedFiles())
                    {
                        _logger.LogInformation("The staged update setup was deleted once it was no longer in use.");
                        return;
                    }
                }
            }

            _logger.LogWarning("A staged update setup is still in use after {Minutes} min; it is deleted at the next start.", CleanupLimit.TotalMinutes);
        }
    }

    private void ScheduleExpiry(Guid attemptId)
    {
        lock (_sync)
        {
            _expiry?.Dispose();
            _expiry = new Timer(
                _ => _ = CancelAsync(attemptId, PrintBridgeUpdateFailure.Expired),
                null,
                GateLifetime,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Records the next step of a running attempt; throws when it was cancelled or replaced meanwhile.</summary>
    private PrintBridgeUpdateRecord Advance(PrintBridgeUpdateRecord next)
    {
        lock (_sync)
        {
            if (_record is not { IsFinished: false } current || current.AttemptId != next.AttemptId)
                throw new SupersededException();

            _store.Save(next);
            _record = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return next;
    }

    private void Fail(PrintBridgeUpdateRecord record, PrintBridgeUpdateFailure failure)
    {
        lock (_sync)
        {
            if (_record is not { IsFinished: false } current || current.AttemptId != record.AttemptId)
                return;
            Publish(current.Finish(PrintBridgeUpdateStage.Failed, failure));
        }

        _logger.LogWarning("The update to {Version} failed: {Failure}.", record.ToVersion, failure);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a decision at start (reconciliation).</summary>
    private void Record(PrintBridgeUpdateRecord record)
    {
        lock (_sync)
            Publish(record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Makes <paramref name="record"/> the current one, written first: the state the app sees is never ahead of the file a
    /// restart reads. Called under the lock. A failed write is logged; the record still applies to this run.
    /// </summary>
    private void Publish(PrintBridgeUpdateRecord record)
    {
        TrySave(record);
        _record = record;
    }

    private void TrySave(PrintBridgeUpdateRecord record)
    {
        try
        {
            _store.Save(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The update state could not be written.");
        }
    }

    private void ReportDownloaded(long bytes, long total)
    {
        var percent = total <= 0 ? 0 : (int)Math.Clamp(bytes * 100 / total, 0, 100);
        lock (_sync)
        {
            if (_downloadPercent == percent)
                return;
            _downloadPercent = percent;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "A partial update download could not be deleted.");
        }
    }

    private sealed class DownloadProgress(PrintBridgeUpdateCoordinator owner, long total) : IProgress<long>
    {
        public void Report(long value) => owner.ReportDownloaded(value, total);
    }

    /// <summary>The attempt was cancelled or replaced while it ran; it stops without recording anything.</summary>
    private sealed class SupersededException : Exception
    {
    }
}
