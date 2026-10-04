namespace Wasla.Application.Abstractions.Setup;

/// <summary>
/// Reads the tenant's operational mode (see <see cref="Wasla.Domain.Enums.TenantOperationalMode"/>) together with the
/// automation settings it governs, so screens can show what is configured and what is effective; the Live Screen poll
/// (<c>/orders/live-data</c>) uses it for the settings menu's automation indicators. It is tenant state,
/// not a user's guided-setup choice. There is deliberately no write here: Setup → Live happens only together with a
/// user completing or skipping guided setup (<c>IGuidedSetupService</c>), and only provisioning and the Development
/// tenant reset write Setup.
/// </summary>
public interface ITenantOperationalModeService
{
    /// <summary>
    /// The mode and the saved automation settings, from one read of the settings row; a tenant without a settings row
    /// has <see cref="TenantAutomationStatus.WithoutSettings"/>. Read-only.
    /// </summary>
    Task<TenantAutomationStatus> GetAutomationStatusAsync(Guid tenantId, CancellationToken ct);
}
