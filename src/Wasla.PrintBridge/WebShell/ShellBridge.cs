using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>What the bridge needs from the window hosting the WebView2 control.</summary>
public interface IShellHost
{
    /// <summary>True while the page can receive messages (WebView ready, window open and visible).</summary>
    bool IsAvailable { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread; silently dropped once the window is gone.</summary>
    void Post(Action action);

    /// <summary>Sends one serialized host message to the page. Called on the UI thread only.</summary>
    void PostWebMessageAsJson(string json);
}

public interface IShellLanguageSwitcher
{
    /// <summary>Persists and applies <paramref name="culture"/>; returns false when it could not be saved.</summary>
    Task<bool> ChangeAsync(string culture);
}

public enum ShellMessageOutcome
{
    Accepted,
    Rejected,
    Ignored
}

/// <summary>
/// Connects the page to the running engine. It reads engine state through <see cref="IPrintBridgeStatusSource"/>
/// and changes it only through <see cref="ShellOperations"/>, whose commands are allowlisted, single-flight and
/// de-duplicated. Page commands are limited to <see cref="ShellCommandType"/>; nothing from the page selects a
/// method, a path or a URL.
/// </summary>
public sealed class ShellBridge : IDisposable
{
    private readonly IPrintBridgeStatusSource _statusSource;
    private readonly ShellSnapshotFactory _snapshotFactory;
    private readonly IShellHost _host;
    private readonly IShellLanguageSwitcher _languageSwitcher;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ShellOperations _operations;
    private readonly ShellHistory _history;
    private readonly IShellNativeActions _native;
    private readonly ILogger _logger;

    private int _pushQueued;
    private bool _pageReady;
    private bool _languageChangeInFlight;
    private bool _disposed;
    private long _sequence;
    private long _snapshotCount;
    private string? _lastPayloadJson;
    private string? _pendingTab;
    private readonly Queue<ShellOperationResult> _pendingNotices = new();

    public ShellBridge(
        IPrintBridgeStatusSource statusSource,
        ShellSnapshotFactory snapshotFactory,
        IShellHost host,
        IShellLanguageSwitcher languageSwitcher,
        PrintBridgeCultureService cultureService,
        ShellOperations operations,
        ShellHistory history,
        IShellNativeActions native,
        ILogger logger)
    {
        _statusSource = statusSource;
        _snapshotFactory = snapshotFactory;
        _host = host;
        _languageSwitcher = languageSwitcher;
        _cultureService = cultureService;
        _operations = operations;
        _history = history;
        _native = native;
        _logger = logger;

        _statusSource.StatusChanged += OnStateChanged;
        _cultureService.CultureChanged += OnStateChanged;
        _operations.StateChanged += OnStateChanged;
    }

    /// <summary>Number of snapshots sent to the page so far.</summary>
    public long SentSnapshotCount => Interlocked.Read(ref _snapshotCount);

    /// <summary>
    /// Handles one message from the page. Must be called on the UI thread with the WebView2-reported
    /// source URI; the raw text is never logged.
    /// </summary>
    public ShellMessageOutcome HandleWebMessage(string? source, string? raw)
    {
        if (_disposed)
            return ShellMessageOutcome.Ignored;

        if (!ShellNavigationPolicy.IsTrustedMessageSource(source))
            return Reject(ShellMessageRejection.UntrustedSource, raw);

        if (!ShellMessageParser.TryParse(raw, out var command, out var rejection))
            return Reject(rejection, raw);

        switch (command!.Type)
        {
            case ShellCommandType.UiReady:
            case ShellCommandType.SnapshotRequest:
                // Repeated ready/request messages (for example after a reload) only resend the current state.
                _pageReady = true;
                PushSnapshot(force: true);
                if (command.Type == ShellCommandType.UiReady)
                {
                    FlushPending();
                    _ = _operations.EnsurePrintersDiscoveredAsync();
                }
                break;

            case ShellCommandType.LanguageChange:
                _ = ChangeLanguageAsync(command.Culture!);
                break;

            case ShellCommandType.ClassicWindowOpen:
                _native.OpenClassicWindow();
                break;

            case ShellCommandType.HistoryQuery:
                SendHistory(command);
                break;

            default:
                _ = RunOperationAsync(command);
                break;
        }

        return ShellMessageOutcome.Accepted;
    }

    /// <summary>Periodic refresh so time-based state (for example a contact that has gone stale) reaches the page.</summary>
    public void Refresh() => PushSnapshot(force: false);

    /// <summary>Resends the full state, for example when a hidden window is shown again.</summary>
    public void Resend() => PushSnapshot(force: true);

    /// <summary>
    /// Asks the page to show <paramref name="tab"/> (tray Print history and Settings). Kept until the page is ready
    /// when the window is still loading. Unknown tab names are ignored. UI thread only.
    /// </summary>
    public void Navigate(string tab)
    {
        if (_disposed || !ShellMessageContract.Tabs.Contains(tab))
            return;

        _pendingTab = tab;
        FlushPending();
    }

    /// <summary>
    /// Shows the result of a connection change the page did not ask for (a setup link), as an operation result
    /// without a request id. Kept until the page is ready. UI thread only.
    /// </summary>
    public void NotifyConnectionResult(ShellOperationOutcome outcome, string message, string? navigateTo = null)
    {
        if (_disposed)
            return;

        _pendingNotices.Enqueue(new ShellOperationResult(ShellMessageContract.ConnectionOpenSetup, null, outcome, message, navigateTo));
        FlushPending();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _statusSource.StatusChanged -= OnStateChanged;
        _cultureService.CultureChanged -= OnStateChanged;
        _operations.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Raised from engine threads, possibly many times per poll. Coalesce into one queued UI update.
        if (_disposed || Interlocked.Exchange(ref _pushQueued, 1) == 1)
            return;

        _host.Post(() =>
        {
            Interlocked.Exchange(ref _pushQueued, 0);
            PushSnapshot(force: false);
        });
    }

    private void PushSnapshot(bool force)
    {
        if (_disposed || !_pageReady || !_host.IsAvailable)
            return;

        var snapshot = _snapshotFactory.Create(_statusSource.GetStatus());
        var payloadJson = ShellMessageSerializer.SerializePayload(snapshot);
        if (!force && string.Equals(payloadJson, _lastPayloadJson, StringComparison.Ordinal))
            return;

        _lastPayloadJson = payloadJson;
        Interlocked.Increment(ref _snapshotCount);
        _host.PostWebMessageAsJson(ShellMessageSerializer.SerializeSnapshotMessage(snapshot, NextSequence()));
    }

    private void SendHistory(ShellCommand command)
    {
        if (!_host.IsAvailable)
            return;

        try
        {
            var page = _history.Query(
                command.HistoryRange ?? PrintHistoryDateFilter.Today,
                command.HistoryPage ?? 0,
                command.HistorySearch);
            _host.PostWebMessageAsJson(ShellMessageSerializer.SerializeHistoryResult(
                new ShellHistoryResult(command.RequestId!, page),
                NextSequence()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell could not read the local print history.");
            SendResult(new ShellOperationResult(
                ShellMessageContract.HistoryQuery,
                command.RequestId,
                ShellOperationOutcome.Failed,
                _operations.Describe(ex)));
        }
    }

    private async Task RunOperationAsync(ShellCommand command)
    {
        ShellOperationResult result;
        try
        {
            result = await _operations.ExecuteAsync(command).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell operation could not complete.");
            return;
        }

        // A repeated request id is the same click delivered twice; the first one is answered already.
        if (result.Outcome == ShellOperationOutcome.Duplicate)
            return;

        SendResult(result);
        PushSnapshot(force: true);
        if (result.NavigateTo is { } tab)
            Navigate(tab);
    }

    private void SendResult(ShellOperationResult result)
    {
        if (_disposed || !_host.IsAvailable)
            return;

        _host.PostWebMessageAsJson(ShellMessageSerializer.SerializeOperationResult(result, NextSequence()));
    }

    /// <summary>Delivers host-initiated notices and the requested tab once the page can render them.</summary>
    private void FlushPending()
    {
        if (_disposed || !_pageReady || !_host.IsAvailable)
            return;

        while (_pendingNotices.TryDequeue(out var notice))
        {
            SendResult(notice);
            if (notice.NavigateTo is { } next)
                _pendingTab = next;
        }

        if (_pendingTab is { } tab)
        {
            _pendingTab = null;
            _host.PostWebMessageAsJson(ShellMessageSerializer.SerializeNavigate(tab, NextSequence()));
        }
    }

    private async Task ChangeLanguageAsync(string culture)
    {
        if (_languageChangeInFlight)
            return;

        _languageChangeInFlight = true;
        try
        {
            var changed = await _languageSwitcher.ChangeAsync(culture).ConfigureAwait(true);
            if (!changed)
                _logger.LogWarning("Print Bridge shell language change was not applied. Culture={Culture}", culture);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge shell language change failed. Culture={Culture}", culture);
        }
        finally
        {
            _languageChangeInFlight = false;
        }

        // Always answer with host truth: the new language, or the unchanged one if saving failed.
        PushSnapshot(force: true);
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private ShellMessageOutcome Reject(ShellMessageRejection reason, string? raw)
    {
        _logger.LogWarning(
            "Print Bridge shell rejected a page message. Reason={Reason}, Length={Length}",
            reason,
            raw?.Length ?? 0);
        return ShellMessageOutcome.Rejected;
    }
}
