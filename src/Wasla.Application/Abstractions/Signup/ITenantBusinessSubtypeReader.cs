namespace Wasla.Application.Abstractions.Signup;

public interface ITenantBusinessSubtypeReader
{
    /// <summary>
    /// Returns null when the tenant has no pending registration.
    /// Returns the selected subtype codes, which may be empty, when a registration exists.
    /// </summary>
    Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct);
}
