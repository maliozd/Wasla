using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Contracts.Enums;
using OrderHub.Contracts.PlatformConnections;
using FoodPlatformDomain = OrderHub.Domain.Enums.FoodPlatform;
using ContractPlatformConnectionDto = OrderHub.Contracts.PlatformConnections.PlatformConnectionDto;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/platform-connections")]
[Authorize]
public sealed class PlatformConnectionsController : ControllerBase
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IPlatformConnectionService _connections;
    private readonly IValidator<CreatePlatformConnectionCommand> _createValidator;

    public PlatformConnectionsController(
        ICurrentCustomerService currentCustomer,
        IPlatformConnectionService connections,
        IValidator<CreatePlatformConnectionCommand> createValidator)
    {
        _currentCustomer = currentCustomer;
        _connections = connections;
        _createValidator = createValidator;
    }

    public sealed record CreatePlatformConnectionRequest(
        FoodPlatformDomain Platform,
        string StoreId,
        string ApiKey,
        string ApiSecret,
        bool IsActive = true);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ContractPlatformConnectionDto>>> GetList(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var list = await _connections.GetListAsync(customer.Id, ct);
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
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var cmd = new CreatePlatformConnectionCommand(
            request.Platform,
            request.StoreId,
            request.ApiKey,
            request.ApiSecret,
            request.IsActive);

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

        var result = await _connections.CreateAsync(customer.Id, cmd, ct);
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
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var ok = await _connections.SetActiveAsync(customer.Id, id, request.IsActive, ct);
        return ok ? Ok() : NotFound();
    }
}

