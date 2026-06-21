using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

public sealed class PrintBridgeSetupSessionService : IPrintBridgeSetupSessionService
{
    /// <summary>Default lifetime of an automatic setup session (~5 minutes).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly CentralDbContext _centralDb;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly ILogger<PrintBridgeSetupSessionService> _logger;

    public PrintBridgeSetupSessionService(
        CentralDbContext centralDb,
        IPrintBridgeDeviceManagementService devices,
        ILogger<PrintBridgeSetupSessionService> logger)
    {
        _centralDb = centralDb;
        _devices = devices;
        _logger = logger;
    }

    public async Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(
        Guid tenantId,
        Guid? deviceId,
        string serverUrl,
        string? defaultDeviceName,
        bool confirmReplaceActiveToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentException("Server URL is required.", nameof(serverUrl));

        var (resolvedDeviceId, deviceName) = await ResolveDeviceAsync(
                tenantId,
                deviceId,
                defaultDeviceName,
                confirmReplaceActiveToken,
                ct)
            .ConfigureAwait(false);

        var rawCode = PrintBridgeSetupCode.Generate();
        var now = DateTime.UtcNow;

        var session = new PrintBridgeSetupSession
        {
            TenantId = tenantId,
            PrintBridgeDeviceId = resolvedDeviceId,
            CodeHash = PrintBridgeSetupCode.Hash(rawCode),
            ServerUrl = serverUrl.Trim(),
            ExpiresAtUtc = now.Add(DefaultLifetime),
            CreatedAt = now,
            UpdatedAt = now
        };

        _centralDb.PrintBridgeSetupSessions.Add(session);
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        // Never log the raw code.
        _logger.LogInformation(
            "Print Bridge setup session created. SessionId={SessionId}, TenantId={TenantId}, DeviceId={DeviceId}",
            session.Id,
            tenantId,
            resolvedDeviceId);

        return new PrintBridgeSetupSessionCreated(session.Id, rawCode, session.ExpiresAtUtc, resolvedDeviceId, deviceName);
    }

    public async Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(string rawCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return null;

        string codeHash;
        try
        {
            codeHash = PrintBridgeSetupCode.Hash(rawCode);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        var session = await _centralDb.PrintBridgeSetupSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CodeHash == codeHash, ct)
            .ConfigureAwait(false);

        // Generic rejection: do not reveal whether a device/tenant/session exists.
        if (session is null)
            return null;

        if (session.ExpiresAtUtc <= now || session.ExchangedAtUtc != null ||
            session.CompletedAtUtc != null || session.FailedAtUtc != null)
            return null;

        var completionCredential = PrintBridgeSetupCode.Generate();
        var completionHash = PrintBridgeSetupCode.Hash(completionCredential);

        // Atomic compare-and-set: only the first concurrent exchange succeeds.
        var marked = await _centralDb.PrintBridgeSetupSessions
            .Where(s => s.Id == session.Id
                && s.ExchangedAtUtc == null
                && s.CompletedAtUtc == null
                && s.FailedAtUtc == null
                && s.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.ExchangedAtUtc, now)
                .SetProperty(s => s.CompletionCredentialHash, completionHash)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        if (marked != 1)
            return null;

        // Token is stored only as a hash, so regenerate to obtain a usable raw token for this device.
        GeneratePrintBridgeTokenResult tokenResult;
        try
        {
            tokenResult = await _devices.RegenerateTokenAsync(session.TenantId, session.PrintBridgeDeviceId, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await MarkFailedAsync(session.Id, "device_unavailable", ct).ConfigureAwait(false);
            return null;
        }

        _logger.LogInformation(
            "Print Bridge setup code exchanged. SessionId={SessionId}, DeviceId={DeviceId}",
            session.Id,
            session.PrintBridgeDeviceId);

        return new PrintBridgeSetupExchangeResult(
            session.Id,
            session.ServerUrl,
            tokenResult.RawToken,
            tokenResult.DeviceName,
            completionCredential);
    }

    public async Task<bool> CompleteAsync(
        Guid sessionId,
        string completionCredential,
        bool connectionVerified,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(completionCredential))
            return false;

        // No-tracking read so a fresh DB row is seen even if a prior call in the same
        // context used ExecuteUpdate (which does not refresh tracked entities).
        var session = await _centralDb.PrintBridgeSetupSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);

        if (session is null || string.IsNullOrWhiteSpace(session.CompletionCredentialHash))
            return false;

        if (!PrintBridgeSetupCode.Verify(completionCredential, session.CompletionCredentialHash))
            return false;

        if (session.CompletedAtUtc != null)
            return true; // idempotent

        if (session.FailedAtUtc != null)
            return false;

        var now = DateTime.UtcNow;
        var updated = await _centralDb.PrintBridgeSetupSessions
            .Where(s => s.Id == sessionId && s.CompletedAtUtc == null && s.FailedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.CompletedAtUtc, now)
                .SetProperty(s => s.ConnectionVerified, connectionVerified)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        if (updated != 1)
            return false;

        _logger.LogInformation(
            "Print Bridge setup session completed. SessionId={SessionId}, ConnectionVerified={ConnectionVerified}",
            sessionId,
            connectionVerified);

        return true;
    }

    public async Task<PrintBridgeSetupStatusDto?> GetStatusAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        var session = await _centralDb.PrintBridgeSetupSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (session is null)
            return null;

        return new PrintBridgeSetupStatusDto(
            session.Id,
            ResolveStatus(session, DateTime.UtcNow),
            session.ExpiresAtUtc,
            session.ConnectionVerified);
    }

    private async Task<(Guid DeviceId, string DeviceName)> ResolveDeviceAsync(
        Guid tenantId,
        Guid? deviceId,
        string? defaultDeviceName,
        bool confirmReplaceActiveToken,
        CancellationToken ct)
    {
        var devices = await _devices.ListDevicesAsync(tenantId, ct).ConfigureAwait(false);

        if (deviceId.HasValue)
        {
            var match = devices.FirstOrDefault(d => d.Id == deviceId.Value && d.IsActive);
            if (match is null)
                throw new InvalidOperationException("Print Bridge device not found or inactive.");
            if (!confirmReplaceActiveToken)
                throw new PrintBridgeSetupTokenReplacementConfirmationRequiredException();
            return (match.Id, match.Name);
        }

        var active = devices.FirstOrDefault(d => d.IsActive);
        if (active is not null)
        {
            if (!confirmReplaceActiveToken)
                throw new PrintBridgeSetupTokenReplacementConfirmationRequiredException();
            return (active.Id, active.Name);
        }

        var created = await _devices
            .CreateDeviceAsync(tenantId, string.IsNullOrWhiteSpace(defaultDeviceName) ? "Print Bridge" : defaultDeviceName!, ct)
            .ConfigureAwait(false);
        return (created.DeviceId, created.DeviceName);
    }

    private async Task MarkFailedAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _centralDb.PrintBridgeSetupSessions
            .Where(s => s.Id == sessionId && s.CompletedAtUtc == null && s.FailedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.FailedAtUtc, now)
                .SetProperty(s => s.FailureReason, reason)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    private static PrintBridgeSetupSessionStatus ResolveStatus(PrintBridgeSetupSession session, DateTime nowUtc) =>
        session.CompletedAtUtc != null ? PrintBridgeSetupSessionStatus.Completed
        : session.FailedAtUtc != null ? PrintBridgeSetupSessionStatus.Failed
        : session.ExpiresAtUtc <= nowUtc ? PrintBridgeSetupSessionStatus.Expired
        : session.ExchangedAtUtc != null ? PrintBridgeSetupSessionStatus.Exchanged
        : PrintBridgeSetupSessionStatus.Pending;
}
