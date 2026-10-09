using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Contracts.Enums;
using Wasla.Contracts.PlatformConnections;
using FoodPlatformDomain = Wasla.Domain.Enums.FoodPlatform;
using ContractPlatformConnectionDto = Wasla.Contracts.PlatformConnections.PlatformConnectionDto;

namespace Wasla.Api.Controllers;

// Platform connection settings, as on Web (PlatformConnectionsController, CanManageTenantSettings).
[ApiController]
[Route("api/platform-connections")]
[Authorize(Policy = WaslaTenantPolicies.CanManageTenantSettings)]
public sealed class PlatformConnectionsController : ControllerBase
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IPlatformConnectionService _connections;
    private readonly IValidator<CreatePlatformConnectionCommand> _createValidator;

    public PlatformConnectionsController(
        ICurrentTenantService currentTenant,
        IPlatformConnectionService connections,
        IValidator<CreatePlatformConnectionCommand> createValidator)
    {
        _currentTenant = currentTenant;
        _connections = connections;
        _createValidator = createValidator;
    }

    public sealed record CreatePlatformConnectionRequest(
        FoodPlatformDomain Platform,
        string StoreId,
        string ApiKey,
        string ApiSecret,
        bool IsActive = true,
        string? SupplierId = null,
        string? ExecutorEmail = null);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ContractPlatformConnectionDto>>> GetList(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var list = await _connections.GetListAsync(tenant.Id, ct);
        var mapped = list.Select(p => new ContractPlatformConnectionDto(
            p.Id,
            (FoodPlatformDto)(int)p.Platform,
            p.StoreId,
            p.IsActive,
            p.LastSyncAttemptUtc,
            p.LastSuccessfulSyncUtc,
            p.ConsecutiveFailures,
            p.CircuitOpenUntilUtc,
            p.SyncIntervalSeconds)).ToList();

        return Ok(mapped);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePlatformConnectionRequest request, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var cmd = new CreatePlatformConnectionCommand(
            request.Platform,
            request.StoreId,
            request.ApiKey,
            request.ApiSecret,
            request.IsActive,
            request.SupplierId,
            request.ExecutorEmail);

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

        var result = await _connections.CreateAsync(tenant.Id, cmd, ct);
        if (!result.Succeeded)
        {
            return result.ErrorCode == "Duplicate"
                ? Conflict(new { message = result.ErrorMessage })
                : BadRequest(new { message = result.ErrorMessage });
        }

        return Created($"/api/platform-connections/{result.Id}", new { id = result.Id });
    }

    [HttpPatch("{id:guid}/active")]
    public async Task<IActionResult> SetActive([FromRoute] Guid id, [FromBody] UpdatePlatformConnectionActiveRequest request, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var ok = await _connections.SetActiveAsync(tenant.Id, id, request.IsActive, ct);
        return ok ? Ok() : NotFound();
    }
}

