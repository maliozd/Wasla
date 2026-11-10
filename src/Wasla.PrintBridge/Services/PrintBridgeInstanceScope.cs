using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// The Windows user and session a Print Bridge process belongs to (WAS-92). The per-user instance lock and the setup
/// pipe are both named from these two values, so a second launch only ever looks for the instance of its own user in
/// its own session.
///
/// The names are not secret: any account can compute another user's SID. They keep accounts from colliding; they do
/// not authenticate anyone. Setup links are protected by checking the pipe's owner before anything is written
/// (<see cref="SetupInstanceChannel.TrySend(string, TimeSpan)"/>).
/// </summary>
internal sealed class PrintBridgeInstanceScope
{
    private const string InstanceMutexPrefix = @"Local\WaslaPrintBridge.Instance";

    public PrintBridgeInstanceScope(SecurityIdentifier user, int sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        User = user;
        SessionId = sessionId;
    }

    public SecurityIdentifier User { get; }

    public int SessionId { get; }

    /// <summary>
    /// <c>Local\</c> is this logon session's own namespace; the SID separates accounts that share a session
    /// (for example "Run as different user").
    /// </summary>
    public string InstanceMutexName => $"{InstanceMutexPrefix}.{User.Value}";

    /// <summary>Pipe names are machine-wide, so the pipe carries both the user's SID and the session id.</summary>
    public string SetupPipeName =>
        string.Create(CultureInfo.InvariantCulture, $"{SetupInstanceChannel.PipeNamePrefix}.{User.Value}.{SessionId}");

    public static PrintBridgeInstanceScope Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user.");
        using var process = Process.GetCurrentProcess();
        return new PrintBridgeInstanceScope(user, process.SessionId);
    }
}
