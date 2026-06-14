using Microsoft.EntityFrameworkCore;
using FluentValidation;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Domain.Entities.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class PlatformConnectionService : IPlatformConnectionService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ISecretManager _secret;
    private readonly IValidator<CreatePlatformConnectionCommand> _validator;

    public PlatformConnectionService(
        ITenantDbContextFactory dbFactory,
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

    public async Task<PlatformConnectionDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        return await db.PlatformConnections.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PlatformConnectionDetailResult(
                p.Id,
                p.Platform,
                p.StoreId,
                p.IsActive,
                p.SyncIntervalSeconds,
                p.SupplierId,
                p.ExecutorEmail))
            .FirstOrDefaultAsync(ct);
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
                ErrorMessage = null
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
            SupplierId = string.IsNullOrWhiteSpace(command.SupplierId) ? null : command.SupplierId.Trim(),
            ExecutorEmail = string.IsNullOrWhiteSpace(command.ExecutorEmail) ? null : command.ExecutorEmail.Trim(),
            EncryptedApiKey = encKey,
            EncryptedApiSecret = encSecret,
            EncryptionKeyVersion = keyVer,
            IsActive = command.IsActive,
            // Internal default: tenant UI must not control sync interval.
            // TODO: Move SyncIntervalSeconds management to central admin/internal configuration if needed.
            SyncIntervalSeconds = 30,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PlatformConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return new CreatePlatformConnectionResult { Succeeded = true, Id = entity.Id };
    }

    public async Task<CreatePlatformConnectionResult> UpdateAsync(Guid customerId, Guid id, UpdatePlatformConnectionCommand command, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var entity = await db.PlatformConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null)
        {
            return new CreatePlatformConnectionResult { Succeeded = false, ErrorCode = "NotFound", ErrorMessage = "Not found." };
        }

        var storeId = (command.StoreId ?? string.Empty).Trim();
        var exists = await db.PlatformConnections
            .AnyAsync(p => p.Id != id && p.Platform == command.Platform && p.StoreId == storeId, ct);
        if (exists)
        {
            return new CreatePlatformConnectionResult
            {
                Succeeded = false,
                ErrorCode = "Duplicate",
                ErrorMessage = null
            };
        }

        entity.StoreId = storeId;
        entity.IsActive = command.IsActive;

        // Tenant UI must not be able to change provider/internal knobs. API/internal callers can still opt-in by sending values.
        if (command.SyncIntervalSeconds.HasValue)
            entity.SyncIntervalSeconds = command.SyncIntervalSeconds.Value;

        if (command.SupplierId is not null)
            entity.SupplierId = string.IsNullOrWhiteSpace(command.SupplierId) ? null : command.SupplierId.Trim();

        if (command.ExecutorEmail is not null)
            entity.ExecutorEmail = string.IsNullOrWhiteSpace(command.ExecutorEmail) ? null : command.ExecutorEmail.Trim();

        entity.UpdatedAt = DateTime.UtcNow;

        // Secrets: never return existing secrets; if omitted, keep existing; if provided, replace only that value.
        var keyVerMax = entity.EncryptionKeyVersion;

        if (!string.IsNullOrWhiteSpace(command.ApiKey))
        {
            var (encKey, keyVer) = await _secret.EncryptAsync(command.ApiKey, ct);
            entity.EncryptedApiKey = encKey;
            keyVerMax = Math.Max(keyVerMax, keyVer);
        }

        if (!string.IsNullOrWhiteSpace(command.ApiSecret))
        {
            var (encSecret, keyVer) = await _secret.EncryptAsync(command.ApiSecret, ct);
            entity.EncryptedApiSecret = encSecret;
            keyVerMax = Math.Max(keyVerMax, keyVer);
        }

        entity.EncryptionKeyVersion = keyVerMax;

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

