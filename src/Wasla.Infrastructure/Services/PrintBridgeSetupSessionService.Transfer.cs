using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Moving one Print Bridge installation from its current tenant to the tenant of a setup session (WAS-91).
/// <para>
/// Authority comes from two proofs presented together: the current device's own valid token (the source) and the
/// destination's unexpired, unused setup code, whose session must have been created for the setup link's server. The
/// move is bound to that installation, that source device and that session; it grants no other access to either tenant.
/// </para>
/// <para>
/// The desktop app generates the destination token and the completion credential before it asks and keeps them, with
/// the request, until it has saved the result. Only hashes are stored here, so repeating the identical request after a
/// lost response or a restart returns the same result and changes nothing, until the app completes the session.
/// </para>
/// </summary>
public sealed partial class PrintBridgeSetupSessionService
{
    /// <summary>
    /// How long after a move the identical request may still be repeated while the app has not completed it, for example
    /// after a crash or a long network outage. Completion ends it earlier.
    /// </summary>
    public static readonly TimeSpan TransferRecoveryWindow = TimeSpan.FromDays(7);

    /// <summary>Shortest app-generated secret accepted: 32 random bytes in Base64Url, as the server's own tokens.</summary>
    private const int MinGeneratedSecretLength = 43;

    private const int MaxGeneratedSecretLength = 128;

    public async Task<PrintBridgeTransferPreview> PreviewTransferAsync(
        PrintBridgeTransferRequest request,
        CancellationToken ct)
    {
        var check = await CheckTransferAsync(request, ct).ConfigureAwait(false);
        if (check.Outcome != PrintBridgeTransferOutcome.Ready)
            return new PrintBridgeTransferPreview(check.Outcome);

        return new PrintBridgeTransferPreview(
            PrintBridgeTransferOutcome.Ready,
            check.SourceTenantName,
            check.TargetTenantName,
            check.Session!.ServerUrl,
            check.Session.ExpiresAtUtc);
    }

    public async Task<PrintBridgeTransferResult> TransferAsync(
        PrintBridgeTransferRequest request,
        CancellationToken ct)
    {
        if (!IsGeneratedSecret(request.NewDeviceToken)
            || !IsGeneratedSecret(request.CompletionCredential)
            || string.Equals(request.NewDeviceToken, request.SourceDeviceToken?.Trim(), StringComparison.Ordinal))
        {
            return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.InvalidOrExpired);
        }

        var existing = await FindSessionByCodeAsync(request.Code, ct).ConfigureAwait(false);
        if (existing is { SourcePrintBridgeDeviceId: not null })
            return await RepeatTransferAsync(existing, request, ct).ConfigureAwait(false);

        var check = await CheckTransferAsync(request, ct).ConfigureAwait(false);
        var result = check.Outcome == PrintBridgeTransferOutcome.Ready
            ? await CommitTransferAsync(check, request, ct).ConfigureAwait(false)
            : new PrintBridgeTransferResult(check.Outcome);

        if (result.Outcome is PrintBridgeTransferOutcome.Ready or PrintBridgeTransferOutcome.Failed)
            return result;

        // A refusal may only mean that the app's own earlier request stored the move after this one looked: the code is
        // used, the source retired and the destination registered. The app would give the move up and keep neither token,
        // so a stored move is answered as the repeat it is (every secret must still match).
        _centralDb.ChangeTracker.Clear();
        var stored = await FindSessionByCodeAsync(request.Code, ct).ConfigureAwait(false);
        return stored is { SourcePrintBridgeDeviceId: not null }
            ? await RepeatTransferAsync(stored, request, ct).ConfigureAwait(false)
            : result;
    }

    /// <summary>
    /// Every check of a move, read-only. The commit repeats the ones that can change under the locks; the active print
    /// job check reads the source tenant's database and is not repeated (the app holds its own jobs meanwhile).
    /// </summary>
    private async Task<TransferCheck> CheckTransferAsync(PrintBridgeTransferRequest request, CancellationToken ct)
    {
        var session = await FindSessionByCodeAsync(request.Code, ct).ConfigureAwait(false);
        if (session is null)
            return TransferCheck.Refused(PrintBridgeTransferOutcome.InvalidOrExpired);

        var now = DateTime.UtcNow;
        if (!IsSameOrigin(session.ServerUrl, request.SetupServerUrl))
        {
            // The link named another server than the one the session was created for: never move on it.
            _logger.LogWarning(
                "Print Bridge transfer rejected: the setup link's server is not the session's. SessionId={SessionId}",
                session.Id);
            return TransferCheck.Refused(PrintBridgeTransferOutcome.InvalidOrExpired);
        }

        if (session.ExpiresAtUtc <= now || session.ExchangedAtUtc != null ||
            session.CompletedAtUtc != null || session.FailedAtUtc != null ||
            !TryParsePersistedSetupMode(session.SetupMode, out var mode))
        {
            return TransferCheck.Refused(PrintBridgeTransferOutcome.InvalidOrExpired);
        }

        var source = await FindSourceDeviceAsync(request, ct).ConfigureAwait(false);
        if (source is null)
            return TransferCheck.Refused(PrintBridgeTransferOutcome.SourceRejected);

        if (source.TenantId == session.TenantId)
            return TransferCheck.Refused(PrintBridgeTransferOutcome.SameTenant);

        var target = await _centralDb.Tenants
            .AsNoTracking()
            .Where(t => t.Id == session.TenantId)
            .Select(t => new { t.Name, t.IsActive })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (target is null || !target.IsActive)
            return TransferCheck.Refused(PrintBridgeTransferOutcome.InvalidOrExpired);

        var installationId = source.InstallationId!.Value;
        var destinationOutcome = await CheckDestinationAsync(session, mode, installationId, source.Id, ct)
            .ConfigureAwait(false);
        if (destinationOutcome != PrintBridgeTransferOutcome.Ready)
            return TransferCheck.Refused(destinationOutcome);

        // WAS-58/56: never while the source tenant still waits for this computer to finish a receipt.
        if (await _activePrintJobChecker
                .HasActivePrintingJobAsync(source.TenantId, installationId, source.Name, ct)
                .ConfigureAwait(false))
        {
            return TransferCheck.Refused(PrintBridgeTransferOutcome.PrintingInProgress);
        }

        return new TransferCheck(
            PrintBridgeTransferOutcome.Ready,
            session,
            mode,
            source.Id,
            source.TenantId,
            installationId,
            source.Tenant?.Name ?? string.Empty,
            target.Name);
    }

    /// <summary>
    /// The destination side of a move: no other registration of the installation that the move may not retire, room in
    /// the destination tenant for a new device, or a reconnect device that is free or already this installation's.
    /// </summary>
    private async Task<PrintBridgeTransferOutcome> CheckDestinationAsync(
        PrintBridgeSetupSession session,
        PrintBridgeSetupMode mode,
        Guid installationId,
        Guid sourceDeviceId,
        CancellationToken ct)
    {
        Guid[] ignored = session.PrintBridgeDeviceId is { } bound ? [sourceDeviceId, bound] : [sourceDeviceId];
        var registration = await FindInstallationRegistrationAsync(session.TenantId, installationId, ignored, ct)
            .ConfigureAwait(false);
        if (registration != InstallationRegistration.None)
            return PrintBridgeTransferOutcome.InstallationConflict;

        if (mode == PrintBridgeSetupMode.NewDevice)
        {
            var activeCount = await CountActiveDevicesAsync(session.TenantId, ct).ConfigureAwait(false);
            return activeCount >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount
                ? PrintBridgeTransferOutcome.DeviceLimitReached
                : PrintBridgeTransferOutcome.Ready;
        }

        if (!session.PrintBridgeDeviceId.HasValue)
            return PrintBridgeTransferOutcome.InvalidOrExpired;

        var device = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.Id == session.PrintBridgeDeviceId.Value
                && d.TenantId == session.TenantId
                && d.RemovedAtUtc == null)
            .Select(d => new { d.InstallationId, d.IsActive })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (device is null)
            return PrintBridgeTransferOutcome.InvalidOrExpired;

        if (device.InstallationId is not null && device.InstallationId != installationId)
            return PrintBridgeTransferOutcome.InstallationConflict;

        // A move gives up a working connection, so it never ends on a device the destination has turned off.
        return device.IsActive
            ? PrintBridgeTransferOutcome.Ready
            : PrintBridgeTransferOutcome.DestinationDeviceDisabled;
    }

    /// <summary>
    /// The device the token belongs to, only while it may act as the source: not removed, active, its tenant active and
    /// bound to exactly the installation in the request. Nothing is written (unlike device authentication).
    /// </summary>
    private async Task<PrintBridgeDevice?> FindSourceDeviceAsync(PrintBridgeTransferRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDeviceToken)
            || request.InstallationId is not { } installationId
            || installationId == Guid.Empty)
        {
            return null;
        }

        var tokenHash = PrintBridgeTokenHasher.HashToken(request.SourceDeviceToken);
        var device = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);

        if (device is null || device.RemovedAtUtc != null || !device.IsActive
            || device.Tenant is null || !device.Tenant.IsActive
            || device.InstallationId != installationId)
        {
            // An InstallationId or a machine name alone never stands in for the token.
            _logger.LogWarning("Print Bridge transfer rejected: the current device credential is not valid for this installation.");
            return null;
        }

        return device;
    }

    private async Task<PrintBridgeTransferResult> CommitTransferAsync(
        TransferCheck check,
        PrintBridgeTransferRequest request,
        CancellationToken ct)
    {
        var sessionId = check.Session!.Id;
        var installationId = check.InstallationId;
        var sourceTokenHash = PrintBridgeTokenHasher.HashToken(request.SourceDeviceToken);
        var newTokenHash = PrintBridgeTokenHasher.HashToken(request.NewDeviceToken!);

        await using var tx = await _centralDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        try
        {
            // The installation lock first, then the tenant locks: the order every setup write uses. A move changes both
            // tenants' devices (and the source tenant's quota), so it holds both, always in the same order, so two moves
            // in opposite directions never wait for each other in a circle.
            var lockResult = await _tenantLock.AcquireInstallationLockAsync(installationId, ct).ConfigureAwait(false);
            foreach (var tenantId in new[] { check.SourceTenantId, check.Session.TenantId }.Order())
            {
                if (lockResult < 0)
                    break;

                lockResult = await _tenantLock.AcquireNewDeviceExchangeLockAsync(tenantId, ct).ConfigureAwait(false);
            }

            if (lockResult < 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "Print Bridge transfer could not acquire its setup locks. SessionId={SessionId}, LockResult={LockResult}",
                    sessionId,
                    lockResult);
                return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.Failed);
            }

            var now = DateTime.UtcNow;
            var session = await _centralDb.PrintBridgeSetupSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                .ConfigureAwait(false);
            if (session is { SourcePrintBridgeDeviceId: not null })
            {
                // A move with this code was stored while this request waited for the locks: the app's own earlier
                // request, still running when it repeated it. Answered as a repeat, so the app keeps the move it made.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                _centralDb.ChangeTracker.Clear();
                var stored = await FindSessionByCodeAsync(request.Code, ct).ConfigureAwait(false);
                return stored is null
                    ? new PrintBridgeTransferResult(PrintBridgeTransferOutcome.InvalidOrExpired)
                    : await RepeatTransferAsync(stored, request, ct).ConfigureAwait(false);
            }

            if (session is null || session.ExpiresAtUtc <= now || session.ExchangedAtUtc != null ||
                session.CompletedAtUtc != null || session.FailedAtUtc != null)
            {
                // Another exchange or move used the code first, or it expired or was ended meanwhile.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.InvalidOrExpired);
            }

            var source = await _centralDb.PrintBridgeDevices
                .FirstOrDefaultAsync(d => d.Id == check.SourceDeviceId, ct)
                .ConfigureAwait(false);
            if (source is null || source.RemovedAtUtc != null || !source.IsActive
                || source.InstallationId != installationId
                || !string.Equals(source.TokenHash, sourceTokenHash, StringComparison.Ordinal))
            {
                // Removed, disabled or given a new token since the check.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.SourceRejected);
            }

            var destinationOutcome = await CheckDestinationAsync(session, check.Mode, installationId, source.Id, ct)
                .ConfigureAwait(false);
            if (destinationOutcome != PrintBridgeTransferOutcome.Ready)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new PrintBridgeTransferResult(destinationOutcome);
            }

            PrintBridgeDevice destination;
            if (check.Mode == PrintBridgeSetupMode.NewDevice)
            {
                // The new device is named after the machine, never after the source tenant's name for it.
                destination = new PrintBridgeDevice
                {
                    TenantId = session.TenantId,
                    Name = NormalizeDeviceName(deviceName: null, request.MachineName),
                    InstallationId = installationId,
                    MachineName = TrimOrNull(request.MachineName, 200),
                    AppVersion = TrimOrNull(request.AppVersion, 50),
                    PrinterName = TrimOrNull(request.PrinterName, 200),
                    TokenHash = newTokenHash,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _centralDb.PrintBridgeDevices.Add(destination);
            }
            else
            {
                destination = await _centralDb.PrintBridgeDevices
                    .FirstAsync(d => d.Id == session.PrintBridgeDeviceId!.Value, ct)
                    .ConfigureAwait(false);
                destination.InstallationId = installationId;
                destination.TokenHash = newTokenHash;
                destination.UpdatedAt = now;
            }

            // Retire the source: the same soft removal as the Web panel's, so it leaves the list and the quota, keeps its
            // record and print history, and its token stops working now.
            source.RemovedAtUtc = now;
            source.IsActive = false;
            source.TokenHash = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());
            source.UpdatedAt = now;

            session.ExchangedAtUtc = now;
            session.PrintBridgeDeviceId = destination.Id;
            session.CompletionCredentialHash = PrintBridgeSetupCode.Hash(request.CompletionCredential!);
            session.SourcePrintBridgeDeviceId = source.Id;
            session.SourceTokenHash = sourceTokenHash;
            session.UpdatedAt = now;

            await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Print Bridge installation moved between tenants. SessionId={SessionId}, SourceDeviceId={SourceDeviceId}, " +
                "SourceTenantId={SourceTenantId}, DeviceId={DeviceId}, TenantId={TenantId}, Mode={Mode}",
                session.Id,
                source.Id,
                source.TenantId,
                destination.Id,
                session.TenantId,
                session.SetupMode);

            return new PrintBridgeTransferResult(
                PrintBridgeTransferOutcome.Ready,
                session.Id,
                session.ServerUrl,
                destination.Name,
                installationId);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "Print Bridge transfer failed; nothing was changed. SessionId={SessionId}", sessionId);
            return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.Failed);
        }
    }

    /// <summary>
    /// The identical move request again, after the move was stored: the app lost the response, restarted or could not
    /// save its settings. Every secret must match what the move stored; the answer is the same and nothing changes.
    /// Anything else, including a session already completed, is refused as invalid.
    /// </summary>
    private async Task<PrintBridgeTransferResult> RepeatTransferAsync(
        PrintBridgeSetupSession session,
        PrintBridgeTransferRequest request,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var repeatable = session.CompletedAtUtc == null
            && session.FailedAtUtc == null
            && session.ExchangedAtUtc is { } exchangedAt
            && exchangedAt.Add(TransferRecoveryWindow) > now
            && session.PrintBridgeDeviceId.HasValue
            && request.InstallationId.HasValue
            && IsSameOrigin(session.ServerUrl, request.SetupServerUrl)
            && PrintBridgeSetupCode.Verify(request.SourceDeviceToken, session.SourceTokenHash)
            && PrintBridgeSetupCode.Verify(request.CompletionCredential, session.CompletionCredentialHash);

        if (!repeatable)
            return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.InvalidOrExpired);

        var destination = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.Id == session.PrintBridgeDeviceId!.Value
                && d.TenantId == session.TenantId
                && d.RemovedAtUtc == null
                && d.InstallationId == request.InstallationId)
            .Select(d => new { d.Name, d.TokenHash })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (destination is null || !PrintBridgeSetupCode.Verify(request.NewDeviceToken, destination.TokenHash))
            return new PrintBridgeTransferResult(PrintBridgeTransferOutcome.InvalidOrExpired);

        _logger.LogInformation(
            "Print Bridge transfer repeated after it was stored; nothing changed. SessionId={SessionId}",
            session.Id);

        return new PrintBridgeTransferResult(
            PrintBridgeTransferOutcome.Ready,
            session.Id,
            session.ServerUrl,
            destination.Name,
            request.InstallationId);
    }

    private async Task<PrintBridgeSetupSession?> FindSessionByCodeAsync(string? rawCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return null;

        var codeHash = PrintBridgeSetupCode.Hash(rawCode);
        return await _centralDb.PrintBridgeSetupSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CodeHash == codeHash, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Scheme, host and port of two absolute URLs are the same; a path never matters.</summary>
    internal static bool IsSameOrigin(string? first, string? second)
    {
        if (!Uri.TryCreate(first, UriKind.Absolute, out var a) || !Uri.TryCreate(second, UriKind.Absolute, out var b))
            return false;

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost.TrimEnd('.'), b.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;
    }

    private static bool IsGeneratedSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinGeneratedSecretLength || value.Length > MaxGeneratedSecretLength)
            return false;

        foreach (var c in value)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }

        return true;
    }

    private static string? TrimOrNull(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), maxLength);

    private sealed record TransferCheck(
        PrintBridgeTransferOutcome Outcome,
        PrintBridgeSetupSession? Session = null,
        PrintBridgeSetupMode Mode = default,
        Guid SourceDeviceId = default,
        Guid SourceTenantId = default,
        Guid InstallationId = default,
        string SourceTenantName = "",
        string TargetTenantName = "")
    {
        public static TransferCheck Refused(PrintBridgeTransferOutcome outcome) => new(outcome);
    }
}
