using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Per-user guided-setup state in the tenant database. Each write is atomic: the first write
/// inserts the user's single row (the unique index decides concurrent inserts) and later writes
/// are conditional on the status they were decided from. A write that loses a race re-reads the
/// row and decides again, so concurrent requests never rewrite a terminal state.
/// <para>
/// Completing or skipping also moves the tenant from Setup to Live, in the same database transaction as the
/// user's own change: a user is never recorded as finished while the tenant stays in Setup. The tenant change is
/// conditional (Setup → Live only), so it is idempotent, a later user's choice never changes a Live tenant, and
/// starting guided setup never touches the tenant.
/// </para>
/// </summary>
public sealed class GuidedSetupService : IGuidedSetupService
{
    // Each retry follows a real state change, and a user changes state at most twice.
    private const int MaxAttempts = 4;

    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;

    public GuidedSetupService(ITenantDbContextFactory tenantDbs, TimeProvider time)
    {
        _tenantDbs = tenantDbs;
        _time = time;
    }

    public async Task<GuidedSetupState> GetAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return ToState(await LoadAsync(db, userId, ct).ConfigureAwait(false));
    }

    public Task<GuidedSetupResult> StartAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(position);
        return ApplyAsync(tenantId, userId, GuidedSetupCommand.Start, position, ct);
    }

    public Task<GuidedSetupResult> SaveProgressAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(position);
        return ApplyAsync(tenantId, userId, GuidedSetupCommand.SaveProgress, position, ct);
    }

    public Task<GuidedSetupResult> SkipAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        ApplyAsync(tenantId, userId, GuidedSetupCommand.Skip, position: null, ct);

    public Task<GuidedSetupResult> CompleteAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        ApplyAsync(tenantId, userId, GuidedSetupCommand.Complete, position: null, ct);

    private async Task<GuidedSetupResult> ApplyAsync(
        Guid tenantId,
        Guid userId,
        GuidedSetupCommand command,
        GuidedSetupPosition? position,
        CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);

        if (position is not null && !position.IsValid)
            return new GuidedSetupResult(GuidedSetupOutcome.InvalidPosition, ToState(await LoadAsync(db, userId, ct).ConfigureAwait(false)));

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var row = await LoadAsync(db, userId, ct).ConfigureAwait(false);
            var current = row?.Status ?? GuidedSetupStatus.NotStarted;
            var outcome = GuidedSetupTransitions.Evaluate(command, current);
            if (outcome != GuidedSetupOutcome.Applied)
                return new GuidedSetupResult(outcome, ToState(row));

            var now = _time.GetUtcNow().UtcDateTime;
            if (row is null)
            {
                var created = await TryInsertAsync(db, NewRow(userId, command, position, now), FinishesGuidedSetup(command), now, ct)
                    .ConfigureAwait(false);
                if (created is not null)
                    return new GuidedSetupResult(GuidedSetupOutcome.Applied, ToState(created));
                continue;
            }

            if (await TryUpdateAsync(db, row, command, position, now, ct).ConfigureAwait(false))
            {
                var updated = await LoadAsync(db, userId, ct).ConfigureAwait(false);
                return new GuidedSetupResult(GuidedSetupOutcome.Applied, ToState(updated));
            }
        }

        throw new InvalidOperationException("Guided setup state kept changing; the command was not applied.");
    }

    private static UserGuidedSetupState NewRow(
        Guid userId,
        GuidedSetupCommand command,
        GuidedSetupPosition? position,
        DateTime now)
    {
        var row = new UserGuidedSetupState
        {
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (command == GuidedSetupCommand.Start)
        {
            row.Status = GuidedSetupStatus.InProgress;
            row.StartedAtUtc = now;
            row.CurrentSectionKey = position!.SectionKey;
            row.CurrentStepKey = position.StepKey;
            return row;
        }

        if (command == GuidedSetupCommand.Skip)
        {
            // Skipped before starting: there is no start time and no position.
            row.Status = GuidedSetupStatus.Skipped;
            row.SkippedAtUtc = now;
            return row;
        }

        throw new InvalidOperationException($"{command} cannot create guided setup state.");
    }

    /// <summary>
    /// Applies the change only if the row still has the status the decision was based on. Completing or skipping
    /// also takes the tenant live; both commit together or not at all.
    /// </summary>
    private static async Task<bool> TryUpdateAsync(
        TenantDbContext db,
        UserGuidedSetupState row,
        GuidedSetupCommand command,
        GuidedSetupPosition? position,
        DateTime now,
        CancellationToken ct)
    {
        var finishing = FinishesGuidedSetup(command);
        await using var transaction = finishing ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;

        var target = db.UserGuidedSetupStates.Where(state => state.Id == row.Id && state.Status == row.Status);
        var changed = command switch
        {
            GuidedSetupCommand.SaveProgress => await target.ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.CurrentSectionKey, position!.SectionKey)
                .SetProperty(state => state.CurrentStepKey, position.StepKey)
                .SetProperty(state => state.UpdatedAt, now), ct).ConfigureAwait(false),
            GuidedSetupCommand.Skip => await target.ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.Status, GuidedSetupStatus.Skipped)
                .SetProperty(state => state.SkippedAtUtc, now)
                .SetProperty(state => state.UpdatedAt, now), ct).ConfigureAwait(false),
            GuidedSetupCommand.Complete => await target.ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.Status, GuidedSetupStatus.Completed)
                .SetProperty(state => state.CompletedAtUtc, now)
                .SetProperty(state => state.UpdatedAt, now), ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"{command} does not update existing guided setup state.")
        };
        if (changed != 1)
            return false;

        if (transaction is not null)
        {
            await TenantOperationalModes.ActivateAsync(db, now, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Completed and Skipped end the user's guided setup; both take the tenant live.</summary>
    private static bool FinishesGuidedSetup(GuidedSetupCommand command) =>
        command is GuidedSetupCommand.Complete or GuidedSetupCommand.Skip;

    /// <summary>
    /// The user's row, or null when they have not started. This table is the only source of truth:
    /// earlier product-tour completions are deliberately not consulted. Reading never writes.
    /// </summary>
    private static Task<UserGuidedSetupState?> LoadAsync(TenantDbContext db, Guid userId, CancellationToken ct) =>
        db.UserGuidedSetupStates
            .AsNoTracking()
            .SingleOrDefaultAsync(state => state.UserId == userId, ct);

    /// <summary>
    /// Inserts the user's row, or returns null when a concurrent request inserted it first. A skip before starting
    /// also takes the tenant live: both changes go to the database in one SaveChanges, which is one transaction, so
    /// a lost insert race changes the tenant as little as the user.
    /// </summary>
    private static async Task<UserGuidedSetupState?> TryInsertAsync(
        TenantDbContext db,
        UserGuidedSetupState row,
        bool takeTenantLive,
        DateTime now,
        CancellationToken ct)
    {
        db.UserGuidedSetupStates.Add(row);
        try
        {
            if (takeTenantLive)
                await TenantOperationalModes.StageActivationAsync(db, now, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateException ex) when (IsUserStateConflict(ex))
        {
            return null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    internal static bool IsUserStateConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains(UserGuidedSetupStateIndexes.User, StringComparison.Ordinal))
                return true;

            if (message.Contains("UNIQUE constraint failed: UserGuidedSetupStates.UserId", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static GuidedSetupState ToState(UserGuidedSetupState? row) =>
        row is null
            ? GuidedSetupState.NotStarted
            : new GuidedSetupState(
                row.Status,
                row.CurrentSectionKey,
                row.CurrentStepKey,
                Utc(row.StartedAtUtc),
                Utc(row.CompletedAtUtc),
                Utc(row.SkippedAtUtc),
                Utc(row.UpdatedAt));

    private static DateTime? Utc(DateTime? value) =>
        value is { } set ? DateTime.SpecifyKind(set, DateTimeKind.Utc) : null;
}
