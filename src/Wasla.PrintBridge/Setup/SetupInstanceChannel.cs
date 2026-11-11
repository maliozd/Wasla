using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Setup;

/// <summary>What happened to a setup link forwarded to the primary instance.</summary>
public enum SetupForwardResult
{
    /// <summary>Written to a pipe whose owner was verified to be this user.</summary>
    Delivered,

    /// <summary>No pipe of this user accepted a connection before the timeout.</summary>
    NoListener,

    /// <summary>
    /// A pipe with this name exists but is owned by someone else, or its access rules refuse this user. Nothing was
    /// written to it.
    /// </summary>
    Rejected,

    /// <summary>The connection failed or broke before the link was written completely.</summary>
    Failed,
}

/// <summary>
/// Local IPC that forwards a <c>wasla-printbridge://</c> setup link from a second launch to the primary instance of the
/// same Windows user and session, so the link is handled in one place and no second tray app starts (WAS-92).
///
/// The pipe name carries the user's SID and session id (<see cref="PrintBridgeInstanceScope"/>). Names are not secret,
/// so the endpoints protect themselves:
/// <list type="bullet">
/// <item>The server creates the pipe with <see cref="PipeOptions.FirstPipeInstance"/> and access for this user only. If
/// the name already exists (another process created it first) it never joins that pipe; it keeps retrying in the
/// background and logs the refusal once.</item>
/// <item>The client connects with <see cref="PipeOptions.CurrentUserOnly"/>: the connection is completed only when
/// the pipe's owner is this user's token owner; otherwise it throws before anything is written. It allows only
/// identification, so a server cannot act as the client.</item>
/// </list>
/// The message is one UTF-8 setup link, at most 8 KiB, ended by the client closing the pipe. The receiver drops
/// anything else.
/// </summary>
public sealed class SetupInstanceChannel : IDisposable
{
    /// <summary>Message contract version; the full name appends the user's SID and session id.</summary>
    public const string PipeNamePrefix = "WaslaPrintBridge.Setup.v1";

    internal const int MaxMessageBytes = 8 * 1024;

    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NameInUseRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    /// <summary>Raised on a background thread when a setup link is received from another instance.</summary>
    public event Action<string>? UriReceived;

    public SetupInstanceChannel(ILogger logger)
        : this(logger, PrintBridgeInstanceScope.Current().SetupPipeName, DefaultReadTimeout)
    {
    }

    internal SetupInstanceChannel(ILogger logger, string pipeName, TimeSpan readTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _logger = logger;
        _pipeName = pipeName;
        _readTimeout = readTimeout;
    }

    public void StartListening()
    {
        _listenTask ??= Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var nameInUseReported = false;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer(_pipeName);
            }
            catch (UnauthorizedAccessException)
            {
                // FirstPipeInstance: the name already exists, created by another process that may belong to another
                // account and may have its own access rules. Joining it could hand that process this user's links.
                if (!nameInUseReported)
                {
                    _logger.LogWarning(
                        "Print Bridge setup IPC: the setup pipe name of this user and session is already in use by another process; "
                        + "setup links cannot be forwarded to this instance until it is free. Retrying.");
                    nameInUseReported = true;
                }

                if (!await DelayAsync(NameInUseRetryDelay, ct).ConfigureAwait(false))
                    break;
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Print Bridge setup IPC listener could not create the pipe; retrying.");
                if (!await DelayAsync(ErrorRetryDelay, ct).ConfigureAwait(false))
                    break;
                continue;
            }

            if (nameInUseReported)
            {
                _logger.LogInformation("Print Bridge setup IPC: the setup pipe is listening again.");
                nameInUseReported = false;
            }

            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    var message = await ReadMessageAsync(server, ct).ConfigureAwait(false);
                    if (message is not null)
                        UriReceived?.Invoke(message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Print Bridge setup IPC listener error; continuing.");
                    if (!await DelayAsync(ErrorRetryDelay, ct).ConfigureAwait(false))
                        break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: CreateCurrentUserPipeSecurity());

    /// <summary>
    /// Reads one message until the client closes its end. A client that sends nothing, too much, or something that is
    /// not a setup link within the read timeout is dropped, so it cannot hold the only pipe instance.
    /// </summary>
    private async Task<string?> ReadMessageAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_readTimeout);

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        try
        {
            int read;
            while ((read = await server.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxMessageBytes)
                {
                    _logger.LogWarning("Print Bridge setup IPC: dropped a message larger than {MaxBytes} bytes.", MaxMessageBytes);
                    return null;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Print Bridge setup IPC: dropped a connection that did not finish its message in time.");
            return null;
        }

        if (buffer.Length == 0)
            return null;

        var message = TryDecodeSetupLink(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (message is null)
            _logger.LogWarning("Print Bridge setup IPC: dropped a message that is not a setup link.");
        return message;
    }

    /// <summary>
    /// The message must be valid UTF-8 and start with the setup-link scheme, like a link accepted from the command line.
    /// Its content is never logged: it carries a one-time setup code.
    /// </summary>
    internal static string? TryDecodeSetupLink(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return text.StartsWith(PrintBridgeProtocolUri.Scheme + "://", StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
    }

    /// <summary>Forwards a setup link to the primary instance of this Windows user and session.</summary>
    public static SetupForwardResult TrySend(string uri, TimeSpan timeout) =>
        TrySend(uri, PrintBridgeInstanceScope.Current().SetupPipeName, timeout);

    internal static SetupForwardResult TrySend(string uri, string pipeName, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return SetupForwardResult.Failed;

        try
        {
            // CurrentUserOnly: Connect returns only after comparing the pipe's owner with this user's token owner, and
            // throws UnauthorizedAccessException for a pipe another account created, before anything is written.
            // Identification: the server may identify this client but never impersonate it.
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            client.Connect((int)timeout.TotalMilliseconds);

            // The pipe has no buffer: the write completes only when the server reads it. An endpoint that accepts and
            // never reads must not keep this launch alive, so the write gets the same time limit.
            var bytes = Encoding.UTF8.GetBytes(uri);
            using var writeDeadline = new CancellationTokenSource(timeout);
            client.WriteAsync(bytes, writeDeadline.Token).AsTask().GetAwaiter().GetResult();
            return SetupForwardResult.Delivered;
        }
        catch (TimeoutException)
        {
            return SetupForwardResult.NoListener;
        }
        catch (UnauthorizedAccessException)
        {
            return SetupForwardResult.Rejected;
        }
        catch (OperationCanceledException)
        {
            return SetupForwardResult.Failed;
        }
        catch (IOException)
        {
            return SetupForwardResult.Failed;
        }
    }

    private static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            // WaitForConnectionAsync, the read and the retry delays all observe the token, so no connection to the
            // pipe name is needed to stop the loop.
            _cts.Cancel();
            _listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // best effort
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
