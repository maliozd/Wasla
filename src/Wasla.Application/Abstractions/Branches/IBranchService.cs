namespace Wasla.Application.Abstractions.Branches;

public interface IBranchService
{
    Task<IReadOnlyList<BranchResult>> GetListAsync(Guid customerId, CancellationToken ct);
    Task<Guid> CreateAsync(Guid customerId, CreateBranchCommand command, CancellationToken ct);
}

