using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.WebShell;

/// <summary>Starts the verified setup (a seam for tests).</summary>
public interface IShellSetupLauncher
{
    /// <summary>
    /// Starts the setup at <paramref name="path"/> with no arguments and without elevation: its full window opens and it asks
    /// Windows for administrator permission itself. Returns a task that completes when it exits, or null when its exit cannot
    /// be observed. Throws when it cannot start; a refused permission prompt is <see cref="Win32Exception"/> 1223.
    /// </summary>
    Task? Start(string path);
}

/// <summary>The app's own check of the staged setup right before it starts it (a seam for tests).</summary>
public interface IShellSetupVerifier
{
    /// <summary>The publisher that signed this app, or null when it is not validly signed (then no setup is trusted).</summary>
    PrintBridgePublisher? AppPublisher();

    PrintBridgeSetupVerification Verify(string path, PrintBridgeRelease release, PrintBridgePublisher appPublisher, Version appVersion);
}

/// <summary>Starts the setup as Windows starts any program the user opens: unelevated, its own window, no arguments.</summary>
public sealed class ShellSetupLauncher : IShellSetupLauncher
{
    public static readonly ShellSetupLauncher Instance = new();

    public Task? Start(string path)
    {
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return process is null ? null : WaitForExitAsync(process);
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using (process)
            await process.WaitForExitAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The same check the service made after the download (<see cref="PrintBridgeSetupVerifier"/>), with this app's own
/// signature as the trust anchor.
/// </summary>
public sealed class ShellSetupVerifier : IShellSetupVerifier
{
    public static readonly ShellSetupVerifier Instance = new();

    public PrintBridgePublisher? AppPublisher() => PrintBridgeAuthenticode.OfRunningExecutable();

    public PrintBridgeSetupVerification Verify(string path, PrintBridgeRelease release, PrintBridgePublisher appPublisher, Version appVersion) =>
        PrintBridgeSetupVerifier.Verify(path, release, appPublisher, appVersion);
}

/// <summary>
/// The app's part of an update (WAS-63, docs/integrations/print-bridge-updates.md): it checks for a newer release, asks the
/// user in a native confirmation, asks the service to prepare that release, and when the service reports the verified setup
/// ready it verifies the staged file again and opens it. If the setup ends without installing, it cancels the attempt so
/// printing resumes. It never elevates, never exits the app (the setup closes it) and takes nothing from the page but the
/// command. What the page may show is <see cref="CreateView"/>; failure codes go to the app log only.
/// </summary>
public sealed class ShellUpdates : IDisposable
{
    /// <summary>ERROR_CANCELLED: the user refused Windows' permission prompt.</summary>
    private const int ErrorCancelled = 1223;

    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(30);

    private enum SetupStep
    {
        None,

        /// <summary>The staged setup is being checked again; it is not started yet and may still be cancelled.</summary>
        Opening,

        /// <summary>The setup was started (or is about to be): cancelling is done in its own window.</summary>
        Open,

        /// <summary>The setup ended, or was never started.</summary>
        Done
    }

    /// <summary>What this app decided about its own attempt, which the service records only as cancelled.</summary>
    private enum AppOutcome
    {
        None,
        VerificationRefused,
        SetupNotStarted,
        SetupClosed
    }

    private readonly object _sync = new();
    private readonly IPrintBridgeUpdates _updates;
    private readonly IPrintBridgeStatusSource _changes;
    private readonly IShellNativeActions _native;
    private readonly IShellSetupLauncher _launcher;
    private readonly IShellSetupVerifier _verifier;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _culture;
    private readonly ILogger _logger;
    private readonly Version _appVersion;

    private bool _checked;
    private string? _available;
    private Guid? _checkedAgainst;
    private Guid? _owned;
    private SetupStep _step;
    private AppOutcome _outcome;
    private Guid? _cancelRequested;
    private Guid? _loggedResult;
    private Task _flow = Task.CompletedTask;
    private bool _disposed;

    /// <param name="changes">Raises an event whenever the service's state (and so <see cref="IPrintBridgeUpdates.CurrentUpdate"/>) may have changed.</param>
    /// <param name="appVersion">This app's release version; by default its assembly's.</param>
    public ShellUpdates(
        IPrintBridgeUpdates updates,
        IPrintBridgeStatusSource changes,
        IShellNativeActions native,
        IShellSetupLauncher launcher,
        IShellSetupVerifier verifier,
        PrintBridgeLocalizer localizer,
        PrintBridgeCultureService culture,
        ILogger logger,
        Version? appVersion = null)
    {
        _updates = updates;
        _changes = changes;
        _native = native;
        _launcher = launcher;
        _verifier = verifier;
        _localizer = localizer;
        _culture = culture;
        _logger = logger;
        _appVersion = appVersion ?? AppVersion;
        _changes.StatusChanged += OnStatusChanged;
    }

    /// <summary>This app's release version (its assembly version as <c>a.b.c</c>).</summary>
    public static Version AppVersion => PrintBridgeReleaseVersion.Of(typeof(Program).Assembly);

    /// <summary>For an engine that is not the service client: no update state, and every command fails as unavailable.</summary>
    public static IPrintBridgeUpdates None { get; } = new NoUpdates();

    /// <summary>Raised from any thread when the host's own part changed (a check result, the setup it opened).</summary>
    public event EventHandler? Changed;

    /// <summary>For tests: the current setup step (verification, start and the wait for its exit).</summary>
    internal Task SetupFlowForTests
    {
        get
        {
            lock (_sync)
                return _flow;
        }
    }

    /// <summary>The section as the page shows it, for a host without updates (the service cannot be reached).</summary>
    public static ShellUpdateView Unavailable(PrintBridgeLocalizer localizer, ShellUpdateBusyView busy) =>
        new(
            PrintBridgeReleaseVersion.Format(AppVersion),
            false,
            ShellUpdateStages.Unavailable,
            localizer["Shell.Update.Status.Unavailable"],
            null,
            null,
            null,
            null,
            null,
            ShellUpdateActionsView.None,
            busy);

    public ShellUpdateView CreateView(ShellUpdateBusyView busy)
    {
        var update = _updates.CurrentUpdate;
        if (update is null)
            return Unavailable(_localizer, busy) with { InstalledVersion = PrintBridgeReleaseVersion.Format(_appVersion) };

        if (!update.Supported)
            return View(update, ShellUpdateStages.Unsupported, _localizer["Shell.Update.Status.Unsupported"], busy, ShellUpdateActionsView.None);

        bool hasChecked;
        string? available;
        Guid? checkedAgainst;
        bool owned;
        SetupStep step;
        AppOutcome outcome;
        bool cancelRequested;
        lock (_sync)
        {
            hasChecked = _checked;
            available = _available;
            checkedAgainst = _checkedAgainst;
            owned = _owned is not null && _owned == update.AttemptId;
            step = owned ? _step : SetupStep.None;
            outcome = owned ? _outcome : AppOutcome.None;
            cancelRequested = _cancelRequested is not null && _cancelRequested == update.AttemptId;
        }

        var target = update.ToVersion ?? string.Empty;
        var cancel = new ShellUpdateActionsView(false, false, !cancelRequested);
        switch (update.Stage)
        {
            case PrintBridgeUpdateStage.Downloading:
            {
                int? percent = update.DownloadPercent is { } p ? Math.Clamp(p, 0, 100) : null;
                return View(update, ShellUpdateStages.Downloading, _localizer.GetString("Shell.Update.Status.Downloading", target), busy, cancel) with
                {
                    DownloadPercent = percent,
                    Progress = percent is { } value ? (value / 100.0).ToString("P0", _culture.CurrentCulture) : null
                };
            }

            case PrintBridgeUpdateStage.Verifying:
                return View(update, ShellUpdateStages.Verifying, _localizer["Shell.Update.Status.Verifying"], busy, cancel);

            case PrintBridgeUpdateStage.WaitingForPrinting:
                return View(update, ShellUpdateStages.WaitingForPrinting, _localizer["Shell.Update.Status.WaitingForPrinting"], busy, cancel);

            case PrintBridgeUpdateStage.Ready when update.StagedSetupPath is null:
                // The new version runs and waits for Windows Installer's commit: nothing can be cancelled any more.
                return View(update, ShellUpdateStages.Installing, _localizer.GetString("Shell.Update.Status.Installing", target), busy, ShellUpdateActionsView.None);

            case PrintBridgeUpdateStage.Ready when owned && step is SetupStep.None or SetupStep.Opening:
                return View(update, ShellUpdateStages.Opening, _localizer["Shell.Update.Status.Opening"], busy, cancel);

            case PrintBridgeUpdateStage.Ready when owned && step == SetupStep.Open:
                return View(update, ShellUpdateStages.SetupOpen, _localizer["Shell.Update.Status.SetupOpen"], busy, ShellUpdateActionsView.None);

            case PrintBridgeUpdateStage.Ready:
                return View(update, ShellUpdateStages.Ready, _localizer.GetString("Shell.Update.Status.Ready", target), busy, cancel);
        }

        // No attempt is in progress: a check, or the result of the last attempt, or nothing yet.
        var idle = !busy.Check && !busy.Start;
        var canStart = idle && available is not null && IsNewer(available, update.InstalledVersion);
        if (busy.Check)
            return View(update, ShellUpdateStages.Checking, _localizer["Shell.Update.Status.Checking"], busy, ShellUpdateActionsView.None);

        // A check made after the attempt ended is newer than its result.
        var checkIsNewer = hasChecked && checkedAgainst == update.AttemptId;
        if (update.Stage is { } stage && !checkIsNewer)
        {
            var (resultStage, failure, label) = Result(update, stage, outcome);
            return View(update, resultStage, label, busy, new ShellUpdateActionsView(idle, canStart && resultStage != ShellUpdateStages.Succeeded, false)) with
            {
                Failure = failure
            };
        }

        if (hasChecked)
        {
            return available is not null && canStart
                ? View(update, ShellUpdateStages.Available, _localizer.GetString("Shell.Update.Status.Available", available), busy, new ShellUpdateActionsView(idle, true, false))
                : View(update, ShellUpdateStages.UpToDate, _localizer["Shell.Update.Status.UpToDate"], busy, new ShellUpdateActionsView(idle, false, false));
        }

        return View(update, ShellUpdateStages.Idle, string.Empty, busy, new ShellUpdateActionsView(idle, false, false));
    }

    /// <summary><c>update.check</c>: asks the service for the newer release and remembers it for this session.</summary>
    internal async Task<(ShellOperationOutcome Outcome, string Message)> CheckAsync(CancellationToken ct)
    {
        if (_updates.CurrentUpdate is { Supported: false })
            return (ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.Unsupported"]);

        var result = await _updates.CheckForUpdateAsync(ct).ConfigureAwait(true);
        string? available = null;
        if (result.Available is not null)
        {
            if (PrintBridgeReleaseVersion.TryParse(result.Available, out var version))
                available = PrintBridgeReleaseVersion.Format(version);
            else
                _logger.LogWarning("The service offered a release version in an unknown form; no update is offered.");
        }

        lock (_sync)
        {
            _checked = true;
            _available = available;
            _checkedAgainst = _updates.CurrentUpdate?.AttemptId;
        }

        _logger.LogInformation("Update check: {Installed} is installed; newer release: {Available}.", result.Installed, available ?? "none");
        RaiseChanged();
        return available is null
            ? (ShellOperationOutcome.Succeeded, _localizer["Shell.Update.Status.UpToDate"])
            : (ShellOperationOutcome.Succeeded, _localizer.GetString("Shell.Update.Status.Available", available));
    }

    /// <summary>
    /// <c>update.start</c>: the native confirmation names the installed and the new version; only after it does the service
    /// prepare the version the last check found. Declining changes nothing and says nothing.
    /// </summary>
    internal async Task<(ShellOperationOutcome Outcome, string Message)> StartAsync(TimeSpan timeout)
    {
        var update = _updates.CurrentUpdate;
        if (update is null)
            return (ShellOperationOutcome.Failed, _localizer["Shell.Update.Status.Unavailable"]);
        if (!update.Supported)
            return (ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.Unsupported"]);
        if (update.Stage is PrintBridgeUpdateStage.Downloading or PrintBridgeUpdateStage.Verifying
            or PrintBridgeUpdateStage.WaitingForPrinting or PrintBridgeUpdateStage.Ready)
            return (ShellOperationOutcome.Busy, _localizer["Update.Error.InProgress"]);

        string? available;
        lock (_sync)
            available = _available;
        if (available is null || !IsNewer(available, update.InstalledVersion))
            return (ShellOperationOutcome.Rejected, _localizer["Shell.Update.CheckFirst"]);

        if (!await _native.ConfirmUpdateAsync(update.InstalledVersion, available).ConfigureAwait(true))
            return (ShellOperationOutcome.Cancelled, string.Empty);

        Guid attemptId;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            attemptId = await _updates.PrepareUpdateAsync(available, cts.Token).ConfigureAwait(true);
        }
        catch (PrintBridgeEngineBusyException)
        {
            // One attempt at a time for the whole computer: another app (or user) started one.
            return (ShellOperationOutcome.Busy, _localizer["Update.Error.InProgress"]);
        }

        lock (_sync)
        {
            _owned = attemptId;
            _step = SetupStep.None;
            _outcome = AppOutcome.None;
            _cancelRequested = null;
        }

        _logger.LogInformation("An update from {Installed} to {Version} was started (attempt {AttemptId}).", update.InstalledVersion, available, attemptId);
        RaiseChanged();
        Evaluate();
        return (ShellOperationOutcome.Succeeded, _localizer.GetString("Shell.Update.Started", available));
    }

    /// <summary>
    /// <c>update.cancel</c>: cancels the attempt shown, unless it is being installed or the setup this app opened is running
    /// (that one is cancelled in the setup's own window).
    /// </summary>
    internal async Task<(ShellOperationOutcome Outcome, string Message)> CancelAsync(CancellationToken ct)
    {
        var update = _updates.CurrentUpdate;
        if (update?.AttemptId is not { } attemptId
            || update.Stage is not (PrintBridgeUpdateStage.Downloading or PrintBridgeUpdateStage.Verifying
                or PrintBridgeUpdateStage.WaitingForPrinting or PrintBridgeUpdateStage.Ready))
            return (ShellOperationOutcome.Rejected, _localizer["Shell.Update.NothingToCancel"]);

        if (update.StagedSetupPath is null && update.Stage == PrintBridgeUpdateStage.Ready)
            return (ShellOperationOutcome.Rejected, _localizer.GetString("Shell.Update.Status.Installing", update.ToVersion ?? string.Empty));

        lock (_sync)
        {
            if (_owned == attemptId && _step == SetupStep.Open)
                return (ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.SetupOpen"]);

            // From here on the setup of this attempt is never opened.
            _cancelRequested = attemptId;
        }

        RaiseChanged();
        try
        {
            await _updates.CancelUpdateAsync(attemptId, ct).ConfigureAwait(true);
        }
        catch
        {
            lock (_sync)
            {
                if (_cancelRequested == attemptId)
                    _cancelRequested = null;
            }

            RaiseChanged();
            throw;
        }

        _logger.LogInformation("The update attempt {AttemptId} was cancelled from the app.", attemptId);
        return (ShellOperationOutcome.Succeeded, _localizer["Shell.Update.Status.Cancelled"]);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _changes.StatusChanged -= OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>Opens the staged setup once, when the attempt this app started reports it ready.</summary>
    private void Evaluate()
    {
        var update = _updates.CurrentUpdate;
        LogResultOnce(update);
        if (update is not { Stage: PrintBridgeUpdateStage.Ready, StagedSetupPath: not null, AttemptId: { } attemptId })
            return;

        lock (_sync)
        {
            if (_disposed || _owned != attemptId || _step != SetupStep.None || _cancelRequested == attemptId)
                return;

            _step = SetupStep.Opening;
            _flow = Task.Run(() => OpenSetupAsync(update, attemptId));
        }

        RaiseChanged();
    }

    private async Task OpenSetupAsync(IpcUpdate ready, Guid attemptId)
    {
        var next = SetupStep.Done;
        try
        {
            if (Describe(ready) is not { } staged)
            {
                await EndAsync(attemptId, AppOutcome.VerificationRefused).ConfigureAwait(false);
                return;
            }

            var publisher = _verifier.AppPublisher();
            if (publisher is null)
            {
                _logger.LogWarning("This Print Bridge app is not validly signed, so it trusts no setup; the update to {Version} is cancelled.", ready.ToVersion);
                await EndAsync(attemptId, AppOutcome.VerificationRefused).ConfigureAwait(false);
                return;
            }

            var verification = _verifier.Verify(staged.Path, staged.Release, publisher, _appVersion);
            if (verification != PrintBridgeSetupVerification.Verified)
            {
                _logger.LogWarning(
                    "The staged setup of {Version} failed the app's check ({Check}); it is not started and the update is cancelled.",
                    ready.ToVersion,
                    verification);
                await EndAsync(attemptId, AppOutcome.VerificationRefused).ConfigureAwait(false);
                return;
            }

            lock (_sync)
            {
                if (_disposed || _cancelRequested == attemptId)
                {
                    _logger.LogInformation("The update to {Version} was cancelled before its setup opened.", ready.ToVersion);
                    return;
                }

                // From here a cancel from the page is refused: the setup's own window cancels it.
                _step = SetupStep.Open;
            }

            RaiseChanged();
            Task? exit;
            try
            {
                exit = _launcher.Start(staged.Path);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                _logger.LogInformation("Windows' permission prompt for the setup of {Version} was refused; the update is cancelled.", ready.ToVersion);
                await EndAsync(attemptId, AppOutcome.SetupClosed).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
            {
                _logger.LogWarning(ex, "The setup of {Version} could not be started; the update is cancelled.", ready.ToVersion);
                await EndAsync(attemptId, AppOutcome.SetupNotStarted).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("The setup of {Version} was opened (attempt {AttemptId}).", ready.ToVersion, attemptId);
            if (exit is null)
            {
                _logger.LogInformation("The end of the setup cannot be observed; the service ends the pause if it does not install.");
                next = SetupStep.Open;
                return;
            }

            await exit.ConfigureAwait(false);

            // A setup that installs closes this app first; still running here means it ended without installing, unless the
            // service has moved on (another version answers, or it is restarting).
            var now = _updates.CurrentUpdate;
            if (now is { Stage: PrintBridgeUpdateStage.Ready } && now.AttemptId == attemptId
                && string.Equals(now.InstalledVersion, ready.InstalledVersion, StringComparison.Ordinal))
            {
                _logger.LogInformation("The setup of {Version} closed without installing it; the update is cancelled and printing resumes.", ready.ToVersion);
                await EndAsync(attemptId, AppOutcome.SetupClosed).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("The setup of {Version} closed; the service reports the result.", ready.ToVersion);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The update setup step failed unexpectedly.");
        }
        finally
        {
            lock (_sync)
            {
                if (_owned == attemptId)
                    _step = next;
            }

            RaiseChanged();
        }
    }

    /// <summary>
    /// The staged setup as the service described it: an absolute path directly in the updates folder, and a complete release
    /// description. Anything else is refused before the file is opened.
    /// </summary>
    private (string Path, PrintBridgeRelease Release)? Describe(IpcUpdate ready)
    {
        var reported = ready.StagedSetupPath!;
        string full;
        string folder;
        try
        {
            if (!Path.IsPathFullyQualified(reported))
            {
                _logger.LogWarning("The staged setup path is not absolute; it is not started.");
                return null;
            }

            full = Path.GetFullPath(reported);
            folder = Path.GetFullPath(PrintBridgePaths.UpdatesDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            _logger.LogWarning("The staged setup path is not valid; it is not started.");
            return null;
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(folder),
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("The staged setup is not in the updates folder; it is not started.");
            return null;
        }

        var release = PrintBridgeRelease.TryCreate(
            PrintBridgeRelease.SchemaVersion,
            ready.ToVersion,
            Path.GetFileName(full),
            ready.SetupBytes ?? 0,
            ready.SetupSha256);
        if (release is null)
        {
            _logger.LogWarning("The staged setup's description is incomplete; it is not started.");
            return null;
        }

        return (full, release);
    }

    /// <summary>Records why this app ended its attempt, then asks the service to cancel it so printing resumes.</summary>
    private async Task EndAsync(Guid attemptId, AppOutcome outcome)
    {
        lock (_sync)
        {
            if (_owned == attemptId)
                _outcome = outcome;
        }

        RaiseChanged();
        try
        {
            using var timeout = new CancellationTokenSource(CancelTimeout);
            await _updates.CancelUpdateAsync(attemptId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "The update attempt {AttemptId} could not be cancelled ({Error}); the service ends the pause when it expires.",
                attemptId,
                ex.GetType().Name);
        }
    }

    private (string Stage, string? Failure, string Label) Result(IpcUpdate update, PrintBridgeUpdateStage stage, AppOutcome outcome)
    {
        var target = update.ToVersion ?? string.Empty;
        switch (stage)
        {
            case PrintBridgeUpdateStage.Succeeded:
                return (ShellUpdateStages.Succeeded, null, _localizer.GetString("Shell.Update.Status.Succeeded", target));

            case PrintBridgeUpdateStage.NotApplied:
                return NotApplied(update);

            case PrintBridgeUpdateStage.Cancelled when update.Failure is { } reason:
                return Failed(FailureGroup(reason));

            case PrintBridgeUpdateStage.Cancelled when outcome == AppOutcome.VerificationRefused:
                return Failed(ShellUpdateFailures.Verification);

            case PrintBridgeUpdateStage.Cancelled when outcome == AppOutcome.SetupNotStarted:
                return Failed(ShellUpdateFailures.Setup);

            case PrintBridgeUpdateStage.Cancelled when outcome == AppOutcome.SetupClosed:
                // The setup (or Windows' permission prompt) was closed: the previous version stays, as after a rollback.
                return NotApplied(update);

            case PrintBridgeUpdateStage.Cancelled:
                return (ShellUpdateStages.Cancelled, null, _localizer["Shell.Update.Status.Cancelled"]);

            default:
                return Failed(update.Failure is { } failure ? FailureGroup(failure) : ShellUpdateFailures.Interrupted);
        }

        (string, string?, string) Failed(string group) =>
            (ShellUpdateStages.Failed, group, _localizer[FailureKey(group)]);
    }

    private (string Stage, string? Failure, string Label) NotApplied(IpcUpdate update) =>
        (ShellUpdateStages.NotApplied, null, _localizer.GetString("Shell.Update.Status.NotApplied", update.InstalledVersion));

    internal static string FailureGroup(PrintBridgeUpdateFailure failure) => failure switch
    {
        PrintBridgeUpdateFailure.Download => ShellUpdateFailures.Download,
        PrintBridgeUpdateFailure.SizeMismatch or PrintBridgeUpdateFailure.HashMismatch or PrintBridgeUpdateFailure.NotSigned
            or PrintBridgeUpdateFailure.SignatureInvalid or PrintBridgeUpdateFailure.PublisherMismatch
            or PrintBridgeUpdateFailure.VersionMismatch => ShellUpdateFailures.Verification,
        PrintBridgeUpdateFailure.PrintingInProgress => ShellUpdateFailures.PrintingInProgress,
        PrintBridgeUpdateFailure.Expired => ShellUpdateFailures.Expired,
        PrintBridgeUpdateFailure.Storage => ShellUpdateFailures.Storage,
        PrintBridgeUpdateFailure.UnexpectedVersion => ShellUpdateFailures.UnexpectedVersion,
        _ => ShellUpdateFailures.Interrupted
    };

    /// <summary>The resource key of a failure group's text.</summary>
    internal static string FailureKey(string group) => group switch
    {
        ShellUpdateFailures.Download => "Shell.Update.Failure.Download",
        ShellUpdateFailures.Verification => "Shell.Update.Failure.Verification",
        ShellUpdateFailures.PrintingInProgress => "Shell.Update.Failure.PrintingInProgress",
        ShellUpdateFailures.Expired => "Shell.Update.Failure.Expired",
        ShellUpdateFailures.Storage => "Shell.Update.Failure.Storage",
        ShellUpdateFailures.UnexpectedVersion => "Shell.Update.Failure.UnexpectedVersion",
        ShellUpdateFailures.Setup => "Shell.Update.Failure.Setup",
        _ => "Shell.Update.Failure.Interrupted"
    };

    /// <summary>The resource keys this class may show, in addition to the page's static strings.</summary>
    public static readonly IReadOnlyList<string> ResourceKeys =
    [
        "Shell.Update.Status.Unavailable",
        "Shell.Update.Status.Unsupported",
        "Shell.Update.Status.Checking",
        "Shell.Update.Status.UpToDate",
        "Shell.Update.Status.Available",
        "Shell.Update.Status.Downloading",
        "Shell.Update.Status.Verifying",
        "Shell.Update.Status.WaitingForPrinting",
        "Shell.Update.Status.Ready",
        "Shell.Update.Status.Opening",
        "Shell.Update.Status.SetupOpen",
        "Shell.Update.Status.Installing",
        "Shell.Update.Status.Succeeded",
        "Shell.Update.Status.NotApplied",
        "Shell.Update.Status.Cancelled",
        "Shell.Update.Failure.Download",
        "Shell.Update.Failure.Verification",
        "Shell.Update.Failure.PrintingInProgress",
        "Shell.Update.Failure.Interrupted",
        "Shell.Update.Failure.Expired",
        "Shell.Update.Failure.Storage",
        "Shell.Update.Failure.UnexpectedVersion",
        "Shell.Update.Failure.Setup",
        "Shell.Update.Started",
        "Shell.Update.CheckFirst",
        "Shell.Update.NothingToCancel",
        "Shell.Update.ConfirmTitle",
        "Shell.Update.ConfirmMessage",
        "Shell.Update.Confirm",
        "Shell.Update.NewVersion",
        "Update.Error.NotSigned",
        "Update.Error.NotReady",
        "Update.Error.InvalidRelease",
        "Update.Error.NotNewer",
        "Update.Error.Storage",
        "Update.Error.InProgress"
    ];

    private ShellUpdateView View(IpcUpdate update, string stage, string label, ShellUpdateBusyView busy, ShellUpdateActionsView actions)
    {
        string? available;
        lock (_sync)
            available = _available;

        return new ShellUpdateView(
            update.InstalledVersion,
            update.Supported,
            stage,
            label,
            available is not null && IsNewer(available, update.InstalledVersion) ? available : null,
            update.ToVersion,
            null,
            null,
            null,
            actions,
            busy);
    }

    private void LogResultOnce(IpcUpdate? update)
    {
        if (update is not { AttemptId: { } attemptId, Stage: PrintBridgeUpdateStage.Succeeded or PrintBridgeUpdateStage.Failed
                or PrintBridgeUpdateStage.NotApplied or PrintBridgeUpdateStage.Cancelled })
            return;

        lock (_sync)
        {
            if (_loggedResult == attemptId)
                return;
            _loggedResult = attemptId;
        }

        // The technical code is for support; the page shows only the user's words.
        _logger.LogInformation(
            "Update attempt {AttemptId} from {From} to {To}: {Stage}, reason {Failure}.",
            attemptId,
            update.FromVersion,
            update.ToVersion,
            update.Stage,
            update.Failure?.ToString() ?? "none");
    }

    private static bool IsNewer(string candidate, string installed) =>
        PrintBridgeReleaseVersion.TryParse(candidate, out var next)
        && (!PrintBridgeReleaseVersion.TryParse(installed, out var current) || next > current);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class NoUpdates : IPrintBridgeUpdates
    {
        public IpcUpdate? CurrentUpdate => null;

        public Task<IpcUpdateCheckResult> CheckForUpdateAsync(CancellationToken ct) => Task.FromException<IpcUpdateCheckResult>(Unavailable());

        public Task<Guid> PrepareUpdateAsync(string version, CancellationToken ct) => Task.FromException<Guid>(Unavailable());

        public Task CancelUpdateAsync(Guid attemptId, CancellationToken ct) => Task.FromException(Unavailable());

        private static LocalizedApplicationException Unavailable() =>
            new(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServiceUnavailable).EffectiveDetailResourceKey);
    }
}
