using System.Data;
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
    private readonly IPrintBridgeSetupTenantLock _tenantLock;
    private readonly ILogger<PrintBridgeSetupSessionService> _logger;

    public PrintBridgeSetupSessionService(
        CentralDbContext centralDb,
        IPrintBridgeDeviceManagementService devices,
        IPrintBridgeSetupTenantLock tenantLock,
        ILogger<PrintBridgeSetupSessionService> logger)
    {
        _centralDb = centralDb;
        _devices = devices;
        _tenantLock = tenantLock;
        _logger = logger;
    }

    public async Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(
        Guid tenantId,
        PrintBridgeSetupMode setupMode,
        Guid? deviceId,
        string serverUrl,
        string? defaultDeviceName,
        bool confirmReplaceActiveToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentException("Server URL is required.", nameof(serverUrl));

        // For ReconnectExistingDevice: validate and bind device at session creation.
        // For NewDevice: PrintBridgeDeviceId remains null until exchange.
        Guid? resolvedDeviceId = null;
        string? resolvedDeviceName = null;

        if (setupMode == PrintBridgeSetupMode.ReconnectExistingDevice)
        {
            var (id, name) = await ResolveSelectedDeviceAsync(tenantId, deviceId, confirmReplaceActiveToken, ct)
                .ConfigureAwait(false);
            resolvedDeviceId = id;
            resolvedDeviceName = name;
        }

        var rawCode = PrintBridgeSetupCode.Generate();
        var now = DateTime.UtcNow;

        var session = new PrintBridgeSetupSession
        {
            TenantId = tenantId,
            PrintBridgeDeviceId = resolvedDeviceId,
            SetupMode = ToPersistedSetupMode(setupMode),
            CodeHash = PrintBridgeSetupCode.Hash(rawCode),
            ServerUrl = serverUrl.Trim(),
            ExpiresAtUtc = now.Add(DefaultLifetime),
            CreatedAt = now,
            UpdatedAt = now
        };

        _centralDb.PrintBridgeSetupSessions.Add(session);
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Print Bridge setup session created. SessionId={SessionId}, TenantId={TenantId}, Mode={Mode}, DeviceId={DeviceId}",
            session.Id,
            tenantId,
            setupMode,
            resolvedDeviceId);

        return new PrintBridgeSetupSessionCreated(session.Id, rawCode, session.ExpiresAtUtc, resolvedDeviceId, resolvedDeviceName);
    }

    public async Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(
        string rawCode,
        PrintBridgeSetupClientInfo clientInfo,
        CancellationToken ct)
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

        if (!TryParsePersistedSetupMode(session.SetupMode, out var persistedMode))
        {
            await MarkFailedAsync(session.Id, "invalid_setup_mode", ct).ConfigureAwait(false);

            _logger.LogWarning(
                "Print Bridge setup exchange rejected because persisted setup mode is invalid. " +
                "SessionId={SessionId}, TenantId={TenantId}, SetupMode={SetupMode}",
                session.Id,
                session.TenantId,
                session.SetupMode);

            return null;
        }

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

        string rawToken;
        string deviceName;

        if (persistedMode == PrintBridgeSetupMode.NewDevice)
        {
            var deviceResult = await CreateAndBindNewDeviceAsync(session, clientInfo, ct)
                .ConfigureAwait(false);

            if (deviceResult is null)
                return null; // quota exceeded or creation failed; session already marked failed

            (rawToken, deviceName) = deviceResult.Value;
        }
        else
        {
            // ReconnectExistingDevice: device was bound at session creation.
            if (!session.PrintBridgeDeviceId.HasValue)
            {
                await MarkFailedAsync(session.Id, "no_device_bound", ct).ConfigureAwait(false);
                return null;
            }

            GeneratePrintBridgeTokenResult tokenResult;
            try
            {
                tokenResult = await _devices
                    .RegenerateTokenAsync(session.TenantId, session.PrintBridgeDeviceId.Value, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await MarkFailedAsync(session.Id, "device_unavailable", ct).ConfigureAwait(false);
                return null;
            }

            rawToken = tokenResult.RawToken;
            deviceName = tokenResult.DeviceName;
        }

        _logger.LogInformation(
            "Print Bridge setup code exchanged. SessionId={SessionId}, Mode={Mode}",
            session.Id,
            session.SetupMode);

        return new PrintBridgeSetupExchangeResult(
            session.Id,
            session.ServerUrl,
            rawToken,
            deviceName,
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

    /// <summary>
    /// Validates that the selected device belongs to the tenant and that the caller has confirmed
    /// token replacement before binding it to a ReconnectExistingDevice session.
    /// </summary>
    private async Task<(Guid DeviceId, string DeviceName)> ResolveSelectedDeviceAsync(
        Guid tenantId,
        Guid? deviceId,
        bool confirmReplaceActiveToken,
        CancellationToken ct)
    {
        if (!deviceId.HasValue)
            throw new PrintBridgeSetupDeviceSelectionRequiredException();

        var device = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId.Value && d.TenantId == tenantId && d.RemovedAtUtc == null, ct)
            .ConfigureAwait(false);

        if (device is null)
            throw new PrintBridgeSetupDeviceNotFoundException();

        if (!confirmReplaceActiveToken)
            throw new PrintBridgeSetupTokenReplacementConfirmationRequiredException();

        return (device.Id, device.Name);
    }

    /// <summary>
    /// Atomically checks quota, creates a new PrintBridgeDevice, generates its token, and
    /// binds the session to it. Returns null and marks the session as failed if quota is exceeded
    /// or creation fails; in that case no device record is persisted.
    /// </summary>
    private async Task<(string RawToken, string DeviceName)?> CreateAndBindNewDeviceAsync(
        PrintBridgeSetupSession session,
        PrintBridgeSetupClientInfo clientInfo,
        CancellationToken ct)
    {
        string? failedReason = null;

        await using (var tx = await _centralDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false))
        {
            try
            {
                var appLockResult = await _tenantLock.AcquireNewDeviceExchangeLockAsync(session.TenantId, ct)
                    .ConfigureAwait(false);
                if (appLockResult < 0)
                {
                    failedReason = "tenant_lock_unavailable";
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Print Bridge new-device exchange could not acquire tenant-scoped database lock. " +
                        "SessionId={SessionId}, TenantId={TenantId}, LockResult={LockResult}",
                        session.Id,
                        session.TenantId,
                        appLockResult);
                }
                else
                {
                    // Quota check inside the transaction so concurrent exchanges cannot both succeed when at limit.
                    var activeCount = await _centralDb.PrintBridgeDevices
                        .CountAsync(d => d.TenantId == session.TenantId && d.IsActive && d.RemovedAtUtc == null, ct)
                        .ConfigureAwait(false);

                    if (activeCount >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount)
                    {
                        failedReason = "quota_exceeded";
                        await tx.RollbackAsync(ct).ConfigureAwait(false);

                        _logger.LogWarning(
                            "Print Bridge new-device exchange rejected: active device quota exceeded. " +
                            "SessionId={SessionId}, TenantId={TenantId}, ActiveCount={ActiveCount}",
                            session.Id, session.TenantId, activeCount);
                    }
                    else
                    {
                        var deviceName = NormalizeDeviceName(clientInfo.MachineName);
                        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
                        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
                        var now = DateTime.UtcNow;

                        var device = new PrintBridgeDevice
                        {
                            TenantId = session.TenantId,
                            Name = deviceName,
                            MachineName = string.IsNullOrWhiteSpace(clientInfo.MachineName)
                                ? null : Truncate(clientInfo.MachineName.Trim(), 200),
                            AppVersion = string.IsNullOrWhiteSpace(clientInfo.AppVersion)
                                ? null : Truncate(clientInfo.AppVersion.Trim(), 100),
                            PrinterName = string.IsNullOrWhiteSpace(clientInfo.PrinterName)
                                ? null : Truncate(clientInfo.PrinterName.Trim(), 200),
                            TokenHash = tokenHash,
                            IsActive = true,
                            CreatedAt = now,
                            UpdatedAt = now
                        };

                        _centralDb.PrintBridgeDevices.Add(device);
                        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

                        // Bind the session to the newly created device.
                        await _centralDb.PrintBridgeSetupSessions
                            .Where(s => s.Id == session.Id)
                            .ExecuteUpdateAsync(set => set
                                .SetProperty(s => s.PrintBridgeDeviceId, device.Id)
                                .SetProperty(s => s.UpdatedAt, now), ct)
                            .ConfigureAwait(false);

                        await tx.CommitAsync(ct).ConfigureAwait(false);

                        _logger.LogInformation(
                            "New Print Bridge device created and bound via setup exchange. " +
                            "DeviceId={DeviceId}, SessionId={SessionId}, TenantId={TenantId}, DeviceName={DeviceName}",
                            device.Id, session.Id, session.TenantId, deviceName);

                        return (rawToken, deviceName);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                failedReason = "device_creation_failed";
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogError(
                    ex,
                    "Print Bridge new-device exchange failed while creating and binding a device. " +
                    "SessionId={SessionId}, TenantId={TenantId}",
                    session.Id,
                    session.TenantId);
            }
        }

        if (failedReason is not null)
            await MarkFailedAsync(session.Id, failedReason, ct).ConfigureAwait(false);

        return null;
    }

    private static string ToPersistedSetupMode(PrintBridgeSetupMode setupMode) => setupMode switch
    {
            PrintBridgeSetupMode.NewDevice => PrintBridgeSetupModeValues.NewDevice,
            PrintBridgeSetupMode.ReconnectExistingDevice => PrintBridgeSetupModeValues.ReconnectExisting,
            _ => throw new InvalidOperationException("Unsupported Print Bridge setup mode.")
    };

    private static bool TryParsePersistedSetupMode(string? value, out PrintBridgeSetupMode setupMode)
    {
        if (string.Equals(value, PrintBridgeSetupModeValues.NewDevice, StringComparison.Ordinal))
        {
            setupMode = PrintBridgeSetupMode.NewDevice;
            return true;
        }

        if (string.Equals(value, PrintBridgeSetupModeValues.ReconnectExisting, StringComparison.Ordinal))
        {
            setupMode = PrintBridgeSetupMode.ReconnectExistingDevice;
            return true;
        }

        setupMode = default;
        return false;
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

    private static string NormalizeDeviceName(string? deviceName)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Print Bridge";
        return Truncate(name, 200);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
