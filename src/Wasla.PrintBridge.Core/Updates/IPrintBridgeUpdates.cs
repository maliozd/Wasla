using Wasla.PrintBridge.Ipc;

namespace Wasla.PrintBridge.Updates;

/// <summary>
/// What the app may do about updates (WAS-63): read the update state the service reports, ask for the newer release, start
/// an attempt and cancel it. The service owns everything else (docs/integrations/print-bridge-updates.md); the app's own
/// work is the confirmation, a last check of the staged setup and starting it.
/// </summary>
public interface IPrintBridgeUpdates
{
    /// <summary>The update state of the last service state, or null while the service cannot be reached.</summary>
    IpcUpdate? CurrentUpdate { get; }

    /// <summary>The installed version and the newer release the server offers, if any.</summary>
    Task<IpcUpdateCheckResult> CheckForUpdateAsync(CancellationToken ct);

    /// <summary>Starts an attempt to update to <paramref name="version"/>; progress and result arrive in <see cref="CurrentUpdate"/>.</summary>
    Task<Guid> PrepareUpdateAsync(string version, CancellationToken ct);

    /// <summary>Cancels <paramref name="attemptId"/> unless it is finished or being committed.</summary>
    Task CancelUpdateAsync(Guid attemptId, CancellationToken ct);
}
