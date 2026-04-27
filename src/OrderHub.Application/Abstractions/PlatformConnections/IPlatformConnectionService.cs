namespace OrderHub.Application.Abstractions.PlatformConnections;

public interface IPlatformConnectionService
{
    Task<IReadOnlyList<PlatformConnectionResult>> GetListAsync(Guid customerId, CancellationToken ct);
    Task<PlatformConnectionDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);
    Task<CreatePlatformConnectionResult> CreateAsync(Guid customerId, CreatePlatformConnectionCommand command, CancellationToken ct);
    Task<CreatePlatformConnectionResult> UpdateAsync(Guid customerId, Guid id, UpdatePlatformConnectionCommand command, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid customerId, Guid id, bool isActive, CancellationToken ct);
}

