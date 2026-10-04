using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Localization;
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

    void OpenClassicWindow();
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
/// Connects the status page to the running engine. It reads engine state through
/// <see cref="IPrintBridgeStatusSource"/> only, so it cannot start, stop or duplicate the polling loop,
/// claim jobs or print. Page commands are limited to <see cref="ShellCommandType"/>.
/// </summary>
public sealed class ShellBridge : IDisposable
{
    private readonly IPrintBridgeStatusSource _statusSource;
    private readonly ShellSnapshotFactory _snapshotFactory;
    private readonly IShellHost _host;
    private readonly IShellLanguageSwitcher _languageSwitcher;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ILogger _logger;

    private int _pushQueued;
    private bool _pageReady;
    private bool _languageChangeInFlight;
    private bool _disposed;
    private long _sequence;
    private string? _lastPayloadJson;

    public ShellBridge(
        IPrintBridgeStatusSource statusSource,
        ShellSnapshotFactory snapshotFactory,
        IShellHost host,
        IShellLanguageSwitcher languageSwitcher,
        PrintBridgeCultureService cultureService,
        ILogger logger)
    {
        _statusSource = statusSource;
        _snapshotFactory = snapshotFactory;
        _host = host;
        _languageSwitcher = languageSwitcher;
        _cultureService = cultureService;
        _logger = logger;

        _statusSource.StatusChanged += OnEngineStateChanged;
        _cultureService.CultureChanged += OnEngineStateChanged;
    }

    /// <summary>Number of snapshots sent to the page so far.</summary>
    public long SentSnapshotCount => Interlocked.Read(ref _sequence);

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
                break;

            case ShellCommandType.LanguageChange:
                _ = ChangeLanguageAsync(command.Culture!);
                break;

            case ShellCommandType.ClassicWindowOpen:
                _host.OpenClassicWindow();
                break;
        }

        return ShellMessageOutcome.Accepted;
    }

    /// <summary>Periodic refresh so time-based state (for example a contact that has gone stale) reaches the page.</summary>
    public void Refresh() => PushSnapshot(force: false);

    /// <summary>Resends the full state, for example when a hidden window is shown again.</summary>
    public void Resend() => PushSnapshot(force: true);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _statusSource.StatusChanged -= OnEngineStateChanged;
        _cultureService.CultureChanged -= OnEngineStateChanged;
    }

    private void OnEngineStateChanged(object? sender, EventArgs e)
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
        var sequence = Interlocked.Increment(ref _sequence);
        _host.PostWebMessageAsJson(ShellMessageSerializer.SerializeSnapshotMessage(snapshot, sequence));
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

    private ShellMessageOutcome Reject(ShellMessageRejection reason, string? raw)
    {
        _logger.LogWarning(
            "Print Bridge shell rejected a page message. Reason={Reason}, Length={Length}",
            reason,
            raw?.Length ?? 0);
        return ShellMessageOutcome.Rejected;
    }
}
