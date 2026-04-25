using Microsoft.EntityFrameworkCore;
using FluentValidation;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class PlatformConnectionService : IPlatformConnectionService
{
    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly ISecretManager _secret;
    private readonly IValidator<CreatePlatformConnectionCommand> _validator;

    public PlatformConnectionService(
        ICustomerDbContextFactory dbFactory,
        ISecretManager secret,
        IValidator<CreatePlatformConnectionCommand> validator)
    {
        _dbFactory = dbFactory;
        _secret = secret;
        _validator = validator;
    }

    public async Task<IReadOnlyList<PlatformConnectionResult>> GetListAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        return await db.PlatformConnections.AsNoTracking()
            .OrderBy(p => p.Platform)
            .ThenBy(p => p.StoreId)
            .Select(p => new PlatformConnectionResult(
                p.Id,
                p.Platform,
                p.StoreId,
                p.IsActive,
                p.LastSyncAttempt,
                p.LastSuccessfulSync,
                p.ConsecutiveFailures,
                p.CircuitOpenUntil,
                p.SyncIntervalSeconds))
            .ToListAsync(ct);
    }

    public async Task<CreatePlatformConnectionResult> CreateAsync(Guid customerId, CreatePlatformConnectionCommand command, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            return new CreatePlatformConnectionResult
            {
                Succeeded = false,
                ErrorCode = "Validation",
                ErrorMessage = validation.Errors.FirstOrDefault()?.ErrorMessage ?? "Validation failed."
            };
        }

        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var storeId = (command.StoreId ?? string.Empty).Trim();

        var exists = await db.PlatformConnections
            .AnyAsync(p => p.Platform == command.Platform && p.StoreId == storeId, ct);
        if (exists)
        {
            return new CreatePlatformConnectionResult
            {
                Succeeded = false,
                ErrorCode = "Duplicate",
                ErrorMessage = "Bu platform ve mağaza kodu zaten tanımlı."
            };
        }

        var (encKey, keyVer1) = await _secret.EncryptAsync(command.ApiKey, ct);
        var (encSecret, keyVer2) = await _secret.EncryptAsync(command.ApiSecret, ct);
        var keyVer = Math.Max(keyVer1, keyVer2);

        var now = DateTime.UtcNow;
        var entity = new OrderHub.Domain.Entities.Customer.PlatformConnection
        {
            Platform = command.Platform,
            StoreId = storeId,
            EncryptedApiKey = encKey,
            EncryptedApiSecret = encSecret,
            EncryptionKeyVersion = keyVer,
            IsActive = command.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PlatformConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return new CreatePlatformConnectionResult { Succeeded = true, Id = entity.Id };
    }

    public async Task<bool> SetActiveAsync(Guid customerId, Guid id, bool isActive, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var entity = await db.PlatformConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return false;

        entity.IsActive = isActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

