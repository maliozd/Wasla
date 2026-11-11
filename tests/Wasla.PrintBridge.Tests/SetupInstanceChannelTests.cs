using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-92: forwarding a setup link from a second launch to the primary instance over the user-scoped pipe. Every test
/// uses a pipe name of its own (never the installed client's) and a fake setup code.
///
/// These run under one Windows account. They show that this user's links reach this user's listener, that a pipe which
/// refuses this user gets nothing, that a listener never joins a name another process created first, and that bad
/// input is dropped. They do not prove isolation between accounts; <see cref="SetupInstanceCrossAccountTests"/> does
/// that with a second, temporary account on CI runners.
/// </summary>
public sealed class SetupInstanceChannelTests
{
    private const string FakeLink =
        "wasla-printbridge://setup?server=https%3A%2F%2Fsushim.app.wasla.tr%2F&code=FAKE0test0code0only0000000000";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TrySend_DeliversTheLinkToThisUsersListener()
    {
        using var listener = Listener.Start();

        var result = SetupInstanceChannel.TrySend(FakeLink, listener.PipeName, ConnectTimeout);

        Assert.Equal(SetupForwardResult.Delivered, result);
        Assert.Equal(FakeLink, await listener.NextAsync());
    }

    [Fact]
    public void TrySend_WithoutAListener_ReportsNoListener()
    {
        var result = SetupInstanceChannel.TrySend(FakeLink, UniquePipeName(), TimeSpan.FromMilliseconds(300));

        Assert.Equal(SetupForwardResult.NoListener, result);
    }

    [Fact]
    public void TrySend_WithoutALink_ConnectsNowhere()
    {
        Assert.Equal(SetupForwardResult.Failed, SetupInstanceChannel.TrySend("  ", UniquePipeName(), ConnectTimeout));
    }

    [Fact]
    public async Task TrySend_ToAPipeThatRefusesThisUser_ReturnsRejected_WithoutThrowing()
    {
        // A pipe whose access rules exclude this user, as the pipe of another account's instance does.
        var pipeName = UniquePipeName();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        using var other = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var connection = other.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var result = SetupInstanceChannel.TrySend(FakeLink, pipeName, ConnectTimeout);

        Assert.Equal(SetupForwardResult.Rejected, result);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(connection.IsCompleted);
    }

    [Fact]
    public async Task TrySend_ToAnEndpointThatNeverReads_GivesUpAfterTheTimeout()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var started = DateTime.UtcNow;
        var result = SetupInstanceChannel.TrySend(FakeLink, pipeName, TimeSpan.FromMilliseconds(500));

        Assert.Equal(SetupForwardResult.Failed, result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "The write waited for a server that never reads.");
        await connection.WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheForwardingClient_AllowsIdentificationOnly_NeverImpersonation()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        // TrySend writes the link in one call and closes its end once that write completes. The pipe has no buffer, so
        // the write completes only after the server has read all of it: while part of the link is unread, the client is
        // still connected. The client must be inspected in that window; after it closes, RunAsClient may throw "Pipe is
        // broken". The long limit only bounds a failure; the server holds the write open deliberately.
        var send = Task.Run(
            () => SetupInstanceChannel.TrySend(FakeLink, pipeName, TimeSpan.FromSeconds(30)),
            TestContext.Current.CancellationToken);
        await connected.WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);

        var head = new byte[16];
        await server.ReadExactlyAsync(head, TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
        Assert.False(send.IsCompleted, "The forwarding client finished its write before the server had read all of it.");

        // Impersonation uses the security context of the data last read from the pipe, which is this client's.
        TokenImpersonationLevel level = default;
        server.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            level = client.ImpersonationLevel;
        });
        Assert.Equal(TokenImpersonationLevel.Identification, level);

        // Reading the rest completes the client's write. The end of the stream arrives only when TrySend closes its end.
        var rest = await ReadToEndAsync(server);
        Assert.Equal(SetupForwardResult.Delivered, await send.WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(FakeLink, Encoding.UTF8.GetString(head) + rest);
    }

    [Fact]
    public async Task Listener_NeverJoinsANameCreatedFirstByAnotherProcess_AndTakesItOverOnceFree()
    {
        var pipeName = UniquePipeName();
        var squatter = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, EveryoneMayConnectAndAddInstances());
        try
        {
            using var listener = Listener.Start(pipeName);
            await listener.Logs.WaitForAsync("already in use by another process");

            // Same account here, so this client accepts the squatter's pipe; the point is that the listener did not
            // join it, so nothing sent to that name reaches this instance.
            var squatterReceived = ReceiveOnceAsync(squatter);
            Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(FakeLink, pipeName, ConnectTimeout));
            Assert.Equal(FakeLink, await squatterReceived);
            Assert.Equal(
                SetupForwardResult.NoListener,
                SetupInstanceChannel.TrySend(FakeLink, pipeName, TimeSpan.FromMilliseconds(500)));
            Assert.Empty(listener.Received);

            squatter.Dispose();

            var second = FakeLink + "1";
            Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(second, pipeName, TimeSpan.FromSeconds(10)));
            Assert.Equal(second, await listener.NextAsync());
            await listener.Logs.WaitForAsync("listening again");
        }
        finally
        {
            squatter.Dispose();
        }
    }

    [Fact]
    public async Task Listener_DropsMalformedMessages_AndKeepsListening()
    {
        using var listener = Listener.Start();

        await SendRawAsync(listener.PipeName, [0x77, 0x61, 0xC3, 0x28]); // invalid UTF-8
        await SendRawAsync(listener.PipeName, Encoding.UTF8.GetBytes("https://sushim.app.wasla.tr/not-a-setup-link"));
        await SendRawAsync(
            listener.PipeName,
            Encoding.UTF8.GetBytes(FakeLink + new string('a', SetupInstanceChannel.MaxMessageBytes)));
        await SendRawAsync(listener.PipeName, []); // connects and closes without a message

        Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(FakeLink, listener.PipeName, ConnectTimeout));
        Assert.Equal(FakeLink, await listener.NextAsync());
        Assert.Equal(new[] { FakeLink }, listener.Received);
        Assert.Equal(2, listener.Logs.Count("not a setup link"));
        Assert.Equal(1, listener.Logs.Count("larger than"));
        Assert.DoesNotContain("FAKE0test", listener.Logs.Text());
    }

    [Fact]
    public async Task Listener_DropsAClientThatNeverFinishesItsMessage()
    {
        using var listener = Listener.Start(readTimeout: TimeSpan.FromMilliseconds(300));

        using var stalled = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        stalled.Connect((int)ConnectTimeout.TotalMilliseconds);
        stalled.Write(Encoding.UTF8.GetBytes("wasla-printbridge://setup?server="));

        Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(FakeLink, listener.PipeName, ConnectTimeout));
        Assert.Equal(FakeLink, await listener.NextAsync());
        Assert.Equal(new[] { FakeLink }, listener.Received);
        Assert.Equal(1, listener.Logs.Count("did not finish its message in time"));
    }

    [Fact]
    public async Task Dispose_StopsListeningPromptly_AndFreesTheName()
    {
        var listener = Listener.Start();
        Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(FakeLink, listener.PipeName, ConnectTimeout));
        await listener.NextAsync();

        var started = DateTime.UtcNow;
        listener.Dispose();
        // Dispose waits at most 1 second for the loop; it must not wait for a client to connect.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "Dispose waited for a connection.");

        // A disposed listener accepts nothing, even while Windows is still closing its pipe instance.
        Assert.Equal(
            SetupForwardResult.NoListener,
            SetupInstanceChannel.TrySend(FakeLink, listener.PipeName, TimeSpan.FromMilliseconds(300)));
        Assert.Single(listener.Received);

        // The name is released: a new listener takes it (at the latest after one retry) and receives links.
        using var next = Listener.Start(listener.PipeName);
        Assert.Equal(SetupForwardResult.Delivered, SetupInstanceChannel.TrySend(FakeLink, next.PipeName, TimeSpan.FromSeconds(10)));
        Assert.Equal(FakeLink, await next.NextAsync());
    }

    [Theory]
    [InlineData(FakeLink, true)]
    [InlineData("WASLA-PRINTBRIDGE://setup?server=x&code=y", true)]
    [InlineData("https://sushim.app.wasla.tr/", false)]
    [InlineData("wasla-printbridge:setup", false)]
    [InlineData(" wasla-printbridge://setup", false)]
    public void TryDecodeSetupLink_AcceptsOnlySetupLinks(string text, bool accepted)
    {
        var decoded = SetupInstanceChannel.TryDecodeSetupLink(Encoding.UTF8.GetBytes(text));

        Assert.Equal(accepted ? text : null, decoded);
    }

    [Fact]
    public void TryDecodeSetupLink_RejectsInvalidUtf8()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("wasla-printbridge://setup?code="), 0xFF];

        Assert.Null(SetupInstanceChannel.TryDecodeSetupLink(bytes));
    }

    private static string UniquePipeName() => $"WaslaPbTest.Setup.{Guid.NewGuid():N}";

    private static PipeSecurity EveryoneMayConnectAndAddInstances()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

    private static async Task SendRawAsync(string pipeName, byte[] bytes)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, TestContext.Current.CancellationToken);
        try
        {
            await client.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            // The listener may close an oversized message before the client has written all of it.
        }
    }

    /// <summary>
    /// Waits for one client and reads its message. Started before the send: the pipe has no buffer, so a client's
    /// write completes only while the server reads.
    /// </summary>
    private static Task<string> ReceiveOnceAsync(NamedPipeServerStream server) =>
        Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken)
                .WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
            return await ReadToEndAsync(server);
        });

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken).WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class Listener : IDisposable
    {
        private readonly SetupInstanceChannel _channel;
        private readonly ConcurrentQueue<string> _received = new();
        private readonly SemaphoreSlim _arrived = new(0);

        private Listener(string pipeName, TimeSpan readTimeout)
        {
            PipeName = pipeName;
            _channel = new SetupInstanceChannel(Logs, pipeName, readTimeout);
            _channel.UriReceived += uri =>
            {
                _received.Enqueue(uri);
                _arrived.Release();
            };
            _channel.StartListening();
        }

        public string PipeName { get; }

        public CapturingLogger Logs { get; } = new();

        public IReadOnlyCollection<string> Received => _received.ToArray();

        public static Listener Start(string? pipeName = null, TimeSpan? readTimeout = null) =>
            new(pipeName ?? UniquePipeName(), readTimeout ?? TimeSpan.FromSeconds(5));

        public async Task<string> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "No setup link arrived.");
            return _received.Last();
        }

        public void Dispose() => _channel.Dispose();
    }

    internal sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text() => string.Join(Environment.NewLine, _entries);

        public int Count(string fragment) => _entries.Count(entry => entry.Contains(fragment, StringComparison.Ordinal));

        public async Task WaitForAsync(string fragment)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Count(fragment) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Expected a log entry containing \"{fragment}\". Logged: {Text()}");
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}");
    }
}
