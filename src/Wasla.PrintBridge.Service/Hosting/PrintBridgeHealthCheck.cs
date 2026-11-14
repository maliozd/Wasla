using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// <c>Wasla.PrintBridge.Service.exe health-check</c> (WAS-63): the installer runs it as LocalSystem right after it started the
/// service, inside its transaction, and fails (rolling everything back) unless the service answers on its pipe within
/// <see cref="Timeout"/>, speaks this build's IPC protocol, is the version of this executable, and has its data folder
/// protected. Before anything is sent, the pipe's server must be the process the Service Control Manager reports for the
/// service (or, for a data folder of its own outside the installed one, a process of this user). It needs neither a
/// connection to Wasla nor a printer.
/// </summary>
internal static class PrintBridgeHealthCheck
{
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    public static int Run(PrintBridgeServiceOptions options, TextWriter output, TextWriter error)
    {
        var expected = PrintBridgeReleaseVersion.Of(typeof(PrintBridgeHealthCheck).Assembly);
        var deadline = DateTime.UtcNow + Timeout;
        var detail = "the service did not answer";
        while (DateTime.UtcNow < deadline)
        {
            bool? healthy;
            try
            {
                healthy = Check(options.Endpoint, expected, out detail);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or PrintBridgeIpcProtocolException or JsonException)
            {
                healthy = null;
                detail = $"the service did not answer properly ({ex.GetType().Name})";
            }

            if (healthy == true)
            {
                output.WriteLine($"Wasla Print Bridge {PrintBridgeReleaseVersion.Format(expected)} is running and healthy.");
                return 0;
            }

            if (healthy == false)
                break;

            Thread.Sleep(Retry);
        }

        error.WriteLine($"Wasla Print Bridge health check failed: {detail}.");
        return 1;
    }

    /// <summary>True healthy, false definitely not, null not yet (starting, not answering).</summary>
    private static bool? Check(PrintBridgeServiceEndpoint endpoint, Version expected, out string detail)
    {
        var servicePid = ServiceProcessId(endpoint.ServiceName);
        if (servicePid is null && endpoint.IsProduction)
        {
            detail = $"the service {endpoint.ServiceName} is not running";
            return null;
        }

        var pipeOptions = servicePid is null ? PipeOptions.CurrentUserOnly : PipeOptions.None;
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, pipeOptions, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(2000);
        }
        catch (TimeoutException)
        {
            detail = "the service's pipe does not answer";
            return null;
        }

        if (servicePid is not null
            && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != servicePid))
        {
            detail = "the pipe is not the service's";
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        Write(pipe, new IpcHello(PrintBridgeIpcProtocol.Hello, PrintBridgeIpcProtocol.Version, "health-check"), ct);

        using (var welcome = Read(pipe, ct))
        {
            if (Text(welcome.RootElement, "type") != PrintBridgeIpcProtocol.Welcome
                || !welcome.RootElement.TryGetProperty("protocol", out var protocol)
                || protocol.ValueKind != JsonValueKind.Number
                || protocol.GetInt32() != PrintBridgeIpcProtocol.Version)
            {
                detail = "the service speaks another protocol version";
                return false;
            }
        }

        const string requestId = "health-check-0001";
        Write(pipe, new { type = PrintBridgeIpcProtocol.Request, id = requestId, command = PrintBridgeIpcProtocol.ServiceHealth, payload = new { } }, ct);
        while (true)
        {
            using var frame = Read(pipe, ct);
            var root = frame.RootElement;
            if (Text(root, "type") != PrintBridgeIpcProtocol.Response || Text(root, "id") != requestId)
                continue; // the state event sent after the handshake

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                detail = "the service refused the health request";
                return false;
            }

            var health = PrintBridgeIpcProtocol.Deserialize<IpcServiceHealth>(root.GetProperty("payload"));
            if (!PrintBridgeReleaseVersion.TryParse(health.Version, out var running) || running != expected)
            {
                detail = $"the service is version {health.Version}, not {PrintBridgeReleaseVersion.Format(expected)}";
                return false;
            }

            switch (health.Availability)
            {
                case IpcAvailability.Active or IpcAvailability.EngineHeldElsewhere:
                    detail = "healthy";
                    return true;
                case IpcAvailability.DataFolderNotSecured:
                    detail = "the service's data folder is not protected";
                    return false;
                default:
                    detail = "the service is still starting";
                    return null;
            }
        }
    }

    private static void Write<T>(Stream pipe, T message, CancellationToken ct) =>
        PrintBridgeIpcProtocol.WriteFrameAsync(pipe, PrintBridgeIpcProtocol.Serialize(message), PrintBridgeIpcProtocol.MaxClientFrameBytes, ct)
            .GetAwaiter().GetResult();

    private static JsonDocument Read(Stream pipe, CancellationToken ct)
    {
        var text = PrintBridgeIpcProtocol.ReadFrameAsync(pipe, PrintBridgeIpcProtocol.MaxServiceFrameBytes, ct).GetAwaiter().GetResult()
                   ?? throw new IOException("The service closed the pipe.");
        return PrintBridgeIpcProtocol.ParseStrict(text);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The process id the Service Control Manager reports for a running service, or null.</summary>
    private static uint? ServiceProcessId(string serviceName)
    {
        const uint scManagerConnect = 0x0001;
        const uint serviceQueryStatus = 0x0004;
        const int scStatusProcessInfo = 0;
        const uint serviceRunning = 4;

        var manager = OpenSCManager(null, null, scManagerConnect);
        if (manager == IntPtr.Zero)
            return null;
        try
        {
            var service = OpenService(manager, serviceName, serviceQueryStatus);
            if (service == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new byte[36];
                if (!QueryServiceStatusEx(service, scStatusProcessInfo, buffer, (uint)buffer.Length, out _))
                    return null;
                var state = BitConverter.ToUInt32(buffer, 4);
                var pid = BitConverter.ToUInt32(buffer, 28);
                return state == serviceRunning && pid != 0 ? pid : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, byte[] buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
