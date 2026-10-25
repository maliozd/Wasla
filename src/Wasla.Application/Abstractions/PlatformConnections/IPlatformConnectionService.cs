namespace Wasla.Application.Abstractions.PlatformConnections;

public interface IPlatformConnectionService
{
    Task<IReadOnlyList<PlatformConnectionResult>> GetListAsync(Guid customerId, CancellationToken ct);
    Task<PlatformConnectionDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);
    Task<CreatePlatformConnectionResult> CreateAsync(Guid customerId, CreatePlatformConnectionCommand command, CancellationToken ct);
    Task<CreatePlatformConnectionResult> UpdateAsync(Guid customerId, Guid id, UpdatePlatformConnectionCommand command, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid customerId, Guid id, bool isActive, CancellationToken ct);

    /// <summary>
    /// Deletes the tenant's connection row with its stored credentials, so the Worker never loads it again and the
    /// platform can be connected anew. Orders, sync logs and integration errors are kept: none of them references the row
    /// through a foreign key. False when the tenant has no connection with this id (already removed, or never its own).
    /// </summary>
    Task<bool> RemoveAsync(Guid customerId, Guid id, CancellationToken ct);
}

