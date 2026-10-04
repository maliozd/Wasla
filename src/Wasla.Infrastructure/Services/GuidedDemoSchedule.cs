namespace Wasla.Infrastructure.Services;

/// <summary>
/// Wasla.Worker's in-memory list of tenants whose practice orders need a look, and when (UTC). The order-sync cycle
/// fills it from each tenant's guided-demo read (so a Worker restart rebuilds it in its first cycle) and
/// <see cref="GuidedDemoScheduler"/> wakes for it. Holds no order data and is never shared between processes: a
/// second Worker instance has its own list, and the conditional updates keep both safe.
/// </summary>
public sealed class GuidedDemoSchedule
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTime> _checks = new();
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Records when <paramref name="tenantId"/> next needs a look; null forgets the tenant.</summary>
    public void Plan(Guid tenantId, DateTime? checkAtUtc)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (checkAtUtc is { } at)
                _checks[tenantId] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            else if (!_checks.Remove(tenantId))
                return;

            changed = _changed;
            _changed = NewSignal();
        }

        // Wakes the scheduler so it measures its wait again; it reads nothing from a database for this.
        changed.TrySetResult();
    }

    /// <summary>Removes and returns the tenants due at <paramref name="nowUtc"/>, earliest first.</summary>
    public IReadOnlyList<Guid> TakeDue(DateTime nowUtc)
    {
        lock (_gate)
        {
            var due = _checks
                .Where(check => check.Value <= nowUtc)
                .OrderBy(check => check.Value)
                .Select(check => check.Key)
                .ToList();
            foreach (var tenantId in due)
                _checks.Remove(tenantId);
            return due;
        }
    }

    /// <summary>The earliest planned look, or null when no tenant has an open practice order.</summary>
    public DateTime? NextCheckAtUtc
    {
        get
        {
            lock (_gate)
                return _checks.Count == 0 ? null : _checks.Values.Min();
        }
    }

    public DateTime? CheckAtUtc(Guid tenantId)
    {
        lock (_gate)
            return _checks.TryGetValue(tenantId, out var at) ? at : null;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _checks.Count;
        }
    }

    /// <summary>Completes on the next <see cref="Plan"/> that changes the list.</summary>
    public Task Changed
    {
        get
        {
            lock (_gate)
                return _changed.Task;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
