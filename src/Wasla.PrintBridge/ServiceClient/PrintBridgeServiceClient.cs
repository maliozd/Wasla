using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.ServiceClient;

/// <summary>Where the app stands with the service.</summary>
public enum PrintBridgeServiceConnectionState
{
    Connecting,
    Connected,

    /// <summary>No service answered, or the pipe's server is not the service.</summary>
    Unavailable,

    /// <summary>The service speaks another protocol version.</summary>
    Incompatible
}

/// <summary>
/// The app's engine (WAS-61): every <see cref="IPrintBridgeEngine"/> call goes to the Print Bridge service over the local
/// pipe; nothing here polls, prints or holds the device token. It connects in the background, verifies the server before
/// writing anything (<see cref="IPrintBridgeServiceVerifier"/>), allows identification only, checks the protocol version,
/// keeps the last state the service pushed, and reconnects after 0.5 s, doubling to 5 s (30 s after a version mismatch).
/// While it is not connected, <see cref="GetStatus"/> reports <see cref="PrintBridgeRuntimeIssueCode.ServiceUnavailable"/>
/// (or <see cref="PrintBridgeRuntimeIssueCode.ServiceIncompatible"/>) and every command fails with that text.
/// </summary>
public sealed class PrintBridgeServiceClient : IPrintBridgeEngine, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinRetry = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IncompatibleRetry = TimeSpan.FromSeconds(30);

    private readonly PrintBridgeServiceClientOptions _options;
    private readonly ILogger _logger;
    private readonly string _clientVersion;
    private readonly CancellationTokenSource _disposing = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private NamedPipeClientStream? _pipe;
    private PrintBridgeServiceConnectionState _state = PrintBridgeServiceConnectionState.Connecting;
    private IpcState? _serviceState;
    private ServiceSettingsView _settings = ServiceSettingsView.Empty;
    private PrintBridgeConnectionTransferConsent? _activeConsent;
    private readonly LiveSettingsView _liveSettings;
    private Task _connectLoop = Task.CompletedTask;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PrintBridgeServiceClient(PrintBridgeServiceClientOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _clientVersion = typeof(PrintBridgeServiceClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? typeof(PrintBridgeServiceClient).Assembly.GetName().Version?.ToString()
                         ?? "unknown";
        if (_clientVersion.Length > 64)
            _clientVersion = _clientVersion[..64];
        _liveSettings = new LiveSettingsView(this);
    }

    private ServiceSettingsView CurrentSettings
    {
        get { lock (_sync) return _settings; }
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<PrintBridgeAutoSetupOutcome>? BackgroundSetupCompleted;

    /// <summary>The protocol version this client announces; tests change it to check the version refusal.</summary>
    internal int ProtocolVersion { get; set; } = PrintBridgeIpcProtocol.Version;

    public PrintBridgeServiceConnectionState ConnectionState
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>Completes when the client is connected and has the service's first state.</summary>
    public Task Connected
    {
        get { lock (_sync) return _connected.Task; }
    }

    /// <summary>A live view: every read returns what the service last reported, so callers may keep the reference.</summary>
    public IPrintBridgeSettingsView Settings => _liveSettings;

    public bool IsRunning => GetStatus().IsRunning;

    public void Start()
    {
        lock (_sync)
        {
            if (!_connectLoop.IsCompleted)
                return;
            _connectLoop = Task.Run(ConnectLoopAsync);
        }
    }

    public PrintBridgeRuntimeStatus GetStatus()
    {
        PrintBridgeServiceConnectionState state;
        IpcState? serviceState;
        lock (_sync)
        {
            state = _state;
            serviceState = _serviceState;
        }

        if (state == PrintBridgeServiceConnectionState.Connected && serviceState?.Status is { } status)
            return PrintBridgeIpcMapping.FromWire(status);

        var issue = state switch
        {
            PrintBridgeServiceConnectionState.Incompatible => PrintBridgeRuntimeIssueCode.ServiceIncompatible,
            PrintBridgeServiceConnectionState.Connected when serviceState is not null =>
                serviceState.Availability switch
                {
                    IpcAvailability.EngineHeldElsewhere => PrintBridgeRuntimeIssueCode.EngineHeldElsewhere,
                    IpcAvailability.DataFolderNotSecured => PrintBridgeRuntimeIssueCode.DataFolderNotSecured,
                    _ => PrintBridgeRuntimeIssueCode.ServiceUnavailable
                },
            _ => PrintBridgeRuntimeIssueCode.ServiceUnavailable
        };
        return Unavailable(issue);
    }

    public async Task StartAsync(CancellationToken ct) =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.EngineStart, new IpcEmptyPayload(), ct).ConfigureAwait(false);

    public async Task StopAsync() =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.EngineStop, new IpcEmptyPayload(), CancellationToken.None).ConfigureAwait(false);

    public async Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct) =>
        PrintBridgeIpcMapping.FromWire(
            await SendAsync<IpcHealth>(PrintBridgeIpcProtocol.ConnectionTest, new IpcEmptyPayload(), ct).ConfigureAwait(false));

    public async Task<PrintBridgeConnectionVerification> CheckConnectionAsync(PrintBridgeConnectionCandidate candidate, CancellationToken ct)
    {
        var result = await SendAsync<IpcConnectionCheckResult>(
            PrintBridgeIpcProtocol.ConnectionCheck,
            new IpcConnectionCheckPayload(candidate.ServerUrl, candidate.KeepSavedToken ? string.Empty : candidate.Token, candidate.KeepSavedToken),
            ct).ConfigureAwait(false);
        return new PrintBridgeConnectionVerification(result.VerificationId, PrintBridgeIpcMapping.FromWire(result.Health));
    }

    public async Task<PrintBridgeConnectionChange> ApplyVerifiedConnectionAsync(
        PrintBridgeConnectionVerification verification,
        bool startListening,
        CancellationToken abandon)
    {
        // Abandoning only stops waiting: once sent, applying finishes in the service (it never half-applies).
        abandon.ThrowIfCancellationRequested();
        var result = await SendAsync<IpcConnectionApplyResult>(
            PrintBridgeIpcProtocol.ConnectionApply,
            new IpcConnectionApplyPayload(verification.VerificationId, startListening),
            CancellationToken.None).ConfigureAwait(false);
        return result.Change;
    }

    public async Task ResetConnectionForReconnectAsync() =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.ConnectionReset, new IpcEmptyPayload(), CancellationToken.None).ConfigureAwait(false);

    public async Task TestPrinterAsync(CancellationToken ct) =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.PrinterTest, new IpcEmptyPayload(), ct).ConfigureAwait(false);

    public async Task<PrintBridgeHistoryPage> QueryPrintHistoryAsync(
        PrintHistoryDateFilter filter,
        string? orderSearch,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var range = PrintBridgeIpcMapping.HistoryRanges.First(r => r.Value == filter).Key;
        var result = await SendAsync<IpcHistoryResult>(
            PrintBridgeIpcProtocol.HistoryQuery,
            new IpcHistoryQueryPayload(range, orderSearch, page, pageSize),
            ct).ConfigureAwait(false);
        return new PrintBridgeHistoryPage(result.Total, result.Page, result.Rows.Select(PrintBridgeIpcMapping.FromWire).ToArray());
    }

    public async Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct)
    {
        var result = await SendAsync<IpcReprintResult>(PrintBridgeIpcProtocol.HistoryReprint, new IpcHistoryReprintPayload(jobId), ct)
            .ConfigureAwait(false);
        return new WaslaPrintBridgeClient.ReprintJobResult(result.Success, result.MessageKey, result.NewPrintJobId);
    }

    public async Task ResolveUncertainJobAsync(Guid jobId, bool printed, CancellationToken ct) =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.HistoryResolve, new IpcHistoryResolvePayload(jobId, printed), ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListPrintersAsync(CancellationToken ct) =>
        (await SendAsync<IpcPrintersResult>(PrintBridgeIpcProtocol.PrintersList, new IpcEmptyPayload(), ct).ConfigureAwait(false)).Printers;

    public async Task<string?> SavePrinterAsync(string printerName, CancellationToken ct) =>
        (await SendAsync<IpcErrorKeyResult>(PrintBridgeIpcProtocol.PrinterSave, new IpcPrinterSavePayload(printerName), ct).ConfigureAwait(false)).ErrorKey;

    public async Task<string?> SaveOperationalSettingsAsync(PrintBridgeOperationalSettings settings, CancellationToken ct) =>
        (await SendAsync<IpcErrorKeyResult>(
            PrintBridgeIpcProtocol.SettingsSave,
            new IpcSettingsSavePayload(settings.TestMode, settings.IdlePollSeconds, settings.BusyPollSeconds, settings.ErrorPollSeconds),
            ct).ConfigureAwait(false)).ErrorKey;

    public async Task SaveLanguageAsync(string culture, CancellationToken ct) =>
        await SendAsync<IpcEmptyPayload>(PrintBridgeIpcProtocol.LanguageSave, new IpcLanguageSavePayload(culture), ct).ConfigureAwait(false);

    public async Task<PrintBridgeAutoSetupOutcome> ApplySetupLinkAsync(
        string link,
        PrintBridgeConnectionTransferConsent consent,
        CancellationToken ct)
    {
        lock (_sync)
        {
            if (_activeConsent is not null)
                throw new PrintBridgeEngineBusyException();
            _activeConsent = consent;
        }

        try
        {
            var result = await SendAsync<IpcSetupApplyResult>(PrintBridgeIpcProtocol.SetupApply, new IpcSetupApplyPayload(link), ct)
                .ConfigureAwait(false);
            return result.Outcome;
        }
        finally
        {
            lock (_sync)
                _activeConsent = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposing.IsCancellationRequested)
            await _disposing.CancelAsync().ConfigureAwait(false);

        NamedPipeClientStream? pipe;
        lock (_sync)
            pipe = _pipe;
        pipe?.Dispose();

        try
        {
            await _connectLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        FailPending();
    }

    private static PrintBridgeRuntimeStatus Unavailable(PrintBridgeRuntimeIssueCode code)
    {
        var issue = new PrintBridgeRuntimeIssue(code);
        return new PrintBridgeRuntimeStatus
        {
            IsRunning = false,
            IsConnected = false,
            LastIssue = issue,
            LastError = issue.EffectiveResourceKey,
            ServerConnectionStatus = BridgeServerConnectionStatus.Error,
            PrinterHealthStatus = PrinterHealthStatus.NotConfigured,
            TrayIconState = TrayIconState.ConnectionLost
        };
    }

    private async Task ConnectLoopAsync()
    {
        var ct = _disposing.Token;
        var delay = MinRetry;
        var lastLoggedState = (PrintBridgeServiceConnectionState?)null;
        while (!ct.IsCancellationRequested)
        {
            var outcome = await ConnectOnceAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                break;

            if (outcome != lastLoggedState)
            {
                _logger.LogInformation("Print Bridge service connection: {State}", outcome);
                lastLoggedState = outcome;
            }

            SetState(outcome);
            var wait = outcome == PrintBridgeServiceConnectionState.Incompatible ? IncompatibleRetry : delay;
            delay = outcome == PrintBridgeServiceConnectionState.Connected ? MinRetry : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetry.Ticks));
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One connection, from connect to its end. Returns the state to show afterwards.</summary>
    private async Task<PrintBridgeServiceConnectionState> ConnectOnceAsync(CancellationToken ct)
    {
        var options = PipeOptions.Asynchronous;
        if (_options.Verifier.RequiresCurrentUserOnly)
            options |= PipeOptions.CurrentUserOnly;

        // Identification only: the service may read who this is, never act as this user.
        var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, options, TokenImpersonationLevel.Identification);
        try
        {
            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return PrintBridgeServiceConnectionState.Unavailable;
            }

            if (!_options.Verifier.IsTrusted(pipe, out var reason))
            {
                _logger.LogWarning("The Print Bridge pipe was not used: {Reason}.", reason);
                return PrintBridgeServiceConnectionState.Unavailable;
            }

            await WriteAsync(pipe, new IpcHello(PrintBridgeIpcProtocol.Hello, ProtocolVersion, _clientVersion), ct).ConfigureAwait(false);

            string? first;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloTimeout.CancelAfter(PrintBridgeIpcProtocol.HelloTimeout);
                first = await PrintBridgeIpcProtocol.ReadFrameAsync(pipe, PrintBridgeIpcProtocol.MaxServiceFrameBytes, helloTimeout.Token)
                    .ConfigureAwait(false);
            }

            if (first is null)
                return PrintBridgeServiceConnectionState.Unavailable;

            using (var document = PrintBridgeIpcProtocol.ParseStrict(first))
            {
                var type = document.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == PrintBridgeIpcProtocol.Error)
                {
                    var error = PrintBridgeIpcProtocol.Deserialize<IpcHandshakeError>(document.RootElement);
                    if (error.Code == PrintBridgeIpcProtocol.ErrorIncompatibleProtocol)
                    {
                        _logger.LogWarning(
                            "The Print Bridge service speaks protocol {Min}–{Max}; this app speaks {Version}.",
                            error.Min,
                            error.Max,
                            ProtocolVersion);
                        return PrintBridgeServiceConnectionState.Incompatible;
                    }

                    return PrintBridgeServiceConnectionState.Unavailable;
                }

                var welcome = PrintBridgeIpcProtocol.Deserialize<IpcWelcome>(document.RootElement);
                if (welcome.Type != PrintBridgeIpcProtocol.Welcome || welcome.Protocol != ProtocolVersion)
                    return PrintBridgeServiceConnectionState.Incompatible;
            }

            lock (_sync)
                _pipe = pipe;

            await ReadLoopAsync(pipe, ct).ConfigureAwait(false);
            return PrintBridgeServiceConnectionState.Unavailable;
        }
        catch (PrintBridgeIpcProtocolException ex)
        {
            _logger.LogWarning("The Print Bridge service broke the protocol ({Code}); reconnecting.", ex.Code);
            return PrintBridgeServiceConnectionState.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            return PrintBridgeServiceConnectionState.Unavailable;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pipe, pipe))
                    _pipe = null;
            }

            pipe.Dispose();
            FailPending();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var text = await PrintBridgeIpcProtocol.ReadFrameAsync(pipe, PrintBridgeIpcProtocol.MaxServiceFrameBytes, ct).ConfigureAwait(false);
            if (text is null)
                return;

            using var document = PrintBridgeIpcProtocol.ParseStrict(text);
            var type = document.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case PrintBridgeIpcProtocol.Response:
                {
                    var response = PrintBridgeIpcProtocol.Deserialize<IpcResponse>(document.RootElement);
                    response = response with { Payload = response.Payload?.Clone() };
                    if (_pending.TryRemove(response.Id, out var waiter))
                        waiter.TrySetResult(response);
                    break;
                }

                case PrintBridgeIpcProtocol.Event:
                    HandleEvent(PrintBridgeIpcProtocol.Deserialize<IpcEvent>(document.RootElement));
                    break;

                default:
                    throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorMalformed);
            }
        }
    }

    private void HandleEvent(IpcEvent message)
    {
        switch (message.Event)
        {
            case PrintBridgeIpcProtocol.StateEvent:
            {
                var state = PrintBridgeIpcProtocol.Deserialize<IpcState>(message.Payload);
                TaskCompletionSource? connected;
                lock (_sync)
                {
                    _serviceState = state;
                    _settings = state.Settings is null ? ServiceSettingsView.Empty : new ServiceSettingsView(state.Settings);
                    _state = PrintBridgeServiceConnectionState.Connected;
                    connected = _connected;
                }

                connected.TrySetResult();
                StatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            }

            case PrintBridgeIpcProtocol.SetupConsentEvent:
            {
                var consent = PrintBridgeIpcProtocol.Deserialize<IpcSetupConsentEvent>(message.Payload);
                _ = AnswerConsentAsync(consent);
                break;
            }

            case PrintBridgeIpcProtocol.SetupBackgroundEvent:
            {
                var background = PrintBridgeIpcProtocol.Deserialize<IpcSetupBackgroundEvent>(message.Payload);
                BackgroundSetupCompleted?.Invoke(this, background.Outcome);
                break;
            }

            default:
                // An event this version does not know is ignored; a newer service would also have refused this protocol.
                break;
        }
    }

    /// <summary>Asks the user through the setup link's own confirmation; anything but an explicit approval is a refusal.</summary>
    private async Task AnswerConsentAsync(IpcSetupConsentEvent consent)
    {
        PrintBridgeConnectionTransferConsent? ask;
        lock (_sync)
            ask = _activeConsent;

        var approved = false;
        if (ask is not null)
        {
            try
            {
                approved = await ask(
                    new PrintBridgeConnectionTransferConsentRequest(
                        consent.CurrentTenantName,
                        consent.CurrentServer,
                        consent.TargetTenantName,
                        consent.TargetServer),
                    _disposing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "The connection change confirmation failed; it counts as a refusal.");
            }
        }

        try
        {
            await SendAsync<IpcEmptyPayload>(
                PrintBridgeIpcProtocol.SetupConsentAnswer,
                new IpcSetupConsentAnswerPayload(consent.ConsentId, approved),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The service treats a lost answer as a refusal.
            _logger.LogInformation("The connection change answer did not reach the service: {Error}", ex.GetType().Name);
        }
    }

    private async Task<TResult> SendAsync<TResult>(string command, object payload, CancellationToken ct)
    {
        NamedPipeClientStream? pipe;
        lock (_sync)
            pipe = _state == PrintBridgeServiceConnectionState.Connected ? _pipe : null;

        if (pipe is null)
            throw ServiceUnavailable();

        var id = Guid.NewGuid().ToString("D");
        var waiter = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            var request = new IpcRequest(
                PrintBridgeIpcProtocol.Request,
                id,
                command,
                JsonSerializer.SerializeToElement(payload, payload.GetType(), PrintBridgeIpcProtocol.JsonOptions));
            try
            {
                await WriteAsync(pipe, request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw ServiceUnavailable();
            }

            var response = await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            if (!response.Ok)
                throw ToException(response.Error);

            return response.Payload is { } element
                ? PrintBridgeIpcProtocol.Deserialize<TResult>(element)
                : throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorMalformed);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync<T>(NamedPipeClientStream pipe, T message, CancellationToken ct)
    {
        var bytes = PrintBridgeIpcProtocol.Serialize(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrintBridgeIpcProtocol.WriteFrameAsync(pipe, bytes, PrintBridgeIpcProtocol.MaxClientFrameBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void SetState(PrintBridgeServiceConnectionState state)
    {
        bool changed;
        lock (_sync)
        {
            changed = _state != state;
            _state = state;
            if (state != PrintBridgeServiceConnectionState.Connected)
            {
                _serviceState = null;
                _settings = ServiceSettingsView.Empty;
                if (_connected.Task.IsCompleted)
                    _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (changed)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FailPending()
    {
        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                waiter.TrySetException(ServiceUnavailable());
        }
    }

    private static Exception ServiceUnavailable() =>
        new LocalizedApplicationException(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServiceUnavailable).EffectiveDetailResourceKey);

    /// <summary>Only what the app can show safely: resource keys and their arguments, never service text.</summary>
    private static Exception ToException(IpcError? error)
    {
        if (error is null)
            return new PrintBridgeServiceException(PrintBridgeIpcProtocol.ErrorFailed);

        var args = (error.Args ?? []).Take(PrintBridgeIpcMapping.MaxArgs).Cast<object>().ToArray();
        return error.Code switch
        {
            PrintBridgeIpcProtocol.ErrorLocalized when !string.IsNullOrWhiteSpace(error.Key) =>
                new LocalizedApplicationException(error.Key, args),
            PrintBridgeIpcProtocol.ErrorConnection => new PrintBridgeConnectionException(
                error.Key ?? "Connection.RequestFailed",
                string.Empty,
                string.Empty,
                args,
                error.Issue ?? PrintBridgeRuntimeIssueCode.RequestFailed,
                error.Status),
            PrintBridgeIpcProtocol.ErrorNotActive =>
                new LocalizedApplicationException(new PrintBridgeRuntimeIssue(error.Issue ?? PrintBridgeRuntimeIssueCode.ServiceUnavailable).EffectiveDetailResourceKey),
            PrintBridgeIpcProtocol.ErrorBusy => new PrintBridgeEngineBusyException(),
            PrintBridgeIpcProtocol.ErrorCancelled => new OperationCanceledException(),
            _ => new PrintBridgeServiceException(error.Code)
        };
    }

    private sealed class LiveSettingsView(PrintBridgeServiceClient client) : IPrintBridgeSettingsView
    {
        public string ServerUrl => client.CurrentSettings.ServerUrl;

        public bool HasSavedToken => client.CurrentSettings.HasSavedToken;

        public PrintBridgeOptions Bridge => client.CurrentSettings.Bridge.Clone();

        public UiOptions Ui => client.CurrentSettings.Ui.Clone();

        public bool IsPrinterUsable => client.CurrentSettings.IsPrinterUsable;
    }

    /// <summary>The saved settings as the service last reported them; never the token.</summary>
    private sealed class ServiceSettingsView : IPrintBridgeSettingsView
    {
        public static readonly ServiceSettingsView Empty = new(null);

        public ServiceSettingsView(IpcSettings? settings)
        {
            ServerUrl = settings?.ServerUrl ?? string.Empty;
            HasSavedToken = settings?.HasSavedToken ?? false;
            IsPrinterUsable = settings?.PrinterUsable ?? false;
            Bridge = new PrintBridgeOptions
            {
                PrinterMode = settings?.PrinterMode ?? "WindowsPrinter",
                PrinterName = settings?.PrinterName ?? string.Empty,
                DryRun = settings?.DryRun ?? false,
                IdlePollIntervalSeconds = settings?.IdlePollSeconds ?? 5,
                BusyPollIntervalSeconds = settings?.BusyPollSeconds ?? 1,
                ErrorPollIntervalSeconds = settings?.ErrorPollSeconds ?? 15,
                MaxJobsPerPoll = settings?.MaxJobsPerPoll ?? 3,
                DisplayName = settings?.DisplayName ?? string.Empty,
                ServerDeviceNameResolved = settings?.ServerDeviceNameResolved ?? false,
                PrintingStoppedByUser = settings?.PrintingStoppedByUser ?? false
            };
            Ui = new UiOptions { Language = settings?.Language ?? string.Empty };
        }

        public string ServerUrl { get; }

        public bool HasSavedToken { get; }

        public PrintBridgeOptions Bridge { get; }

        public UiOptions Ui { get; }

        public bool IsPrinterUsable { get; }
    }
}

/// <summary>The service answered with an error the app shows only as a generic failure.</summary>
public sealed class PrintBridgeServiceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
