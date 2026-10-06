namespace Wasla.Infrastructure.Platform.TrendyolGo;

/// <summary>
/// Process-wide limit on Trendyol GO package-polling HTTP requests: at most <see cref="DefaultPermitLimit"/> requests
/// start in any rolling <see cref="DefaultWindow"/>. Registered as a singleton, so every tenant connection served by
/// this process shares one budget. Each HTTP attempt (every page and every retry) takes one permit.
/// </summary>
/// <remarks>
/// Trendyol GO documents at most 50 requests to the same endpoint in 10 seconds, without saying whether that is per
/// supplier, per integrator or per source IP. Until that is confirmed in writing, the whole process stays at 40.
/// The budget is per process: several Worker instances would each get their own and need a distributed limiter.
/// Algorithm: a rolling log of grant times. A request is granted at once while fewer than the limit were granted in
/// the last window; otherwise it waits until the oldest grant leaves the window. Waiters are served one at a time in
/// arrival order. <see cref="Defer"/> pauses every request after a 429 until the provider's retry time.
/// </remarks>
public sealed class TrendyolRequestRateLimiter
{
    public const int DefaultPermitLimit = 40;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _grants = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _deferLock = new();
    private DateTimeOffset _resumeAt = DateTimeOffset.MinValue;

    public TrendyolRequestRateLimiter(TimeProvider time)
        : this(time, null, DefaultPermitLimit, DefaultWindow)
    {
    }

    /// <param name="delay">Waits for the given time. Tests pass a fake that moves a fake clock.</param>
    internal TrendyolRequestRateLimiter(
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay,
        int permitLimit,
        TimeSpan window)
    {
        if (permitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(permitLimit));
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _time = time;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, time, ct));
        _permitLimit = permitLimit;
        _window = window;
    }

    /// <summary>Waits asynchronously until one more request may start, then counts it.</summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = _time.GetUtcNow();

                var resumeAt = ResumeAt;
                if (resumeAt > now)
                {
                    await _delay(resumeAt - now, ct).ConfigureAwait(false);
                    continue;
                }

                while (_grants.Count > 0 && now - _grants.Peek() >= _window)
                    _grants.Dequeue();

                if (_grants.Count < _permitLimit)
                {
                    _grants.Enqueue(now);
                    return;
                }

                await _delay(_grants.Peek() + _window - now, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// After a 429, holds every later request in this process until <paramref name="delay"/> has passed. The quota's
    /// scope is unknown, so one connection's throttling pauses all Trendyol GO polling.
    /// </summary>
    public void Defer(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var until = _time.GetUtcNow() + delay;
        lock (_deferLock)
        {
            if (until > _resumeAt)
                _resumeAt = until;
        }
    }

    private DateTimeOffset ResumeAt
    {
        get
        {
            lock (_deferLock)
                return _resumeAt;
        }
    }
}
