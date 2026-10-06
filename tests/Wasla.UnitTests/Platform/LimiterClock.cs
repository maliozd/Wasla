using Wasla.Infrastructure.Platform.TrendyolGo;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Fake time for <see cref="TrendyolRequestRateLimiter"/>: a wait moves the clock forward at once and is recorded, so
/// tests never sleep. Every read and move is locked because parallel tenants read the clock.
/// </summary>
internal sealed class LimiterClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _waits = [];
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now
    {
        get { lock (_gate) return _now; }
        set { lock (_gate) _now = value; }
    }

    public IReadOnlyList<TimeSpan> Waits
    {
        get { lock (_gate) return [.. _waits]; }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    public Task Delay(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _waits.Add(wait);
            _now += wait;
        }

        return Task.CompletedTask;
    }

    public TrendyolRequestRateLimiter Limiter(int permitLimit = TrendyolRequestRateLimiter.DefaultPermitLimit) =>
        new(this, Delay, permitLimit, TrendyolRequestRateLimiter.DefaultWindow);
}
