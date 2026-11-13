using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Hosting;

namespace Wasla.PrintBridge.Ipc;

/// <summary>
/// The service's end of the local pipe (WAS-61). Up to <see cref="PrintBridgeIpcProtocol.MaxServerInstances"/> clients at a
/// time; one instance always listens while there is room, so the name never disappears while the service runs. The
/// pipe's access rules give interactive users read and write, deny network logons, and give nobody but the service,
/// SYSTEM and administrators the right to add instances. An instance is created as the first one whenever the service
/// holds none, so it never joins a pipe someone else created first.
/// </summary>
internal sealed class PrintBridgeIpcServer : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan NameInUseRetry = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly PrintBridgeServiceBackend _backend;
    private readonly IPrintBridgeIpcClientAuthorizer _authorizer;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly string _serviceVersion;
    private readonly SemaphoreSlim _slots = new(PrintBridgeIpcProtocol.MaxServerInstances, PrintBridgeIpcProtocol.MaxServerInstances);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _connections = [];
    private int _instances;
    private Task _acceptLoop = Task.CompletedTask;

    public PrintBridgeIpcServer(
        string pipeName,
        PrintBridgeServiceBackend backend,
        IPrintBridgeIpcClientAuthorizer authorizer,
        ILoggerFactory loggers,
        string serviceVersion)
    {
        _pipeName = pipeName;
        _backend = backend;
        _authorizer = authorizer;
        _loggers = loggers;
        _logger = loggers.CreateLogger("Wasla.PrintBridge.Ipc");
        _serviceVersion = serviceVersion;
    }

    /// <summary>Completes once the first instance listens (or the name is found in use and retrying began).</summary>
    public Task Listening => _listening.Task;

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    public async Task StopAsync()
    {
        if (!_stopping.IsCancellationRequested)
            await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] connections;
        lock (_sync)
            connections = [.. _connections];
        await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stopping.Token;
        var nameInUseLogged = false;
        while (!ct.IsCancellationRequested)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateInstance();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _slots.Release();
                if (!nameInUseLogged)
                {
                    _logger.LogError(
                        "The Print Bridge pipe name is already in use by another process; the service retries every {Seconds} s and does not join that pipe.",
                        NameInUseRetry.TotalSeconds);
                    nameInUseLogged = true;
                }

                _listening.TrySetResult();
                await Task.Delay(NameInUseRetry, ct).ConfigureAwait(false);
                continue;
            }

            if (nameInUseLogged)
            {
                _logger.LogInformation("The Print Bridge pipe listens now.");
                nameInUseLogged = false;
            }

            _listening.TrySetResult();
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ReleaseInstance(pipe);
                break;
            }
            catch (IOException)
            {
                ReleaseInstance(pipe);
                continue;
            }

            Track(RunConnectionAsync(pipe, ct));
        }
    }

    private async Task RunConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var connection = new PrintBridgeIpcConnection(
                pipe,
                _backend,
                _authorizer,
                _loggers.CreateLogger<PrintBridgeIpcConnection>(),
                _serviceVersion);
            await connection.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("A Print Bridge pipe client ended with an error: {Error}", ex.GetType().Name);
        }
        finally
        {
            ReleaseInstance(pipe);
        }
    }

    private void Track(Task connection)
    {
        lock (_sync)
            _connections.Add(connection);

        _ = connection.ContinueWith(
            t =>
            {
                lock (_sync)
                    _connections.Remove(t);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private NamedPipeServerStream CreateInstance()
    {
        lock (_sync)
        {
            var options = PipeOptions.Asynchronous;
            if (_instances == 0)
                options |= PipeOptions.FirstPipeInstance;

            var pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                PrintBridgeIpcProtocol.MaxServerInstances,
                PipeTransmissionMode.Byte,
                options,
                BufferSize,
                BufferSize,
                CreateSecurity());
            _instances++;
            return pipe;
        }
    }

    private void ReleaseInstance(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Dispose();
        }
        finally
        {
            lock (_sync)
                _instances--;
            _slots.Release();
        }
    }

    /// <summary>The pipe's access rules (see the class summary).</summary>
    internal static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (self.User is { } user)
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        // Read and write only: no CreateNewInstance, so no other process can add an instance of this pipe.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return security;
    }
}
