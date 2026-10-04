using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wasla.PrintBridge.Setup;

/// <summary>
/// Local single-machine IPC used to forward a <c>wasla-printbridge://</c> setup URI from a freshly
/// launched (secondary) instance to the already-running primary instance, so the setup is processed
/// in one place and no second tray app is created.
///
/// Uses a per-user named pipe. Messages are bounded in size and validated by the receiver.
/// </summary>
public sealed class SetupInstanceChannel : IDisposable
{
    public const string PipeName = "WaslaPrintBridge.Setup.v1";

    private const int MaxMessageBytes = 8 * 1024;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    /// <summary>Raised on a background thread when a URI is received from another instance.</summary>
    public event Action<string>? UriReceived;

    public SetupInstanceChannel(ILogger logger)
    {
        _logger = logger;
    }

    public void StartListening()
    {
        _listenTask ??= Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity: CreateCurrentUserPipeSecurity());

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var message = await ReadMessageAsync(server, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(message))
                    UriReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Print Bridge setup IPC listener error; continuing.");
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task<string?> ReadMessageAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await server.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMessageBytes)
                return null; // reject oversized input
        }

        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Try to forward a setup URI to a running primary instance. Returns false if no instance is listening.
    /// </summary>
    public static bool TrySend(string uri, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(uri);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            // Unblock a pending WaitForConnectionAsync by dialing ourselves.
            try
            {
                using var self = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                self.Connect(100);
            }
            catch
            {
                // ignored
            }

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
