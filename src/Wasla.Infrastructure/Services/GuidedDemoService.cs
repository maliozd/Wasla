using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Demos;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;

namespace Wasla.Infrastructure.Services;

public sealed class GuidedDemoService : IGuidedDemoService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly ITenantBusinessSubtypeReader _subtypes;
    private readonly TimeProvider _time;

    public GuidedDemoService(
        ITenantDbContextFactory tenantDbs,
        ITenantBusinessSubtypeReader subtypes,
        TimeProvider time)
    {
        _tenantDbs = tenantDbs;
        _subtypes = subtypes;
        _time = time;
    }

    public async Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var active = await GetActiveAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (active is not null)
            return active;

        var now = _time.GetUtcNow().UtcDateTime;
        var codes = await _subtypes.GetSubtypeCodesAsync(tenantId, ct).ConfigureAwait(false);
        var scenario = GuidedDemoScenarioCatalog.ForSubtypes(codes);

        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        // Close only sessions that can no longer resume. A session a concurrent start just created
        // stays open, so the unique open-session index decides the race below.
        await db.GuidedDemoSessions
            .Where(session => session.UserId == userId
                && session.CompletedAtUtc == null
                && (session.ExpiresAtUtc <= now
                    || session.Status == OrderStatus.Cancelled
                    || session.Status == OrderStatus.Delivered))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.CompletedAtUtc, now)
                .SetProperty(session => session.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        var created = new GuidedDemoSession
        {
            UserId = userId,
            ScenarioCode = scenario.Code,
            Status = OrderStatus.New,
            CustomerNameKey = scenario.CustomerNameKey,
            NoteKey = scenario.NoteKey,
            ItemsJson = JsonSerializer.Serialize(scenario.Lines, JsonOptions),
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.Add(Lifetime)
        };
        db.GuidedDemoSessions.Add(created);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsOpenSessionConflict(ex))
        {
            // Another request started this user's demo first; reuse it instead of failing.
            var winner = await GetActiveAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (winner is not null)
                return winner;
            throw;
        }

        return ToState(created);
    }

    internal static bool IsOpenSessionConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains(GuidedDemoSessionIndexes.UserOpen, StringComparison.Ordinal))
                return true;

            if (message.Contains("UNIQUE constraint failed: GuidedDemoSessions.UserId", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var session = await db.GuidedDemoSessions.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.CompletedAtUtc == null
                && row.ExpiresAtUtc > now
                && row.Status != OrderStatus.Cancelled
                && row.Status != OrderStatus.Delivered)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return session is null ? null : ToState(session);
    }

    public async Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var active = await GetActiveAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (active is not null)
            return active;

        // A delivered practice order stays for its own short stage, then leaves the Live Screen. Training goes on:
        // GetLatestAsync still reports it as delivered. Real delivered orders keep LiveScreenVisibility's window.
        var now = _time.GetUtcNow().UtcDateTime;
        var dueFrom = GuidedDemoTiming.DueFrom(now);
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var delivered = await db.GuidedDemoSessions.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Status == OrderStatus.Delivered
                && row.CompletedAtUtc != null
                && row.CompletedAtUtc > dueFrom
                && row.CompletedAtUtc <= now)
            .OrderByDescending(row => row.CompletedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return delivered is null ? null : ToState(delivered);
    }

    public async Task<GuidedDemoSummary?> GetLatestAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var latest = await db.GuidedDemoSessions.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Select(row => new { row.Id, row.Status, row.CompletedAtUtc, row.ExpiresAtUtc })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latest is null)
            return null;

        // The same definition of "open" as GetActiveAsync: it can still move.
        var isOpen = latest.CompletedAtUtc == null
            && latest.ExpiresAtUtc > now
            && latest.Status != OrderStatus.Cancelled
            && latest.Status != OrderStatus.Delivered;
        return new GuidedDemoSummary(latest.Id, latest.Status, isOpen);
    }

    public async Task<GuidedDemoActionResult> ApplyActionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        string action,
        CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var session = await db.GuidedDemoSessions.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == sessionId && row.UserId == userId, ct)
            .ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        if (session is null || session.CompletedAtUtc is not null || session.ExpiresAtUtc <= now)
            return new GuidedDemoActionResult(false, "Orders.ActionFailed", null);

        if (!GuidedDemoTransitions.TryMove(session.Status, action, out var next, out var messageKey))
            return new GuidedDemoActionResult(false, messageKey, session.Status);

        DateTime? completedAt = next is OrderStatus.Cancelled or OrderStatus.Delivered ? now : null;
        var changed = await db.GuidedDemoSessions
            .Where(row => row.Id == sessionId && row.UserId == userId
                && row.Status == session.Status && row.CompletedAtUtc == null && row.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, next)
                .SetProperty(row => row.CompletedAtUtc, completedAt)
                .SetProperty(row => row.UpdatedAt, now), ct).ConfigureAwait(false);
        if (changed != 1)
            return new GuidedDemoActionResult(false, "Orders.InvalidStatusForAction", null);
        return new GuidedDemoActionResult(true, messageKey, next);
    }

    public async Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var open = await db.GuidedDemoSessions
            .Where(session => session.UserId == userId && session.CompletedAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (open.Count == 0)
            return;

        foreach (var session in open)
            session.CompletedAtUtc = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static GuidedDemoSessionState ToState(GuidedDemoSession session)
    {
        var lines = JsonSerializer.Deserialize<List<GuidedDemoLine>>(session.ItemsJson, JsonOptions) ?? [];
        // The time it entered its status: every status change sets UpdatedAt (and CompletedAtUtc for Delivered).
        var enteredAt = DateTime.SpecifyKind(
            session.Status == OrderStatus.Delivered && session.CompletedAtUtc is { } completed ? completed : session.UpdatedAt,
            DateTimeKind.Utc);
        return new GuidedDemoSessionState(
            session.Id,
            session.UserId,
            session.ScenarioCode,
            session.Status,
            DateTime.SpecifyKind(session.ReceivedAtUtc, DateTimeKind.Utc),
            session.CustomerNameKey,
            session.NoteKey,
            lines,
            session.Status == OrderStatus.Delivered && session.CompletedAtUtc is { } deliveredAt
                ? DateTime.SpecifyKind(deliveredAt, DateTimeKind.Utc)
                : null)
        {
            Automatic = GuidedDemoTiming.StepFor(session.Status, enteredAt)
        };
    }
}
