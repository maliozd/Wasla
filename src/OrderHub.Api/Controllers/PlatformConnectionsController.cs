using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/platform-connections")]
[Authorize]
public sealed class PlatformConnectionsController : ControllerBase
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly ICustomerDbContextFactory _customerDbFactory;
    private readonly ISecretManager _secretManager;

    public PlatformConnectionsController(
        ICurrentCustomerService currentCustomer,
        ICustomerDbContextFactory customerDbFactory,
        ISecretManager secretManager)
    {
        _currentCustomer = currentCustomer;
        _customerDbFactory = customerDbFactory;
        _secretManager = secretManager;
    }

    public sealed record CreatePlatformConnectionRequest(
        FoodPlatform Platform,
        string StoreId,
        string ApiKey,
        string ApiSecret,
        bool IsActive = true);

    public sealed record SetActiveRequest(bool IsActive);

    [HttpGet]
    public async Task<IActionResult> GetList(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        await using var db = (CustomerDbContext)await _customerDbFactory.CreateAsync(customer.Id, ct);

        var list = await db.PlatformConnections
            .AsNoTracking()
            .OrderBy(p => p.Platform)
            .ThenBy(p => p.StoreId)
            .Select(p => new
            {
                p.Id,
                p.Platform,
                p.StoreId,
                p.IsActive,
                p.EncryptionKeyVersion,
                p.LastSyncAttempt,
                p.LastSuccessfulSync,
                p.ConsecutiveFailures,
                p.CircuitOpenUntil,
                p.SyncIntervalSeconds,
                p.CreatedAt,
                p.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePlatformConnectionRequest request, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        if (string.IsNullOrWhiteSpace(request.StoreId)) return BadRequest("StoreId is required");
        if (string.IsNullOrWhiteSpace(request.ApiKey)) return BadRequest("ApiKey is required");
        if (string.IsNullOrWhiteSpace(request.ApiSecret)) return BadRequest("ApiSecret is required");

        await using var db = (CustomerDbContext)await _customerDbFactory.CreateAsync(customer.Id, ct);

        var (encKey, keyVer1) = await _secretManager.EncryptAsync(request.ApiKey, ct);
        var (encSecret, keyVer2) = await _secretManager.EncryptAsync(request.ApiSecret, ct);
        var keyVer = Math.Max(keyVer1, keyVer2);

        var entity = new OrderHub.Domain.Entities.Customer.PlatformConnection
        {
            Platform = request.Platform,
            StoreId = request.StoreId.Trim(),
            EncryptedApiKey = encKey,
            EncryptedApiSecret = encSecret,
            EncryptionKeyVersion = keyVer,
            IsActive = request.IsActive
        };

        db.PlatformConnections.Add(entity);
        await db.SaveChangesAsync(ct);

        // Never return encrypted secrets.
        return Created($"/api/platform-connections/{entity.Id}", new
        {
            entity.Id,
            entity.Platform,
            entity.StoreId,
            entity.IsActive,
            entity.EncryptionKeyVersion,
            entity.CreatedAt,
            entity.UpdatedAt
        });
    }

    [HttpPatch("{id:guid}/active")]
    public async Task<IActionResult> SetActive([FromRoute] Guid id, [FromBody] SetActiveRequest request, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        await using var db = (CustomerDbContext)await _customerDbFactory.CreateAsync(customer.Id, ct);

        var entity = await db.PlatformConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return NotFound();

        entity.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            entity.Id,
            entity.Platform,
            entity.StoreId,
            entity.IsActive,
            entity.UpdatedAt
        });
    }
}

