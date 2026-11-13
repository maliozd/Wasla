using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Ipc;

/// <summary>
/// One app connected to the service (WAS-61). The order is fixed: read the hello (bounded in size and time), identify and
/// authorize the client, check the protocol version, answer, then serve requests concurrently (at most
/// <see cref="PrintBridgeIpcProtocol.MaxInFlightRequestsPerClient"/>) and push state. A frame that breaks the framing ends
/// the connection; a request that is well framed but malformed is answered <c>malformed</c>. Payloads are never logged.
/// </summary>
internal sealed class PrintBridgeIpcConnection
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StateCoalesce = TimeSpan.FromMilliseconds(250);

    private static readonly IReadOnlySet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        PrintBridgeIpcProtocol.StateGet,
        PrintBridgeIpcProtocol.PrintersList,
        PrintBridgeIpcProtocol.HistoryQuery,
        PrintBridgeIpcProtocol.ConnectionCheck,
        PrintBridgeIpcProtocol.SetupConsentAnswer
    };

    private readonly NamedPipeServerStream _pipe;
    private readonly PrintBridgeServiceBackend _backend;
    private readonly IPrintBridgeIpcClientAuthorizer _authorizer;
    private readonly ILogger _logger;
    private readonly string _serviceVersion;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _consents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PrintBridgeConnectionVerification> _verifications = new(StringComparer.Ordinal);
    private CancellationTokenSource _closing = new();
    private PrintBridgeIpcClientIdentity? _client;
    private int _inFlight;
    private int _statePending;

    public PrintBridgeIpcConnection(
        NamedPipeServerStream pipe,
        PrintBridgeServiceBackend backend,
        IPrintBridgeIpcClientAuthorizer authorizer,
        ILogger<PrintBridgeIpcConnection> logger,
        string serviceVersion)
    {
        _pipe = pipe;
        _backend = backend;
        _authorizer = authorizer;
        _logger = logger;
        _serviceVersion = serviceVersion;
    }

    public async Task RunAsync(CancellationToken serverStopping)
    {
        _closing = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        var ct = _closing.Token;
        try
        {
            string? helloText;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloTimeout.CancelAfter(PrintBridgeIpcProtocol.HelloTimeout);
                try
                {
                    helloText = await PrintBridgeIpcProtocol.ReadFrameAsync(_pipe, PrintBridgeIpcProtocol.MaxClientFrameBytes, helloTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("A Print Bridge pipe client sent no hello in time; disconnected.");
                    return;
                }
            }

            if (helloText is null)
                return;

            // Windows lets the server identify the client only after it read from the pipe: the hello. Nothing in it is
            // acted on before the client is authorized.
            _client = _authorizer.Authorize(_pipe, out var refusal);
            if (_client is null)
            {
                _logger.LogWarning("A Print Bridge pipe client was refused: {Reason}.", refusal ?? "not authorized");
                return;
            }

            if (!await HandshakeAsync(helloText, ct).ConfigureAwait(false))
                return;

            _logger.LogInformation(
                "Print Bridge app connected. User={UserSid}, Session={SessionId}, Process={ProcessId}",
                _client.User.Value,
                _client.SessionId,
                _client.ProcessId);

            _backend.Changed += OnBackendChanged;
            _backend.BackgroundSetupCompleted += OnBackgroundSetupCompleted;
            await SendAsync(new IpcEvent(PrintBridgeIpcProtocol.Event, PrintBridgeIpcProtocol.StateEvent, ToElement(_backend.Snapshot())), ct)
                .ConfigureAwait(false);

            await ReadRequestsAsync(ct).ConfigureAwait(false);
        }
        catch (PrintBridgeIpcProtocolException ex)
        {
            _logger.LogWarning("A Print Bridge pipe client broke the protocol ({Code}); disconnected.", ex.Code);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the service is stopping.
        }
        finally
        {
            _backend.Changed -= OnBackendChanged;
            _backend.BackgroundSetupCompleted -= OnBackgroundSetupCompleted;
            await _closing.CancelAsync().ConfigureAwait(false);
            // A move confirmation still open in this app is a refusal (WAS-59: closing the app abandons it).
            foreach (var consent in _consents.Values)
                consent.TrySetResult(false);
            if (_client is not null)
                _logger.LogInformation("Print Bridge app disconnected. User={UserSid}, Session={SessionId}", _client.User.Value, _client.SessionId);
        }
    }

    private async Task<bool> HandshakeAsync(string helloText, CancellationToken ct)
    {
        IpcHello hello;
        using (var document = PrintBridgeIpcProtocol.ParseStrict(helloText))
            hello = PrintBridgeIpcProtocol.Deserialize<IpcHello>(document.RootElement);

        if (hello.Type != PrintBridgeIpcProtocol.Hello || hello.Client is null or { Length: > 64 })
            throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorMalformed);

        if (hello.Protocol is < PrintBridgeIpcProtocol.MinSupportedVersion or > PrintBridgeIpcProtocol.MaxSupportedVersion)
        {
            _logger.LogWarning("A Print Bridge app with protocol version {Version} was refused; this service speaks {Min}–{Max}.",
                hello.Protocol, PrintBridgeIpcProtocol.MinSupportedVersion, PrintBridgeIpcProtocol.MaxSupportedVersion);
            await SendAsync(
                new IpcHandshakeError(
                    PrintBridgeIpcProtocol.Error,
                    PrintBridgeIpcProtocol.ErrorIncompatibleProtocol,
                    PrintBridgeIpcProtocol.MinSupportedVersion,
                    PrintBridgeIpcProtocol.MaxSupportedVersion),
                ct).ConfigureAwait(false);
            return false;
        }

        await SendAsync(new IpcWelcome(PrintBridgeIpcProtocol.Welcome, PrintBridgeIpcProtocol.Version, _serviceVersion), ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task ReadRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var text = await PrintBridgeIpcProtocol.ReadFrameAsync(_pipe, PrintBridgeIpcProtocol.MaxClientFrameBytes, ct)
                .ConfigureAwait(false);
            if (text is null)
                return;

            IpcRequest request;
            using (var document = PrintBridgeIpcProtocol.ParseStrict(text))
            {
                request = PrintBridgeIpcProtocol.Deserialize<IpcRequest>(document.RootElement);
                // An envelope without a usable id cannot be answered: the client broke the protocol.
                if (request.Type != PrintBridgeIpcProtocol.Request
                    || request.Id is null
                    || !PrintBridgeIpcProtocol.RequestIdPattern().IsMatch(request.Id))
                {
                    throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorMalformed);
                }

                request = request with { Payload = request.Payload.ValueKind == JsonValueKind.Undefined ? default : request.Payload.Clone() };
            }

            if (request.Command is null || !PrintBridgeIpcProtocol.Commands.Contains(request.Command))
            {
                await RespondErrorAsync(request.Id, new IpcError(PrintBridgeIpcProtocol.ErrorUnknownCommand, null, null, null, null), ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (request.Payload.ValueKind != JsonValueKind.Object)
            {
                await RespondErrorAsync(request.Id, Malformed(), ct).ConfigureAwait(false);
                continue;
            }

            if (Interlocked.Increment(ref _inFlight) > PrintBridgeIpcProtocol.MaxInFlightRequestsPerClient)
            {
                Interlocked.Decrement(ref _inFlight);
                await RespondErrorAsync(request.Id, new IpcError(PrintBridgeIpcProtocol.ErrorBusy, null, null, null, null), ct)
                    .ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleAsync(request, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            object result;
            try
            {
                result = await DispatchAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var error = MapError(ex);
                LogOutcome(request.Command, error.Code);
                if (!ReadOnlyCommands.Contains(request.Command))
                    await SendStateAsync(ct).ConfigureAwait(false);
                await RespondErrorAsync(request.Id, error, ct).ConfigureAwait(false);
                return;
            }

            LogOutcome(request.Command, "ok");
            // Whatever changed is visible to the app before the answer, so a caller that reads the state next sees it.
            if (!ReadOnlyCommands.Contains(request.Command))
                await SendStateAsync(ct).ConfigureAwait(false);
            await SendAsync(new IpcResponse(PrintBridgeIpcProtocol.Response, request.Id, true, ToElement(result), null), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away; the command itself has finished in the service.
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<object> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Command == PrintBridgeIpcProtocol.StateGet)
        {
            Payload<IpcEmptyPayload>(request);
            return _backend.Snapshot();
        }

        if (request.Command == PrintBridgeIpcProtocol.SetupConsentAnswer)
        {
            var answer = Payload<IpcSetupConsentAnswerPayload>(request);
            // Only the app that was asked can answer: consent ids live in this connection.
            if (!_consents.TryRemove(answer.ConsentId, out var consent))
                throw new ConsentNotFoundException();
            consent.TrySetResult(answer.Approved);
            return new IpcEmptyPayload();
        }

        var engine = _backend.Engine ?? throw new NotActiveException(PrintBridgeServiceBackend.IssueFor(_backend.Availability));

        switch (request.Command)
        {
            case PrintBridgeIpcProtocol.EngineStart:
                Payload<IpcEmptyPayload>(request);
                await engine.StartAsync(ct).ConfigureAwait(false);
                return new IpcEmptyPayload();

            case PrintBridgeIpcProtocol.EngineStop:
                Payload<IpcEmptyPayload>(request);
                await engine.StopAsync().ConfigureAwait(false);
                return new IpcEmptyPayload();

            case PrintBridgeIpcProtocol.ConnectionTest:
                Payload<IpcEmptyPayload>(request);
                return PrintBridgeIpcMapping.ToWire(await engine.TestConnectionAsync(ct).ConfigureAwait(false));

            case PrintBridgeIpcProtocol.ConnectionCheck:
            {
                var check = Payload<IpcConnectionCheckPayload>(request);
                var verification = await engine.CheckConnectionAsync(
                    new PrintBridgeConnectionCandidate(check.ServerUrl, check.Token, check.KeepSavedToken),
                    ct).ConfigureAwait(false);
                // Bound to this client: another app cannot apply a candidate it did not check.
                _verifications[verification.VerificationId] = verification;
                return new IpcConnectionCheckResult(verification.VerificationId, PrintBridgeIpcMapping.ToWire(verification.Health));
            }

            case PrintBridgeIpcProtocol.ConnectionApply:
            {
                var apply = Payload<IpcConnectionApplyPayload>(request);
                if (!_verifications.TryRemove(apply.VerificationId, out var verification))
                    throw new LocalizedApplicationException("Shell.Setup.CheckExpired");
                // Once applying began it finishes in the service, even if this app closes meanwhile.
                var change = await engine.ApplyVerifiedConnectionAsync(verification, apply.StartListening, CancellationToken.None)
                    .ConfigureAwait(false);
                return new IpcConnectionApplyResult(change);
            }

            case PrintBridgeIpcProtocol.ConnectionReset:
                Payload<IpcEmptyPayload>(request);
                await engine.ResetConnectionForReconnectAsync().ConfigureAwait(false);
                return new IpcEmptyPayload();

            case PrintBridgeIpcProtocol.PrinterTest:
                Payload<IpcEmptyPayload>(request);
                await engine.TestPrinterAsync(ct).ConfigureAwait(false);
                return new IpcEmptyPayload();

            case PrintBridgeIpcProtocol.PrintersList:
                Payload<IpcEmptyPayload>(request);
                return new IpcPrintersResult([.. await engine.ListPrintersAsync(ct).ConfigureAwait(false)]);

            case PrintBridgeIpcProtocol.PrinterSave:
            {
                var printer = Payload<IpcPrinterSavePayload>(request);
                return new IpcErrorKeyResult(await engine.SavePrinterAsync(printer.Name, ct).ConfigureAwait(false));
            }

            case PrintBridgeIpcProtocol.SettingsSave:
            {
                var settings = Payload<IpcSettingsSavePayload>(request);
                var errorKey = await engine.SaveOperationalSettingsAsync(
                    new PrintBridgeOperationalSettings(settings.TestMode, settings.IdlePollSeconds, settings.BusyPollSeconds, settings.ErrorPollSeconds),
                    ct).ConfigureAwait(false);
                return new IpcErrorKeyResult(errorKey);
            }

            case PrintBridgeIpcProtocol.LanguageSave:
            {
                var language = Payload<IpcLanguageSavePayload>(request);
                await engine.SaveLanguageAsync(language.Culture, ct).ConfigureAwait(false);
                return new IpcEmptyPayload();
            }

            case PrintBridgeIpcProtocol.HistoryQuery:
            {
                var query = Payload<IpcHistoryQueryPayload>(request);
                var page = await engine.QueryPrintHistoryAsync(
                    PrintBridgeIpcMapping.HistoryRanges[query.Range],
                    query.Search,
                    query.Page,
                    query.PageSize,
                    ct).ConfigureAwait(false);
                return new IpcHistoryResult(page.Total, page.Page, page.Rows.Select(PrintBridgeIpcMapping.ToWire).ToArray());
            }

            case PrintBridgeIpcProtocol.HistoryReprint:
            {
                var reprint = Payload<IpcHistoryReprintPayload>(request);
                var result = await engine.ReprintJobAsync(reprint.JobId, ct).ConfigureAwait(false);
                return new IpcReprintResult(result.Success, result.MessageKey ?? string.Empty, result.NewPrintJobId);
            }

            case PrintBridgeIpcProtocol.HistoryResolve:
            {
                var resolve = Payload<IpcHistoryResolvePayload>(request);
                await engine.ResolveUncertainJobAsync(resolve.JobId, resolve.Printed, ct).ConfigureAwait(false);
                return new IpcEmptyPayload();
            }

            case PrintBridgeIpcProtocol.SetupApply:
            {
                var setup = Payload<IpcSetupApplyPayload>(request);
                var outcome = await engine.ApplySetupLinkAsync(setup.Link, AskThisClientAsync, ct).ConfigureAwait(false);
                return new IpcSetupApplyResult(outcome);
            }

            default:
                throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorUnknownCommand);
        }
    }

    /// <summary>
    /// The move confirmation (WAS-91) is asked of this app only, in its user's session. Closing the app, a lost connection
    /// or anything but an explicit approval is a refusal.
    /// </summary>
    private async Task<bool> AskThisClientAsync(PrintBridgeConnectionTransferConsentRequest request, CancellationToken ct)
    {
        var consentId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _consents[consentId] = answer;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
            using var cancel = linked.Token.Register(() => answer.TrySetResult(false));
            await SendAsync(
                new IpcEvent(
                    PrintBridgeIpcProtocol.Event,
                    PrintBridgeIpcProtocol.SetupConsentEvent,
                    ToElement(new IpcSetupConsentEvent(
                        consentId,
                        request.CurrentTenantName,
                        request.CurrentServer,
                        request.TargetTenantName,
                        request.TargetServer))),
                linked.Token).ConfigureAwait(false);
            return await answer.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _consents.TryRemove(consentId, out _);
        }
    }

    private static T Payload<T>(IpcRequest request) =>
        PrintBridgeIpcMapping.Validate(PrintBridgeIpcProtocol.Deserialize<T>(request.Payload));

    private IpcError MapError(Exception ex) => ex switch
    {
        PrintBridgeIpcProtocolException protocol when protocol.Code == PrintBridgeIpcProtocol.ErrorUnknownCommand =>
            new IpcError(PrintBridgeIpcProtocol.ErrorUnknownCommand, null, null, null, null),
        PrintBridgeIpcProtocolException => Malformed(),
        NotActiveException notActive => new IpcError(PrintBridgeIpcProtocol.ErrorNotActive, null, null, notActive.Issue, null),
        ConsentNotFoundException => new IpcError(PrintBridgeIpcProtocol.ErrorConsentNotFound, null, null, null, null),
        PrintBridgeEngineBusyException => new IpcError(PrintBridgeIpcProtocol.ErrorBusy, null, null, null, null),
        PrintBridgeConnectionException connection => new IpcError(
            PrintBridgeIpcProtocol.ErrorConnection,
            connection.UserMessageKey,
            PrintBridgeIpcMapping.Args(connection.FormatArgs),
            connection.IssueCode,
            connection.StatusCode),
        LocalizedApplicationException localized => new IpcError(
            PrintBridgeIpcProtocol.ErrorLocalized,
            localized.ResourceKey,
            PrintBridgeIpcMapping.Args(localized.Args),
            null,
            null),
        OperationCanceledException => new IpcError(PrintBridgeIpcProtocol.ErrorCancelled, null, null, null, null),
        _ => LogUnexpected(ex)
    };

    private IpcError LogUnexpected(Exception ex)
    {
        _logger.LogWarning(ex, "A Print Bridge pipe command failed unexpectedly.");
        return new IpcError(PrintBridgeIpcProtocol.ErrorFailed, null, null, null, null);
    }

    private static IpcError Malformed() => new(PrintBridgeIpcProtocol.ErrorMalformed, null, null, null, null);

    private void LogOutcome(string command, string outcome)
    {
        if (ReadOnlyCommands.Contains(command))
            _logger.LogDebug("Pipe command {Command}: {Outcome}", command, outcome);
        else
            _logger.LogInformation("Pipe command {Command} from session {SessionId}: {Outcome}", command, _client?.SessionId, outcome);
    }

    private Task RespondErrorAsync(string id, IpcError error, CancellationToken ct) =>
        SendAsync(new IpcResponse(PrintBridgeIpcProtocol.Response, id, false, null, error), ct);

    private Task SendStateAsync(CancellationToken ct) =>
        SendAsync(new IpcEvent(PrintBridgeIpcProtocol.Event, PrintBridgeIpcProtocol.StateEvent, ToElement(_backend.Snapshot())), ct);

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        // Coalesced: at most one state event per quarter second, carrying the newest state.
        if (Interlocked.Exchange(ref _statePending, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StateCoalesce, _closing.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _statePending, 0);
                await SendStateAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        });
    }

    private void OnBackgroundSetupCompleted(object? sender, PrintBridgeAutoSetupOutcome outcome)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(
                    new IpcEvent(PrintBridgeIpcProtocol.Event, PrintBridgeIpcProtocol.SetupBackgroundEvent, ToElement(new IpcSetupBackgroundEvent(outcome))),
                    _closing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        });
    }

    private async Task SendAsync<T>(T message, CancellationToken ct)
    {
        var bytes = PrintBridgeIpcProtocol.Serialize(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WriteTimeout);
            try
            {
                await PrintBridgeIpcProtocol.WriteFrameAsync(_pipe, bytes, PrintBridgeIpcProtocol.MaxServiceFrameBytes, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A client that does not read cannot hold the service: it is disconnected.
                await _closing.CancelAsync().ConfigureAwait(false);
                _pipe.Dispose();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, PrintBridgeIpcProtocol.JsonOptions);

    private sealed class NotActiveException(Models.PrintBridgeRuntimeIssueCode issue) : Exception("The engine is not active.")
    {
        public Models.PrintBridgeRuntimeIssueCode Issue { get; } = issue;
    }

    private sealed class ConsentNotFoundException() : Exception("No such confirmation for this client.");
}
