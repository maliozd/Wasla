using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

public sealed partial class PrintBridgeSetupSessionService : IPrintBridgeSetupSessionService
{
    /// <summary>Default lifetime of an automatic setup session (~5 minutes).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private const string FailureReasonTenantLockUnavailable = "tenant_lock_unavailable";
    private const string FailureReasonQuotaExceeded = "quota_exceeded";
    private const string FailureReasonDeviceCreationFailed = "device_creation_failed";

    private readonly CentralDbContext _centralDb;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly IPrintBridgeSetupTenantLock _tenantLock;
    private readonly IPrintBridgeActivePrintJobChecker _activePrintJobChecker;
    private readonly ILogger<PrintBridgeSetupSessionService> _logger;

    public PrintBridgeSetupSessionService(
        CentralDbContext centralDb,
        IPrintBridgeDeviceManagementService devices,
        IPrintBridgeSetupTenantLock tenantLock,
        IPrintBridgeActivePrintJobChecker activePrintJobChecker,
        ILogger<PrintBridgeSetupSessionService> logger)
    {
        _centralDb = centralDb;
        _devices = devices;
        _tenantLock = tenantLock;
        _activePrintJobChecker = activePrintJobChecker;
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

        if (!clientInfo.InstallationId.HasValue || clientInfo.InstallationId.Value == Guid.Empty)
        {
            await MarkFailedAsync(session.Id, "missing_installation_id", ct).ConfigureAwait(false);
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
                return null; // quota exceeded or creation failed (session marked failed), or the session ended while the lock was awaited (left as the reset or expiry left it)

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
            InstallationBinding binding;
            try
            {
                binding = await BindInstallationIdentityAsync(
                        session.TenantId,
                        session.PrintBridgeDeviceId.Value,
                        clientInfo.InstallationId.Value,
                        ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await MarkFailedAsync(session.Id, "device_unavailable", ct).ConfigureAwait(false);
                return null;
            }

            if (binding == InstallationBinding.RegisteredToAnotherTenant)
            {
                // Reconnecting would silently take this computer away from the other tenant (WAS-91); only the move
                // approved in Print Bridge with that device's own token may do that.
                await MarkFailedAsync(
                    session.Id,
                    PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant,
                    ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "Print Bridge reconnect exchange rejected: the installation is registered to another tenant. " +
                    "SessionId={SessionId}, TenantId={TenantId}",
                    session.Id,
                    session.TenantId);
                throw new PrintBridgeSetupInstallationRegisteredToAnotherTenantException();
            }

            if (binding != InstallationBinding.Bound)
            {
                await MarkFailedAsync(session.Id, "installation_identity_conflict", ct).ConfigureAwait(false);
                return null;
            }

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
            clientInfo.InstallationId.Value,
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
        // Completing a move also forgets the retired token's hash: the move can no longer be repeated (WAS-91).
        var updated = await _centralDb.PrintBridgeSetupSessions
            .Where(s => s.Id == sessionId && s.CompletedAtUtc == null && s.FailedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.CompletedAtUtc, now)
                .SetProperty(s => s.ConnectionVerified, connectionVerified)
                .SetProperty(s => s.SourceTokenHash, (string?)null)
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
            session.ConnectionVerified,
            session.FailureReason);
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
    /// or creation fails; in that case no device record is persisted. Also returns null, without marking the session,
    /// when the session ended (failed or expired) while the tenant lock was awaited.
    /// </summary>
    private async Task<(string RawToken, string DeviceName)?> CreateAndBindNewDeviceAsync(
        PrintBridgeSetupSession session,
        PrintBridgeSetupClientInfo clientInfo,
        CancellationToken ct)
    {
        string? failedReason = null;
        var installationId = clientInfo.InstallationId!.Value;

        await using (var tx = await _centralDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false))
        {
            try
            {
                // The installation lock first, then the tenant lock: the order every setup write uses (WAS-91).
                var appLockResult = await _tenantLock.AcquireInstallationLockAsync(installationId, ct)
                    .ConfigureAwait(false);
                if (appLockResult >= 0)
                {
                    appLockResult = await _tenantLock.AcquireNewDeviceExchangeLockAsync(session.TenantId, ct)
                        .ConfigureAwait(false);
                }

                if (appLockResult < 0)
                {
                    failedReason = FailureReasonTenantLockUnavailable;
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Print Bridge new-device exchange could not acquire tenant-scoped database lock. " +
                        "SessionId={SessionId}, TenantId={TenantId}, LockResult={LockResult}",
                        session.Id,
                        session.TenantId,
                        appLockResult);
                }
                else if (!await IsSessionStillExchangeableAsync(session.Id, ct).ConfigureAwait(false))
                {
                    // A development tenant reset (or expiry) ended the session while this exchange waited for the
                    // tenant lock. The reset already recorded its reason, so nothing is marked here.
                    await tx.RollbackAsync(ct).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Print Bridge new-device exchange stopped: the setup session ended before the device was created. " +
                        "SessionId={SessionId}, TenantId={TenantId}",
                        session.Id,
                        session.TenantId);
                }
                else
                {
                    var registration = await FindInstallationRegistrationAsync(
                            session.TenantId,
                            installationId,
                            ignoredDeviceIds: [],
                            ct)
                        .ConfigureAwait(false);

                    if (registration != InstallationRegistration.None)
                    {
                        // Registered in this tenant: the existing refusal. Registered to another tenant: a new device
                        // here would silently take the computer away from it (WAS-91); only the approved move may.
                        failedReason = registration == InstallationRegistration.SameTenant
                            ? PrintBridgeSetupFailureReasons.InstallationAlreadyRegistered
                            : PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant;
                        await tx.RollbackAsync(ct).ConfigureAwait(false);

                        _logger.LogWarning(
                            "Print Bridge new-device exchange rejected: installation identity is already registered. " +
                            "SessionId={SessionId}, TenantId={TenantId}, Registration={Registration}",
                            session.Id,
                            session.TenantId,
                            registration);
                    }
                    else
                    {
                        var activeCount = await CountActiveDevicesAsync(session.TenantId, ct).ConfigureAwait(false);
                        if (activeCount >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount)
                        {
                            failedReason = FailureReasonQuotaExceeded;
                            await tx.RollbackAsync(ct).ConfigureAwait(false);

                            _logger.LogWarning(
                                "Print Bridge new-device exchange rejected: active device quota exceeded. " +
                                "SessionId={SessionId}, TenantId={TenantId}, ActiveCount={ActiveCount}",
                                session.Id, session.TenantId, activeCount);
                        }
                        else
                        {
                            var created = await CreateDeviceAndBindSessionAsync(session, clientInfo, installationId, ct)
                                .ConfigureAwait(false);
                            await tx.CommitAsync(ct).ConfigureAwait(false);

                            _logger.LogInformation(
                                "New Print Bridge device created and bound via setup exchange. " +
                                "DeviceId={DeviceId}, SessionId={SessionId}, TenantId={TenantId}, DeviceName={DeviceName}",
                                created.DeviceId, session.Id, session.TenantId, created.DeviceName);

                            return (created.RawToken, created.DeviceName);
                        }
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
                failedReason = FailureReasonDeviceCreationFailed;
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
        {
            await MarkFailedAsync(session.Id, failedReason, ct).ConfigureAwait(false);

            if (string.Equals(
                    failedReason,
                    PrintBridgeSetupFailureReasons.InstallationAlreadyRegistered,
                    StringComparison.Ordinal))
            {
                throw new PrintBridgeSetupInstallationAlreadyRegisteredException();
            }

            if (string.Equals(
                    failedReason,
                    PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant,
                    StringComparison.Ordinal))
            {
                throw new PrintBridgeSetupInstallationRegisteredToAnotherTenantException();
            }
        }

        return null;
    }

    private async Task<(Guid DeviceId, string RawToken, string DeviceName)> CreateDeviceAndBindSessionAsync(
        PrintBridgeSetupSession session,
        PrintBridgeSetupClientInfo clientInfo,
        Guid installationId,
        CancellationToken ct)
    {
        var deviceName = NormalizeDeviceName(clientInfo.DeviceName, clientInfo.MachineName);
        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var device = new PrintBridgeDevice
        {
            TenantId = session.TenantId,
            Name = deviceName,
            InstallationId = installationId,
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

        // Bind the session to the newly created device, only while it is still unfinished and unbound.
        var bound = await _centralDb.PrintBridgeSetupSessions
            .Where(s => s.Id == session.Id
                && s.PrintBridgeDeviceId == null
                && s.CompletedAtUtc == null
                && s.FailedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.PrintBridgeDeviceId, device.Id)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        if (bound != 1)
            throw new InvalidOperationException("The Print Bridge setup session could not be bound to the new device.");

        return (device.Id, rawToken, deviceName);
    }

    /// <summary>
    /// Re-reads the session inside the new-device transaction, after the tenant lock is held. The development
    /// tenant reset fails unfinished sessions under the same lock, so a session it ended while this exchange waited
    /// is seen here, before any device or token exists.
    /// </summary>
    private async Task<bool> IsSessionStillExchangeableAsync(Guid sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await _centralDb.PrintBridgeSetupSessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId
                && s.ExchangedAtUtc != null
                && s.PrintBridgeDeviceId == null
                && s.CompletedAtUtc == null
                && s.FailedAtUtc == null
                && s.ExpiresAtUtc > now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Where else <paramref name="installationId"/> is registered: on a device that is not removed (active or passive),
    /// other than <paramref name="ignoredDeviceIds"/>. A registration in the session's own tenant wins over one elsewhere,
    /// so the existing same-tenant refusal keeps its reason. Checked across all tenants (WAS-91): the per-tenant unique
    /// index does not stop one computer from being registered to two tenants.
    /// <para>
    /// Every setup write that binds an installation holds its installation lock, so this read needs no key-range locks.
    /// On SQL Server it reads with <c>READCOMMITTEDLOCK</c> inside the serializable transaction: a range lock across all
    /// tenants' devices would make unrelated setups of different tenants block, and deadlock, each other.
    /// </para>
    /// </summary>
    private async Task<InstallationRegistration> FindInstallationRegistrationAsync(
        Guid tenantId,
        Guid installationId,
        Guid[] ignoredDeviceIds,
        CancellationToken ct)
    {
        var devices = _centralDb.Database.IsSqlServer()
            ? _centralDb.PrintBridgeDevices.FromSql(
                $"SELECT * FROM [PrintBridgeDevices] WITH (READCOMMITTEDLOCK) WHERE [InstallationId] = {installationId}")
            : _centralDb.PrintBridgeDevices;

        var tenantIds = await devices
            .AsNoTracking()
            .Where(d => d.InstallationId == installationId
                && d.RemovedAtUtc == null
                && !ignoredDeviceIds.Contains(d.Id))
            .Select(d => d.TenantId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (tenantIds.Contains(tenantId))
            return InstallationRegistration.SameTenant;

        return tenantIds.Count > 0 ? InstallationRegistration.OtherTenant : InstallationRegistration.None;
    }

    private enum InstallationRegistration
    {
        None,
        SameTenant,
        OtherTenant
    }

    private enum InstallationBinding
    {
        Bound,
        Conflict,
        RegisteredToAnotherTenant
    }

    // The caller holds the tenant's setup lock, as the exchange, the move and the Web panel do (WAS-190): see PrintBridgeDeviceQuota.
    private Task<int> CountActiveDevicesAsync(Guid tenantId, CancellationToken ct) =>
        PrintBridgeDeviceQuota.CountActiveDevicesAsync(_centralDb, tenantId, ct);

    private async Task<InstallationBinding> BindInstallationIdentityAsync(
        Guid tenantId,
        Guid deviceId,
        Guid installationId,
        CancellationToken ct)
    {
        await using var tx = await _centralDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var appLockResult = await _tenantLock.AcquireInstallationLockAsync(installationId, ct)
            .ConfigureAwait(false);
        if (appLockResult >= 0)
        {
            appLockResult = await _tenantLock.AcquireNewDeviceExchangeLockAsync(tenantId, ct)
                .ConfigureAwait(false);
        }

        if (appLockResult < 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return InstallationBinding.Conflict;
        }

        var device = await _centralDb.PrintBridgeDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.TenantId == tenantId && d.RemovedAtUtc == null, ct)
            .ConfigureAwait(false);

        if (device is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return InstallationBinding.Conflict;
        }

        // Even a device already bound to this installation may not take it while another tenant still has it
        // registered (legacy duplicates from before WAS-91 included).
        if (await FindInstallationRegistrationAsync(tenantId, installationId, [deviceId], ct).ConfigureAwait(false)
            == InstallationRegistration.OtherTenant)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return InstallationBinding.RegisteredToAnotherTenant;
        }

        if (device.InstallationId.HasValue)
        {
            var ok = device.InstallationId.Value == installationId;
            if (ok)
                await tx.CommitAsync(ct).ConfigureAwait(false);
            else
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            return ok ? InstallationBinding.Bound : InstallationBinding.Conflict;
        }

        var duplicate = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .AnyAsync(d => d.TenantId == tenantId
                && d.Id != deviceId
                && d.InstallationId == installationId
                && d.RemovedAtUtc == null, ct)
            .ConfigureAwait(false);

        if (duplicate)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return InstallationBinding.Conflict;
        }

        device.InstallationId = installationId;
        device.UpdatedAt = DateTime.UtcNow;
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return InstallationBinding.Bound;
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

    private static string NormalizeDeviceName(string? deviceName, string? machineName)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = (machineName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Print Bridge";
        return Truncate(name, 200);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
