using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Wasla.PrintBridge.Ipc;

/// <summary>Who is on the other end of an accepted pipe connection (no user name, only the SID).</summary>
internal sealed record PrintBridgeIpcClientIdentity(SecurityIdentifier User, int SessionId, int ProcessId);

/// <summary>Decides, right after a client connected and before anything is read, whether it may use the service.</summary>
internal interface IPrintBridgeIpcClientAuthorizer
{
    /// <summary>The client's identity when it may connect; null when it must be disconnected.</summary>
    PrintBridgeIpcClientIdentity? Authorize(NamedPipeServerStream pipe, out string? refusal);
}

/// <summary>
/// The production rule (WAS-61): the client's token, read by identifying (not impersonating) it, must belong to an
/// interactive logon (<c>INTERACTIVE</c>) or to an enabled member of <c>Administrators</c>, and must never be a network
/// logon or anonymous. The pipe's access rules already deny <c>NETWORK</c>; this is the second check.
/// </summary>
internal sealed class WindowsTokenClientAuthorizer : IPrintBridgeIpcClientAuthorizer
{
    private const uint TokenQuery = 0x0008;

    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier Anonymous = new(WellKnownSidType.AnonymousSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public PrintBridgeIpcClientIdentity? Authorize(NamedPipeServerStream pipe, out string? refusal)
    {
        refusal = null;
        var processId = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : -1;
        var sessionId = GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? (int)session : -1;

        SafeAccessTokenHandle? token = null;
        try
        {
            token = ReadClientToken(pipe.SafePipeHandle);
            if (token is null)
            {
                refusal = "the client's token could not be identified";
                return null;
            }

            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var user = identity.User;
            var groups = identity.Groups?.OfType<SecurityIdentifier>().ToHashSet() ?? [];
            if (user is null || user == Anonymous || groups.Contains(Anonymous))
            {
                refusal = "anonymous client";
                return null;
            }

            if (groups.Contains(Network))
            {
                refusal = "network logon";
                return null;
            }

            if (!groups.Contains(Interactive) && !groups.Contains(Administrators))
            {
                refusal = "neither an interactive logon nor an administrator";
                return null;
            }

            return new PrintBridgeIpcClientIdentity(user, sessionId, processId);
        }
        finally
        {
            token?.Dispose();
        }
    }

    /// <summary>
    /// The client's token at the level it allowed (the app allows identification only). Impersonation lasts only for
    /// <c>OpenThreadToken</c>; if this thread cannot return to the service's own identity, the process ends rather than
    /// run anything as the client.
    /// </summary>
    private static SafeAccessTokenHandle? ReadClientToken(SafePipeHandle pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe))
            return null;

        try
        {
            return OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token) ? token : null;
        }
        finally
        {
            if (!RevertToSelf())
                Environment.FailFast("Wasla Print Bridge could not stop identifying a pipe client.");
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle hNamedPipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(IntPtr threadHandle, uint desiredAccess, bool openAsSelf, out SafeAccessTokenHandle tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);
}
