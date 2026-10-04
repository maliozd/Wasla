namespace Wasla.Application.Abstractions.Tours;

public interface IUserProductTourService
{
    Task<bool> IsCompletedAsync(Guid tenantId, Guid userId, string tourKey, CancellationToken ct);

    /// <summary>
    /// Records completion for this user only. A second call keeps the original timestamp.
    /// Returns false when the key is not storable or the write fails.
    /// </summary>
    Task<bool> CompleteAsync(Guid tenantId, Guid userId, string tourKey, CancellationToken ct);
}
