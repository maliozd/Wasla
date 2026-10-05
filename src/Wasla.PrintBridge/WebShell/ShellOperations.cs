using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

public enum ShellOperationOutcome
{
    Succeeded,
    Failed,
    Busy,
    Duplicate,
    Rejected,
    Cancelled
}

/// <summary>Result reported to the page. <see cref="Message"/> is localized and never contains exception text.</summary>
/// <param name="NavigateTo">A tab the page should show next (one of <see cref="ShellMessageContract.Tabs"/>), or null.</param>
public sealed record ShellOperationResult(
    string Operation,
    string? RequestId,
    ShellOperationOutcome Outcome,
    string Message,
    string? NavigateTo = null);

/// <summary>Operations currently running in the host, so the page can show busy state from host truth.</summary>
public sealed record ShellBusyState(
    bool Engine,
    bool ConnectionTest,
    bool ConnectionReset,
    bool PrintersRefresh,
    bool PrinterSave,
    bool TestPrint,
    bool Reprint,
    bool ConnectionSetup,
    bool SettingsSave)
{
    public static readonly ShellBusyState Idle = new(false, false, false, false, false, false, false, false, false);
}

/// <summary>
/// Executes the state-changing page commands. Each operation group is single-flight (a second request while one
/// runs is answered with <see cref="ShellOperationOutcome.Busy"/>), each request id is honored once, and every
/// failure is mapped to a localized message. The engine itself keeps owning polling, printing and tokens.
/// While the connection dialog is open, start, stop, connection checks and reset wait for it (and the reverse),
/// so nothing changes the engine underneath a connection that is being replaced.
/// </summary>
public sealed class ShellOperations
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private enum Group
    {
        Engine,
        ConnectionTest,
        ConnectionReset,
        PrintersRefresh,
        PrinterSave,
        TestPrint,
        Reprint,
        ConnectionSetup,
        SettingsSave
    }

    private static readonly Group[] ConnectionGroups = [Group.Engine, Group.ConnectionTest, Group.ConnectionReset];

    private readonly IPrintBridgeEngine _engine;
    private readonly IPrinterCatalog _printers;
    private readonly IShellPrinterSettings _printerSettings;
    private readonly IShellOperationalSettings _operationalSettings;
    private readonly IShellNativeActions _native;
    private readonly ShellHistory _history;
    private readonly PrintBridgeSettingsHolder _settings;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly int[] _running = new int[Enum.GetValues<Group>().Length];
    private readonly RecentRequestIds _requestIds = new(capacity: 256);

    public ShellOperations(
        IPrintBridgeEngine engine,
        IPrinterCatalog printers,
        IShellPrinterSettings printerSettings,
        IShellOperationalSettings operationalSettings,
        IShellNativeActions native,
        ShellHistory history,
        PrintBridgeSettingsHolder settings,
        PrintBridgeLocalizer localizer,
        ILogger logger,
        TimeSpan? timeout = null)
    {
        _engine = engine;
        _printers = printers;
        _printerSettings = printerSettings;
        _operationalSettings = operationalSettings;
        _native = native;
        _history = history;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Raised from any thread when an operation starts or finishes.</summary>
    public event EventHandler? StateChanged;

    public ShellBusyState Busy => new(
        IsRunning(Group.Engine),
        IsRunning(Group.ConnectionTest),
        IsRunning(Group.ConnectionReset),
        IsRunning(Group.PrintersRefresh),
        IsRunning(Group.PrinterSave),
        IsRunning(Group.TestPrint),
        IsRunning(Group.Reprint),
        IsRunning(Group.ConnectionSetup),
        IsRunning(Group.SettingsSave));

    public static bool IsOperation(ShellCommandType type) => GroupFor(type) is not null || type == ShellCommandType.LogsOpenFolder;

    /// <summary>Discovers printers once without a page request (first page load). Errors are only logged.</summary>
    public async Task EnsurePrintersDiscoveredAsync()
    {
        if (_printers.HasDiscovered || !TryEnter(Group.PrintersRefresh))
            return;

        try
        {
            using var cts = new CancellationTokenSource(_timeout);
            await _printers.RefreshAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell could not list installed printers.");
        }
        finally
        {
            Exit(Group.PrintersRefresh);
        }
    }

    public async Task<ShellOperationResult> ExecuteAsync(ShellCommand command)
    {
        var operation = OperationName(command.Type);
        if (!IsOperation(command.Type))
            return new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Rejected, _localizer["Shell.Op.Failed"]);

        if (command.RequestId is { } requestId && !_requestIds.TryAdd(requestId))
            return new ShellOperationResult(operation, requestId, ShellOperationOutcome.Duplicate, string.Empty);

        if (command.Type == ShellCommandType.LogsOpenFolder)
            return OpenLogFolder(command);

        var group = GroupFor(command.Type)!.Value;
        if (BlockedByConnectionSetup(group) || !TryEnter(group))
            return new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Busy, _localizer["Shell.Op.Busy"]);

        try
        {
            if (group == Group.ConnectionSetup)
                return await RunConnectionSetupAsync(command).ConfigureAwait(true);

            var (outcome, message) = await RunAsync(command).ConfigureAwait(true);
            return new ShellOperationResult(operation, command.RequestId, outcome, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell operation failed. Operation={Operation}", operation);
            return new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Failed, Describe(ex));
        }
        finally
        {
            Exit(group);
        }
    }

    private async Task<(ShellOperationOutcome Outcome, string Message)> RunAsync(ShellCommand command)
    {
        switch (command.Type)
        {
            case ShellCommandType.EngineStart:
            {
                if (_engine.IsRunning)
                    return (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.Started"]);

                var token = _settings.OrderHub.AgentToken;
                if (PrintBridgePollingGate.RequiresReconnectBeforeStart(token, _engine.GetStatus().LastIssue))
                    return (ShellOperationOutcome.Rejected, _localizer["Message.ReconnectFromWebToContinue"]);

                _engine.Start();
                return (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.Started"]);
            }

            case ShellCommandType.EngineStop:
                if (_engine.IsRunning)
                    await _engine.StopAsync().ConfigureAwait(true);
                return (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.Stopped"]);

            case ShellCommandType.ConnectionTest:
            {
                using var cts = new CancellationTokenSource(_timeout);
                await _engine.TestConnectionAsync(cts.Token).ConfigureAwait(true);
                return (ShellOperationOutcome.Succeeded, _localizer["Message.ConnectionSuccess"]);
            }

            case ShellCommandType.ConnectionReset:
                if (!await _native.ConfirmConnectionResetAsync().ConfigureAwait(true))
                    return (ShellOperationOutcome.Cancelled, _localizer["Shell.Op.ResetCancelled"]);

                await _engine.ResetConnectionForReconnectAsync().ConfigureAwait(true);
                return (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.ResetDone"]);

            case ShellCommandType.SettingsSave:
            {
                var change = new ShellOperationalSettingsChange(
                    command.TestMode!.Value,
                    command.IdlePollSeconds!.Value,
                    command.BusyPollSeconds!.Value,
                    command.ErrorPollSeconds!.Value);

                // Turning test mode on stops paper printing while jobs are still reported as printed, so the host
                // asks in a native dialog; the page alone can never turn it on.
                if (change.TestMode && !_settings.Bridge.DryRun && !await _native.ConfirmEnableTestModeAsync().ConfigureAwait(true))
                    return (ShellOperationOutcome.Cancelled, _localizer["Shell.Op.TestModeCancelled"]);

                return _operationalSettings.TrySave(change, out var errorKey)
                    ? (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.SettingsSaved"])
                    : (ShellOperationOutcome.Rejected, _localizer[errorKey ?? "Shell.Op.Failed"]);
            }

            case ShellCommandType.PrintersRefresh:
            {
                using var cts = new CancellationTokenSource(_timeout);
                await _printers.RefreshAsync(cts.Token).ConfigureAwait(true);
                return (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.PrintersRefreshed"]);
            }

            case ShellCommandType.PrinterSave:
            {
                if (!_printers.HasDiscovered)
                {
                    using var cts = new CancellationTokenSource(_timeout);
                    await _printers.RefreshAsync(cts.Token).ConfigureAwait(true);
                }

                var name = command.PrinterName!;
                if (!_printers.IsInstalled(name))
                    return (ShellOperationOutcome.Rejected, _localizer["Shell.Printer.NotInstalled"]);

                return _printerSettings.TrySavePrinter(name, out var errorKey)
                    ? (ShellOperationOutcome.Succeeded, _localizer["Shell.Op.PrinterSaved"])
                    : (ShellOperationOutcome.Rejected, _localizer[errorKey ?? "Shell.Op.Failed"]);
            }

            case ShellCommandType.PrinterTestPrint:
            {
                using var cts = new CancellationTokenSource(_timeout);
                await _engine.TestPrinterAsync(cts.Token).ConfigureAwait(true);
                return (ShellOperationOutcome.Succeeded, _localizer["Message.TestPrintSent"]);
            }

            case ShellCommandType.HistoryReprint:
            {
                var jobId = _history.Resolve(command.HistoryItemRef!);
                if (jobId is null)
                    return (ShellOperationOutcome.Rejected, _localizer.GetReprintMessage("Reprint.JobNotFound"));

                using var cts = new CancellationTokenSource(_timeout);
                var result = await _engine.ReprintJobAsync(jobId.Value, cts.Token).ConfigureAwait(true);
                // A success without a message key must not fall through to the generic "reprint failed" text.
                var messageKey = string.IsNullOrWhiteSpace(result.MessageKey) ? "Reprint.Created" : result.MessageKey;
                return (ShellOperationOutcome.Succeeded, _localizer.GetReprintMessage(messageKey));
            }

            default:
                return (ShellOperationOutcome.Rejected, _localizer["Shell.Op.Failed"]);
        }
    }

    /// <summary>
    /// Waits for the native connection dialog. Only its localized message reaches the page; when the bridge is
    /// connected but has no usable printer yet, the page is asked to show the Printer tab.
    /// </summary>
    private async Task<ShellOperationResult> RunConnectionSetupAsync(ShellCommand command)
    {
        const string operation = ShellMessageContract.ConnectionOpenSetup;
        var result = await _native.RunConnectionSetupAsync().ConfigureAwait(true);
        return result.Outcome switch
        {
            ShellConnectionSetupOutcome.Connected =>
                new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Succeeded, result.Message),
            ShellConnectionSetupOutcome.ConnectedChoosePrinter =>
                new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Succeeded, result.Message, NavigateTo: "printer"),
            ShellConnectionSetupOutcome.Cancelled =>
                new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Cancelled, result.Message),
            _ => new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Failed, result.Message)
        };
    }

    private bool BlockedByConnectionSetup(Group group) =>
        group == Group.ConnectionSetup
            ? ConnectionGroups.Any(IsRunning)
            : ConnectionGroups.Contains(group) && IsRunning(Group.ConnectionSetup);

    private ShellOperationResult OpenLogFolder(ShellCommand command)
    {
        const string operation = ShellMessageContract.LogsOpenFolder;
        try
        {
            return _native.OpenLogFolder()
                ? new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Succeeded, _localizer["Shell.Op.LogsOpened"])
                : new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Failed, _localizer["Message.LogsFolderMissing"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell could not open the log folder.");
            return new ShellOperationResult(operation, command.RequestId, ShellOperationOutcome.Failed, _localizer["Shell.Op.Failed"]);
        }
    }

    /// <summary>Only resource-backed text reaches the page; anything else becomes a generic message.</summary>
    internal string Describe(Exception ex) => ex switch
    {
        PrintBridgeConnectionException connection => _localizer.GetRuntimeIssueDetail(
            new PrintBridgeRuntimeIssue(connection.IssueCode, connection.UserMessageKey, connection.FormatArgs)),
        LocalizedApplicationException localized when localized.ResourceKey.StartsWith("Reprint.", StringComparison.Ordinal)
                                                     || localized.ResourceKey.StartsWith("PrintBridge.Reprint", StringComparison.Ordinal)
            => _localizer.GetReprintMessage(localized.ResourceKey),
        LocalizedApplicationException localized => _localizer.GetString(localized.ResourceKey, localized.Args),
        OperationCanceledException => _localizer["Shell.Op.TimedOut"],
        _ => _localizer["Shell.Op.Failed"]
    };

    internal static string OperationName(ShellCommandType type) =>
        ShellMessageContract.Commands.First(c => c.Value.Type == type).Key;

    private static Group? GroupFor(ShellCommandType type) => type switch
    {
        ShellCommandType.EngineStart or ShellCommandType.EngineStop => Group.Engine,
        ShellCommandType.ConnectionTest => Group.ConnectionTest,
        ShellCommandType.ConnectionReset => Group.ConnectionReset,
        ShellCommandType.PrintersRefresh => Group.PrintersRefresh,
        ShellCommandType.PrinterSave => Group.PrinterSave,
        ShellCommandType.PrinterTestPrint => Group.TestPrint,
        ShellCommandType.HistoryReprint => Group.Reprint,
        ShellCommandType.ConnectionOpenSetup => Group.ConnectionSetup,
        ShellCommandType.SettingsSave => Group.SettingsSave,
        _ => null
    };

    private bool IsRunning(Group group) => Volatile.Read(ref _running[(int)group]) == 1;

    private bool TryEnter(Group group)
    {
        if (Interlocked.Exchange(ref _running[(int)group], 1) == 1)
            return false;

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Exit(Group group)
    {
        Volatile.Write(ref _running[(int)group], 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Bounded memory of request ids already handled, so a re-sent click is never executed twice.</summary>
    private sealed class RecentRequestIds(int capacity)
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public bool TryAdd(string requestId)
        {
            lock (_sync)
            {
                if (!_seen.Add(requestId))
                    return false;

                _order.Enqueue(requestId);
                while (_order.Count > capacity)
                    _seen.Remove(_order.Dequeue());
                return true;
            }
        }
    }
}
