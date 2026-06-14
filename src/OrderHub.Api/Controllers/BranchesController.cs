using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Tenant;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/branches")]
[Authorize]
public sealed class BranchesController : ControllerBase
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantDbContextFactory _customerDbFactory;

    public BranchesController(ICurrentTenantService currentTenant, ITenantDbContextFactory customerDbFactory)
    {
        _currentTenant = currentTenant;
        _customerDbFactory = customerDbFactory;
    }

    public sealed record CreateBranchRequest(string Name, string? Address, bool IsActive = true);

    [HttpGet]
    public async Task<IActionResult> GetList(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        await using var db = await _customerDbFactory.CreateAsync(tenant.Id, ct);

        var branches = await db.Branches
            .AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Address,
                b.IsActive,
                b.CreatedAt,
                b.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(branches);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBranchRequest request, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        await using var db = await _customerDbFactory.CreateAsync(tenant.Id, ct);

        var entity = new OrderHub.Domain.Entities.Customer.Branch
        {
            Name = request.Name.Trim(),
            Address = request.Address?.Trim() ?? string.Empty,
            IsActive = request.IsActive
        };

        db.Branches.Add(entity);
        await db.SaveChangesAsync(ct);

        return Created($"/api/branches/{entity.Id}", new
        {
            entity.Id,
            entity.Name,
            entity.Address,
            entity.IsActive,
            entity.CreatedAt,
            entity.UpdatedAt
        });
    }
}

