namespace Wasla.Application.Abstractions.Orders;

/// <summary>
/// The signed-in employee's choice to confirm routine order actions (Approve and Mark ready). The tenant comes from
/// server-side resolution and the user from the validated session; a missing record means confirmations stay on.
/// </summary>
public interface IUserOrderActionPreferenceService
{
    Task<bool> GetConfirmRoutineActionsAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>Stores the choice and returns the value now stored.</summary>
    Task<bool> SetConfirmRoutineActionsAsync(Guid tenantId, Guid userId, bool confirmRoutineActions, CancellationToken ct);
}
