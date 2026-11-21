using System.Text.Json;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Central;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The 81 Turkish provinces of the repository's reference data (<c>turkey-reference-data.json</c>, the file the
/// <c>seed-turkey-reference-data</c> import reads), as active CentralDb city rows, so a test catalog is the complete one
/// the pilot form requires (WAS-217).
/// </summary>
internal static class PilotProvinceSeed
{
    private static readonly Lazy<IReadOnlyList<(string PlateCode, string Name)>> Provinces = new(Load);

    /// <summary>
    /// Active city rows with explicit ids (the plate number plus <paramref name="idOffset"/>, clear of cities a test host
    /// seeds itself), for SQLite test databases.
    /// </summary>
    public static List<City> All(int idOffset = 0) => Provinces.Value
        .Select(p => new City
        {
            Id = idOffset + int.Parse(p.PlateCode, System.Globalization.CultureInfo.InvariantCulture),
            CountryCode = "TR",
            Name = p.Name,
            PlateCode = p.PlateCode,
            SortOrder = int.Parse(p.PlateCode, System.Globalization.CultureInfo.InvariantCulture),
            IsActive = true
        })
        .ToList();

    private static IReadOnlyList<(string, string)> Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Wasla.sln was not found.");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "src", "Wasla.Infrastructure", "ReferenceData", "turkey-reference-data.json")));
        return json.RootElement.GetProperty("cities").EnumerateArray()
            .Select(c => (c.GetProperty("plateCode").GetString()!, c.GetProperty("name").GetString()!))
            .ToList();
    }
}

/// <summary>
/// A clock the test moves by hand. Timers it creates (such as a <see cref="CancellationTokenSource"/> deadline) fire when
/// <see cref="Advance"/> passes their due time, never on their own.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and runs every timer that has come due, in due order.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).OrderBy(t => t.DueAt).ToList();
            foreach (var timer in due)
                timer.DueAt = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (!clock._timers.Contains(this))
                    clock._timers.Add(this);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An SMTP stand-in whose sends the test controls. A send counts as delivered only when it completes; one cancelled by
/// its token delivers nothing, as a cancelled SMTP transaction does.
/// </summary>
internal sealed class ControlledSender : IEmailSender
{
    private readonly object _gate = new();
    private readonly List<EmailMessage> _delivered = [];
    private int _started;

    /// <summary>When set, each send waits for <see cref="Release"/> (or its token) before it completes.</summary>
    public bool Hold { get; init; }

    /// <summary>When true, a held send ignores its token and waits for <see cref="Release"/> only.</summary>
    public bool IgnoreCancellation { get; init; }

    /// <summary>When set, a send fails with this exception instead of delivering.</summary>
    public Exception? Failure { get; init; }

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstStarted => _firstStarted.Task;

    public int Started => Volatile.Read(ref _started);

    public IReadOnlyList<EmailMessage> Delivered
    {
        get
        {
            lock (_gate)
                return _delivered.ToArray();
        }
    }

    public void Release() => _release.TrySetResult();

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref _started);
        _firstStarted.TrySetResult();
        if (Hold)
            await (IgnoreCancellation ? _release.Task : _release.Task.WaitAsync(ct)).ConfigureAwait(false);
        if (Failure is not null)
            throw Failure;

        lock (_gate)
            _delivered.Add(message);
    }
}
