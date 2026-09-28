using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Demos;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class GuidedDemoService : IGuidedDemoService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private static readonly TimeSpan RecentDeliveredWindow = LiveScreenVisibility.RecentDeliveredWindow;

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
        var open = await db.GuidedDemoSessions
            .Where(session => session.UserId == userId && session.CompletedAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var session in open)
            session.CompletedAtUtc = now;

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
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToState(created);
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

        var now = _time.GetUtcNow().UtcDateTime;
        var windowStart = now - RecentDeliveredWindow;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var delivered = await db.GuidedDemoSessions.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Status == OrderStatus.Delivered
                && row.CompletedAtUtc != null
                && row.CompletedAtUtc >= windowStart
                && row.CompletedAtUtc <= now)
            .OrderByDescending(row => row.CompletedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return delivered is null ? null : ToState(delivered);
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
                : null);
    }
}
