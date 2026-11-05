namespace Wasla.Application.Abstractions.Branches;

public interface IBranchService
{
    Task<IReadOnlyList<BranchResult>> GetListAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> CreateAsync(Guid tenantId, CreateBranchCommand command, CancellationToken ct);
}

