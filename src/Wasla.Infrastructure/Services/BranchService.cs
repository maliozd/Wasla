using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Branches;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class BranchService : IBranchService
{
    private readonly ITenantDbContextFactory _dbFactory;

    public BranchService(ITenantDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<BranchResult>> GetListAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        return await db.Branches.AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new BranchResult(b.Id, b.Name, b.Address, b.IsActive))
            .ToListAsync(ct);
    }

    public async Task<Guid> CreateAsync(Guid customerId, CreateBranchCommand command, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var now = DateTime.UtcNow;

        var entity = new Wasla.Domain.Entities.Customer.Branch
        {
            Name = (command.Name ?? string.Empty).Trim(),
            Address = (command.Address ?? string.Empty).Trim(),
            IsActive = command.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Branches.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }
}

