using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Ipc;

namespace Wasla.PrintBridge.ServiceClient;

/// <summary>
/// Decides, after connecting and before anything is written, whether the pipe's server is the Print Bridge service
/// (WAS-61). Pipe names are machine-wide and not secret, so the name proves nothing.
/// </summary>
public interface IPrintBridgeServiceVerifier
{
    /// <summary>Whether the client must connect with <see cref="PipeOptions.CurrentUserOnly"/> (the server runs as this user).</summary>
    bool RequiresCurrentUserOnly { get; }

    bool IsTrusted(NamedPipeClientStream pipe, out string? reason);
}

/// <summary>
/// The production check: the pipe's server process is the process the Service Control Manager runs for the service.
/// Only administrators can register that service, so a process that created the pipe name first never passes.
/// </summary>
public sealed class ServiceControlManagerVerifier(string serviceName) : IPrintBridgeServiceVerifier
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    public string ServiceName { get; } = serviceName;

    public bool RequiresCurrentUserOnly => false;

    public bool IsTrusted(NamedPipeClientStream pipe, out string? reason)
    {
        reason = null;
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId))
        {
            reason = "the pipe's server process is unknown";
            return false;
        }

        var serviceProcessId = QueryServiceProcessId(ServiceName);
        if (serviceProcessId is null or 0)
        {
            reason = "the service is not running";
            return false;
        }

        if (serviceProcessId != serverProcessId)
        {
            reason = "the pipe is not served by the service";
            return false;
        }

        return true;
    }

    /// <summary>The process id the Service Control Manager reports for <paramref name="serviceName"/>, or null.</summary>
    public static uint? QueryServiceProcessId(string serviceName)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return null;

        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return null;

            try
            {
                var size = Marshal.SizeOf<ServiceStatusProcess>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
                        return null;
                    return Marshal.PtrToStructure<ServiceStatusProcess>(buffer).ProcessId;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>
/// For a service started by the developer as a console program (Debug builds with a development data root) and for
/// in-process tests: the pipe must be owned by the current user, which <see cref="PipeOptions.CurrentUserOnly"/> checks
/// before the connection completes.
/// </summary>
public sealed class CurrentUserServerVerifier : IPrintBridgeServiceVerifier
{
    public bool RequiresCurrentUserOnly => true;

    public bool IsTrusted(NamedPipeClientStream pipe, out string? reason)
    {
        reason = null;
        return true;
    }
}

/// <summary>Which service the app talks to and how it verifies it.</summary>
public sealed record PrintBridgeServiceClientOptions(string PipeName, IPrintBridgeServiceVerifier Verifier)
{
    /// <summary>
    /// The installed service, verified through the Service Control Manager. A Debug build running against
    /// <c>WASLA_PRINTBRIDGE_DATA_ROOT</c> talks to the console service of that folder instead, verified as the same user.
    /// </summary>
    public static PrintBridgeServiceClientOptions ForCurrentBuild()
    {
        if (PrintBridgePaths.IsIsolatedDevelopmentRoot)
        {
            var endpoint = PrintBridgeServiceEndpoint.ForDataRoot(PrintBridgePaths.ProgramDataRoot);
            return new PrintBridgeServiceClientOptions(endpoint.PipeName, new CurrentUserServerVerifier());
        }

        var production = PrintBridgeServiceEndpoint.Production;
        return new PrintBridgeServiceClientOptions(production.PipeName, new ServiceControlManagerVerifier(production.ServiceName));
    }
}
