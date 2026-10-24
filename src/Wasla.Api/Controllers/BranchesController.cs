using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Branches;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Api.Controllers;

// Branch settings, as on Web (BranchesController, CanManageTenantSettings).
[ApiController]
[Route("api/branches")]
[Authorize(Policy = WaslaTenantPolicies.CanManageTenantSettings)]
public sealed class BranchesController : ControllerBase
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantDbContextFactory _customerDbFactory;
    private readonly IBranchService _branches;
    private readonly IValidator<CreateBranchCommand> _createValidator;

    public BranchesController(
        ICurrentTenantService currentTenant,
        ITenantDbContextFactory customerDbFactory,
        IBranchService branches,
        IValidator<CreateBranchCommand> createValidator)
    {
        _currentTenant = currentTenant;
        _customerDbFactory = customerDbFactory;
        _branches = branches;
        _createValidator = createValidator;
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

        // The same command, validator and service as Web.
        var cmd = new CreateBranchCommand(request.Name ?? string.Empty, request.Address ?? string.Empty, request.IsActive);
        var validation = await _createValidator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            var modelState = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
            foreach (var e in validation.Errors)
            {
                modelState.AddModelError(e.PropertyName, e.ErrorMessage);
            }
            return ValidationProblem(modelState);
        }

        var id = await _branches.CreateAsync(tenant.Id, cmd, ct);

        await using var db = await _customerDbFactory.CreateAsync(tenant.Id, ct);
        var created = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Address,
                b.IsActive,
                b.CreatedAt,
                b.UpdatedAt
            })
            .SingleAsync(ct);

        return Created($"/api/branches/{id}", created);
    }
}
