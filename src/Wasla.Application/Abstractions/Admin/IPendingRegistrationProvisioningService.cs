namespace Wasla.Application.Abstractions.Admin;

public enum ProvisioningOutcome
{
    Success = 0,
    AlreadyProvisioned = 1,
    NotFound = 2,
    NotEligible = 3,
    ValidationError = 4,
    Failed = 5
}

/// <summary>
/// Safe structured result from provisioning. Never contains connection strings, passwords, or raw secrets.
/// </summary>
public sealed class ProvisioningResult
{
    public ProvisioningOutcome Outcome { get; init; }

    /// <summary>True when the registration was successfully provisioned or was already provisioned with a valid Tenant.</summary>
    public bool IsSuccess => Outcome is ProvisioningOutcome.Success or ProvisioningOutcome.AlreadyProvisioned;

    /// <summary>Human-readable error or additional detail. Must not contain secrets.</summary>
    public string? Message { get; init; }

    public Guid? TenantId { get; init; }
    public string? TenantName { get; init; }
    public string? TenantDomain { get; init; }

    public static ProvisioningResult Success(Guid tenantId, string name, string domain) => new()
    {
        Outcome = ProvisioningOutcome.Success,
        TenantId = tenantId,
        TenantName = name,
        TenantDomain = domain
    };

    public static ProvisioningResult AlreadyProvisioned(Guid tenantId, string name, string domain) => new()
    {
        Outcome = ProvisioningOutcome.AlreadyProvisioned,
        TenantId = tenantId,
        TenantName = name,
        TenantDomain = domain,
        Message = "Already provisioned."
    };

    public static ProvisioningResult NotFound() => new()
    {
        Outcome = ProvisioningOutcome.NotFound,
        Message = "Pending registration not found."
    };

    public static ProvisioningResult NotEligible(string reason) => new()
    {
        Outcome = ProvisioningOutcome.NotEligible,
        Message = reason
    };

    public static ProvisioningResult ValidationError(string reason) => new()
    {
        Outcome = ProvisioningOutcome.ValidationError,
        Message = reason
    };

    public static ProvisioningResult Failure(string message) => new()
    {
        Outcome = ProvisioningOutcome.Failed,
        Message = message
    };
}

/// <summary>
/// Shared provisioning service used by both Wasla.Cli and the Central Admin web UI.
/// CLI may pass explicit SQL overrides; Web passes nulls to use configured defaults.
/// </summary>
public interface IPendingRegistrationProvisioningService
{
    /// <summary>
    /// Provisions the tenant for a paid pending registration.
    /// </summary>
    /// <param name="registrationId">The PendingRegistration to provision.</param>
    /// <param name="force">
    /// When true, retries provisioning even if the registration is already marked Provisioned
    /// but the tenant record is missing. Ignored if the registration is fully provisioned.
    /// </param>
    /// <param name="sqlServerOverride">
    /// Override the SQL Server instance from config. Pass null to use configured default.
    /// Used by Wasla.Cli when --sql-server is supplied.
    /// </param>
    /// <param name="sqlAuthOverride">
    /// Override the SQL auth from config ("trusted" or "sql:user:pass").
    /// Pass null to use configured default. Used by Wasla.Cli when --sql-auth is supplied.
    /// </param>
    /// <param name="panelLoginUrl">
    /// Optional already-built tenant login URL for panel-ready email. Web can pass a request-aware URL;
    /// CLI may pass null and let Infrastructure fall back to the registration primary domain.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProvisioningResult> ProvisionAsync(
        Guid registrationId,
        bool force = false,
        string? sqlServerOverride = null,
        string? sqlAuthOverride = null,
        string? panelLoginUrl = null,
        CancellationToken ct = default);
}
