namespace Wasla.Application.Abstractions.PlatformConnections;

public interface IPlatformConnectionService
{
    Task<IReadOnlyList<PlatformConnectionResult>> GetListAsync(Guid tenantId, CancellationToken ct);
    Task<PlatformConnectionDetailResult?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<CreatePlatformConnectionResult> CreateAsync(Guid tenantId, CreatePlatformConnectionCommand command, CancellationToken ct);
    Task<CreatePlatformConnectionResult> UpdateAsync(Guid tenantId, Guid id, UpdatePlatformConnectionCommand command, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid tenantId, Guid id, bool isActive, CancellationToken ct);

    /// <summary>
    /// Deletes the tenant's connection row with its stored credentials, so the Worker never loads it again and the
    /// platform can be connected anew. Orders, sync logs and integration errors are kept: none of them references the row
    /// through a foreign key. False when the tenant has no connection with this id (already removed, or never its own).
    /// </summary>
    Task<bool> RemoveAsync(Guid tenantId, Guid id, CancellationToken ct);
}

