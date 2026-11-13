using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61: the local pipe between the app and the service, over a real named pipe in this process. The server is the
/// production <see cref="PrintBridgeIpcServer"/> with the production client rule; behind it is a fake engine, so these
/// tests are about the transport: authorization, framing, validation, versioning, reconnection, consent routing and
/// secrets. The engine behind a real pipe is covered by the tray tests; another process and the Windows Service by
/// <see cref="PrintBridgeServiceProcessTests"/> and <see cref="PrintBridgeWindowsServiceTests"/>.
/// </summary>
public sealed class PrintBridgeIpcTests : IAsyncDisposable
{
    private const string SecretToken = "pb_SECRET-ipc-token-not-a-real-credential";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _pipeName = "WaslaPbTest." + Guid.NewGuid().ToString("N");
    private readonly PrintBridgeServiceBackend _backend = new();
    private readonly FakeStatusSource _engine = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private PrintBridgeIpcServer? _server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task TheHandshake_AnswersWelcome_ThenTheState()
    {
        await StartServerAsync();
        using var raw = await RawClient.ConnectAsync(_pipeName);

        await raw.SendAsync(new { type = "hello", protocol = PrintBridgeIpcProtocol.Version, client = "test" });

        var welcome = await raw.ReceiveAsync();
        Assert.Equal("welcome", welcome.GetProperty("type").GetString());
        Assert.Equal(PrintBridgeIpcProtocol.Version, welcome.GetProperty("protocol").GetInt32());
        var state = await raw.ReceiveAsync();
        Assert.Equal("event", state.GetProperty("type").GetString());
        Assert.Equal("state", state.GetProperty("event").GetString());
        Assert.Equal("active", state.GetProperty("payload").GetProperty("availability").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task AnotherProtocolVersion_IsRefused_WithTheSupportedRange_AndTheConnectionCloses(int protocol)
    {
        await StartServerAsync();
        using var raw = await RawClient.ConnectAsync(_pipeName);

        await raw.SendAsync(new { type = "hello", protocol, client = "test" });

        var refusal = await raw.ReceiveAsync();
        Assert.Equal("error", refusal.GetProperty("type").GetString());
        Assert.Equal("incompatible_protocol", refusal.GetProperty("code").GetString());
        Assert.Equal(PrintBridgeIpcProtocol.MinSupportedVersion, refusal.GetProperty("min").GetInt32());
        Assert.Equal(PrintBridgeIpcProtocol.MaxSupportedVersion, refusal.GetProperty("max").GetInt32());
        Assert.True(await raw.IsClosedAsync());
    }

    [Fact]
    public async Task AnAppOfAnotherProtocolVersion_ShowsIncompatible_AndSendsNothing()
    {
        await StartServerAsync();
        var client = CreateClient(protocol: PrintBridgeIpcProtocol.Version + 1);

        await WaitUntilAsync(() => client.ConnectionState == PrintBridgeServiceConnectionState.Incompatible);

        Assert.Equal(PrintBridgeRuntimeIssueCode.ServiceIncompatible, client.GetStatus().LastIssue?.Code);
        Assert.False(client.IsRunning);
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => client.StartAsync(Ct));
        Assert.Equal("RuntimeIssue.ServiceUnavailable.Detail", refused.ResourceKey);
        Assert.Equal(0, _engine.StartCalls);
    }

    [Fact]
    public async Task AClientThatSendsNoHello_IsDisconnected()
    {
        await StartServerAsync();
        using var raw = await RawClient.ConnectAsync(_pipeName);

        Assert.True(await raw.IsClosedAsync(PrintBridgeIpcProtocol.HelloTimeout + TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PrintBridgeIpcProtocol.MaxClientFrameBytes + 1)]
    public async Task AFrameOfTheWrongSize_EndsTheConnection_AndTheServerServesTheNextClient(int length)
    {
        await StartServerAsync();
        using (var raw = await RawClient.ConnectAsync(_pipeName))
        {
            await raw.SendHeaderAsync(length);
            Assert.True(await raw.IsClosedAsync());
        }

        var client = CreateClient();
        await client.Connected.WaitAsync(Wait, Ct);
        Assert.True(client.IsRunning);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"type":"hello","protocol":1,"client":"a","client":"b"}""")]
    [InlineData("""{"type":"hello","protocol":1,"client":"test","extra":true}""")]
    [InlineData("""{"type":"request","id":"abcdefgh-1","command":"state.get","payload":{}}""")]
    public async Task AMalformedHello_EndsTheConnection(string hello)
    {
        await StartServerAsync();
        using var raw = await RawClient.ConnectAsync(_pipeName);

        await raw.SendTextAsync(hello);

        Assert.True(await raw.IsClosedAsync());
    }

    [Fact]
    public async Task NotUtf8_EndsTheConnection()
    {
        await StartServerAsync();
        using var raw = await RawClient.ConnectAsync(_pipeName);

        await raw.SendBytesAsync([0xC3, 0x28, 0xFF]);

        Assert.True(await raw.IsClosedAsync());
    }

    [Theory]
    [InlineData("engine.fly", "{}", "unknown_command")]
    [InlineData("engine.start", """{"now":true}""", "malformed")]
    [InlineData("printer.save", """{"name":""}""", "malformed")]
    [InlineData("printer.save", """{"name":" POS-58"}""", "malformed")]
    [InlineData("printer.save", """{"name":"POS\n58"}""", "malformed")]
    [InlineData("settings.save", """{"testMode":false,"idlePollSeconds":-1,"busyPollSeconds":1,"errorPollSeconds":1}""", "malformed")]
    [InlineData("settings.save", """{"testMode":"no","idlePollSeconds":5,"busyPollSeconds":1,"errorPollSeconds":1}""", "malformed")]
    [InlineData("language.save", """{"culture":"de-DE"}""", "malformed")]
    [InlineData("history.query", """{"range":"forever","search":null,"page":0,"pageSize":20}""", "malformed")]
    [InlineData("history.query", """{"range":"today","search":null,"page":0,"pageSize":51}""", "malformed")]
    [InlineData("history.reprint", """{"jobId":"00000000-0000-0000-0000-000000000000"}""", "malformed")]
    [InlineData("setup.apply", """{"link":"https://example.test/setup"}""", "malformed")]
    [InlineData("connection.check", """{"serverUrl":"https://x.test","token":"abc","keepSavedToken":true}""", "malformed")]
    [InlineData("connection.apply", """{"verificationId":"not-an-id","startListening":true}""", "malformed")]
    [InlineData("setup.consentAnswer", """{"consentId":"0123456789abcdef0123456789abcdef","approved":true}""", "consent_not_found")]
    public async Task AnInvalidRequest_IsAnsweredWithAnError_AndTheConnectionStaysUsable(string command, string payload, string code)
    {
        await StartServerAsync();
        using var raw = await RawClient.HandshakeAsync(_pipeName);

        await raw.SendTextAsync($$"""{"type":"request","id":"abcdefgh-1","command":"{{command}}","payload":{{payload}}}""");
        var answer = await raw.ReceiveResponseAsync("abcdefgh-1");

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(code, answer.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _engine.StartCalls);
        Assert.Empty(_engine.SavedPrinters);
        Assert.Empty(_engine.SavedLanguages);

        await raw.SendTextAsync("""{"type":"request","id":"abcdefgh-2","command":"state.get","payload":{}}""");
        Assert.True((await raw.ReceiveResponseAsync("abcdefgh-2")).GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("""{"type":"request","id":"short","command":"state.get","payload":{}}""")]
    [InlineData("""{"type":"request","command":"state.get","payload":{}}""")]
    [InlineData("""{"type":"response","id":"abcdefgh-1","ok":true}""")]
    public async Task ARequestThatCannotBeAnswered_EndsTheConnection(string request)
    {
        await StartServerAsync();
        using var raw = await RawClient.HandshakeAsync(_pipeName);

        await raw.SendTextAsync(request);

        Assert.True(await raw.IsClosedAsync());
    }

    [Fact]
    public async Task AClientTheAuthorizerRefuses_IsDisconnected_BeforeAnythingIsAnswered()
    {
        await StartServerAsync(new RefusingAuthorizer());
        using var raw = await RawClient.ConnectAsync(_pipeName);

        await raw.SendAsync(new { type = "hello", protocol = PrintBridgeIpcProtocol.Version, client = "test" });

        Assert.True(await raw.IsClosedAsync());
        Assert.Contains(_logs.Lines, l => l.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThisTestProcess_IsAuthorizedByTheProductionRule()
    {
        // An interactive logon (a developer) or an enabled administrator (a CI runner) passes; the rule itself is checked
        // against a network logon on a disposable runner (PrintBridgeWindowsServiceTests).
        await StartServerAsync();
        var client = CreateClient();
        await client.Connected.WaitAsync(Wait, Ct);
        Assert.Equal(PrintBridgeServiceConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task WhileAnotherEngineHoldsTheLock_TheAppShowsWhy_AndCommandsAreRefused()
    {
        await StartServerAsync(activate: false);
        _backend.SetUnavailable(IpcAvailability.EngineHeldElsewhere);
        var client = CreateClient();
        await client.Connected.WaitAsync(Wait, Ct);

        Assert.Equal(PrintBridgeRuntimeIssueCode.EngineHeldElsewhere, client.GetStatus().LastIssue?.Code);
        Assert.False(client.Settings.HasSavedToken);
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => client.StartAsync(Ct));
        Assert.Equal("RuntimeIssue.EngineHeldElsewhere.Detail", refused.ResourceKey);
        Assert.Equal(0, _engine.StartCalls);

        // The other engine exited and the service took over: the app follows without reconnecting.
        _backend.Activate(_engine);
        await WaitUntilAsync(() => client.GetStatus().LastIssue is null);
        Assert.True(client.Settings.HasSavedToken);
        await client.StartAsync(Ct);
        Assert.Equal(1, _engine.StartCalls);
    }

    [Fact]
    public async Task WhenTheServiceGoesAway_TheAppShowsIt_AndReconnectsWhenItIsBack()
    {
        await StartServerAsync();
        var client = CreateClient();
        await client.Connected.WaitAsync(Wait, Ct);

        await _server!.StopAsync();
        await WaitUntilAsync(() => client.ConnectionState == PrintBridgeServiceConnectionState.Unavailable);
        Assert.Equal(PrintBridgeRuntimeIssueCode.ServiceUnavailable, client.GetStatus().LastIssue?.Code);
        await Assert.ThrowsAsync<LocalizedApplicationException>(() => client.TestPrinterAsync(Ct));

        // The service restarts (same pipe name): the client reconnects on its own.
        await _server.DisposeAsync();
        _server = null;
        await StartServerAsync(activateBackend: false);
        await client.Connected.WaitAsync(Wait, Ct);
        await client.TestPrinterAsync(Ct);
        Assert.Equal(1, _engine.TestPrintCalls);
    }

    [Fact]
    public async Task AMoveConfirmation_IsAskedOnlyOfTheAppThatOpenedTheLink_AndNobodyElseCanAnswerIt()
    {
        await StartServerAsync();
        _engine.ConsentRequest = new PrintBridgeConnectionTransferConsentRequest("Restaurant A", "a.app.wasla.tr", "Restaurant B", "b.app.wasla.tr");
        var opener = CreateClient();
        await opener.Connected.WaitAsync(Wait, Ct);
        using var other = await RawClient.HandshakeAsync(_pipeName);

        var asked = new TaskCompletionSource<PrintBridgeConnectionTransferConsentRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = opener.ApplySetupLinkAsync(
            "wasla-printbridge://setup?server=https%3A%2F%2Fb.app.wasla.tr&code=FAKEcode",
            (request, _) =>
            {
                asked.TrySetResult(request);
                return answer.Task;
            },
            Ct);

        var request = await asked.Task.WaitAsync(Wait, Ct);
        Assert.Equal("Restaurant B", request.TargetTenantName);

        // The other app received no confirmation and cannot answer one it was not asked.
        Assert.DoesNotContain(other.ReceivedSoFar(), m => m.Contains("setup.consent", StringComparison.Ordinal));
        await other.SendTextAsync("""{"type":"request","id":"abcdefgh-9","command":"setup.consentAnswer","payload":{"consentId":"0123456789abcdef0123456789abcdef","approved":true}}""");
        Assert.Equal("consent_not_found", (await other.ReceiveResponseAsync("abcdefgh-9")).GetProperty("error").GetProperty("code").GetString());

        answer.SetResult(true);
        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, await applying.WaitAsync(Wait, Ct));
        Assert.Equal([true], _engine.ConsentAnswers);
    }

    [Fact]
    public async Task ClosingTheAppWhileItsMoveConfirmationIsOpen_IsARefusal()
    {
        await StartServerAsync();
        _engine.ConsentRequest = new PrintBridgeConnectionTransferConsentRequest("Restaurant A", "a.app.wasla.tr", "Restaurant B", "b.app.wasla.tr");
        var opener = CreateClient(track: false);
        await opener.Connected.WaitAsync(Wait, Ct);

        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = opener.ApplySetupLinkAsync(
            "wasla-printbridge://setup?server=https%3A%2F%2Fb.app.wasla.tr&code=FAKEcode",
            (_, ct) =>
            {
                asked.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => true, TaskScheduler.Default);
            },
            CancellationToken.None);
        await asked.Task.WaitAsync(Wait, Ct);

        await opener.DisposeAsync();

        await WaitUntilAsync(() => { lock (_engine.ConsentAnswers) return _engine.ConsentAnswers.Count == 1; });
        Assert.Equal([false], _engine.ConsentAnswers);
    }

    [Fact]
    public async Task AVerifiedConnection_CanBeAppliedOnlyByTheAppThatCheckedIt()
    {
        await StartServerAsync();
        var checker = CreateClient();
        await checker.Connected.WaitAsync(Wait, Ct);
        var verification = await checker.CheckConnectionAsync(
            new PrintBridgeConnectionCandidate("https://sushim.app.wasla.tr", SecretToken, KeepSavedToken: false), Ct);
        using var other = await RawClient.HandshakeAsync(_pipeName);

        await other.SendTextAsync($$$"""{"type":"request","id":"abcdefgh-3","command":"connection.apply","payload":{"verificationId":"{{{verification.VerificationId}}}","startListening":true}}""");
        var refused = await other.ReceiveResponseAsync("abcdefgh-3");

        Assert.Equal("localized", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Shell.Setup.CheckExpired", refused.GetProperty("error").GetProperty("key").GetString());
        Assert.Equal(0, _engine.ApplyConnectionCalls);

        Assert.Equal(PrintBridgeConnectionChange.AppliedListening, await checker.ApplyVerifiedConnectionAsync(verification, true, Ct));
        Assert.Equal(("https://sushim.app.wasla.tr", SecretToken, true), _engine.AppliedConnection!.Value);
    }

    [Fact]
    public async Task TheTokenGoesToTheServiceOnce_AndNeverComesBack_NorReachesTheLog()
    {
        await StartServerAsync();
        using var raw = await RawClient.HandshakeAsync(_pipeName);

        await raw.SendTextAsync($$$"""{"type":"request","id":"abcdefgh-4","command":"connection.check","payload":{"serverUrl":"https://sushim.app.wasla.tr","token":"{{{SecretToken}}}","keepSavedToken":false}}""");
        var verified = await raw.ReceiveResponseAsync("abcdefgh-4");
        await raw.SendTextAsync($$$"""{"type":"request","id":"abcdefgh-5","command":"connection.apply","payload":{"verificationId":"{{{verified.GetProperty("payload").GetProperty("verificationId").GetString()}}}","startListening":true}}""");
        await raw.ReceiveResponseAsync("abcdefgh-5");
        await raw.SendTextAsync("""{"type":"request","id":"abcdefgh-6","command":"state.get","payload":{}}""");
        var state = await raw.ReceiveResponseAsync("abcdefgh-6");

        Assert.Equal([("https://sushim.app.wasla.tr", SecretToken)], _engine.CheckedConnections);
        Assert.All(raw.ReceivedSoFar(), frame => Assert.DoesNotContain(SecretToken, frame, StringComparison.Ordinal));
        Assert.All(raw.ReceivedSoFar(), frame => Assert.DoesNotContain("agentToken", frame, StringComparison.OrdinalIgnoreCase));
        Assert.True(state.GetProperty("payload").GetProperty("settings").GetProperty("hasSavedToken").GetBoolean());
        Assert.All(_logs.Lines, line => Assert.DoesNotContain(SecretToken, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoreThanEightRequestsInFlight_TheNinthIsBusy()
    {
        await StartServerAsync();
        _engine.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var raw = await RawClient.HandshakeAsync(_pipeName);

        for (var i = 1; i <= PrintBridgeIpcProtocol.MaxInFlightRequestsPerClient; i++)
            await raw.SendTextAsync($$$"""{"type":"request","id":"inflight-{{{i}}}","command":"printer.test","payload":{}}""");
        await WaitUntilAsync(() => _engine.TestPrintCalls == PrintBridgeIpcProtocol.MaxInFlightRequestsPerClient);
        await raw.SendTextAsync("""{"type":"request","id":"inflight-9","command":"printer.test","payload":{}}""");

        Assert.Equal("busy", (await raw.ReceiveResponseAsync("inflight-9")).GetProperty("error").GetProperty("code").GetString());
        _engine.TestPrintGate.SetResult();

        // The eight held requests finish concurrently, so their answers arrive in any order.
        var answered = new HashSet<string>(StringComparer.Ordinal);
        while (answered.Count < PrintBridgeIpcProtocol.MaxInFlightRequestsPerClient)
        {
            var message = await raw.ReceiveAsync();
            if (message.GetProperty("type").GetString() != "response")
                continue;
            Assert.True(message.GetProperty("ok").GetBoolean());
            Assert.True(answered.Add(message.GetProperty("id").GetString()!));
        }

        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"inflight-{i}").Order(), answered.Order());
    }

    [Fact]
    public void ThePipe_DeniesNetworkLogons_AndGivesInteractiveUsersReadAndWriteButNoNewInstances()
    {
        var rules = PrintBridgeIpcServer.CreateSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        var network = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
        Assert.Equal(AccessControlType.Deny, network.AccessControlType);
        var interactive = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)));
        Assert.Equal(AccessControlType.Allow, interactive.AccessControlType);
        Assert.True(interactive.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(interactive.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }

    [Fact]
    public async Task APipeNameSomeoneElseCreatedFirst_IsNeverJoined()
    {
        // Another process (here: a pipe this test owns) holds the name first. The service must not join it as a further
        // instance, so a client connecting to the name never reaches the service through someone else's pipe.
        using var squatter = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _server = new PrintBridgeIpcServer(_pipeName, _backend, new WindowsTokenClientAuthorizer(), _logs, "v-test");
        _server.Start();
        await _server.Listening.WaitAsync(Wait, Ct);

        await WaitUntilAsync(() => _logs.Lines.Any(l => l.Contains("already in use", StringComparison.Ordinal)));
    }

    private async Task StartServerAsync(IPrintBridgeIpcClientAuthorizer? authorizer = null, bool activate = true, bool activateBackend = true)
    {
        _server = new PrintBridgeIpcServer(_pipeName, _backend, authorizer ?? new WindowsTokenClientAuthorizer(), _logs, "v-test");
        _server.Start();
        await _server.Listening.WaitAsync(Wait, Ct);
        if (activate && activateBackend)
            _backend.Activate(_engine);
    }

    private PrintBridgeServiceClient CreateClient(int protocol = PrintBridgeIpcProtocol.Version, bool track = true)
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(_pipeName, new CurrentUserServerVerifier()),
            NullLogger.Instance)
        {
            ProtocolVersion = protocol
        };
        client.Start();
        if (track)
            _disposables.Add(client);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(25, Ct);
        }
    }

    private sealed class RefusingAuthorizer : IPrintBridgeIpcClientAuthorizer
    {
        public PrintBridgeIpcClientIdentity? Authorize(NamedPipeServerStream pipe, out string? refusal)
        {
            refusal = "refused by the test";
            return null;
        }
    }

    /// <summary>A client that speaks raw frames, to send what the production client never would.</summary>
    private sealed class RawClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly List<string> _received = [];

        private RawClient(NamedPipeClientStream pipe) => _pipe = pipe;

        public static async Task<RawClient> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync((int)Wait.TotalMilliseconds, Ct);
            return new RawClient(pipe);
        }

        /// <summary>Connected, hello answered, and the first state read.</summary>
        public static async Task<RawClient> HandshakeAsync(string pipeName)
        {
            var client = await ConnectAsync(pipeName);
            await client.SendAsync(new { type = "hello", protocol = PrintBridgeIpcProtocol.Version, client = "raw" });
            Assert.Equal("welcome", (await client.ReceiveAsync()).GetProperty("type").GetString());
            Assert.Equal("state", (await client.ReceiveAsync()).GetProperty("event").GetString());
            return client;
        }

        public Task SendAsync(object message) => SendTextAsync(JsonSerializer.Serialize(message));

        public Task SendTextAsync(string text) => SendBytesAsync(Encoding.UTF8.GetBytes(text));

        public async Task SendBytesAsync(byte[] body)
        {
            await SendHeaderAsync(body.Length);
            await _pipe.WriteAsync(body, Ct);
            await _pipe.FlushAsync(Ct);
        }

        public async Task SendHeaderAsync(int length)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await _pipe.WriteAsync(header, Ct);
            await _pipe.FlushAsync(Ct);
        }

        public async Task<JsonElement> ReceiveAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Wait);
            var text = await PrintBridgeIpcProtocol.ReadFrameAsync(_pipe, PrintBridgeIpcProtocol.MaxServiceFrameBytes, timeout.Token)
                ?? throw new IOException("The service closed the pipe.");
            lock (_received)
                _received.Add(text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        /// <summary>Reads past state events to the response with <paramref name="id"/>.</summary>
        public async Task<JsonElement> ReceiveResponseAsync(string id)
        {
            while (true)
            {
                var message = await ReceiveAsync();
                if (message.GetProperty("type").GetString() == "response" && message.GetProperty("id").GetString() == id)
                    return message;
            }
        }

        public string[] ReceivedSoFar()
        {
            lock (_received)
                return [.. _received];
        }

        /// <summary>Whether the service closed the pipe (reads return end of stream or fail) within the time.</summary>
        public async Task<bool> IsClosedAsync(TimeSpan? within = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(within ?? Wait);
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    if (await _pipe.ReadAsync(buffer, timeout.Token) == 0)
                        return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Dispose() => _pipe.Dispose();
    }
}

/// <summary>Collects every log line (all levels) of the service part, to check that nothing secret is written.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return [.. _lines];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
                owner._lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
