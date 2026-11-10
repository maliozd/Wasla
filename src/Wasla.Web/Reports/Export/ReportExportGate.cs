using System.Collections.Concurrent;

namespace Wasla.Web.Reports.Export;

public enum ReportExportAdmission
{
    Admitted,

    /// <summary>The same user of the same tenant already has an export running.</summary>
    AlreadyRunning,

    /// <summary>The Web process is already creating its maximum number of exports.</summary>
    Busy
}

/// <summary>
/// Keeps report exports (WAS-184) bounded in this Web process: one export at a time per signed-in user of a tenant, and at
/// most <see cref="DefaultMaxConcurrentExports"/> at once in total. A refused request waits for nothing: it is answered
/// 429 at once. The limit is per process; Wasla runs one Web instance (docs/architecture/realtime-scale-out.md).
/// </summary>
public sealed class ReportExportGate : IDisposable
{
    public const int DefaultMaxConcurrentExports = 4;

    private readonly ConcurrentDictionary<(Guid TenantId, Guid UserId), byte> _running = new();
    private readonly SemaphoreSlim _slots;

    public ReportExportGate()
        : this(DefaultMaxConcurrentExports)
    {
    }

    public ReportExportGate(int maxConcurrentExports)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentExports, 1);
        _slots = new SemaphoreSlim(maxConcurrentExports, maxConcurrentExports);
    }

    /// <summary>Admits an export, or says why not. An admitted export holds its place until <paramref name="lease"/> is disposed.</summary>
    public ReportExportAdmission TryEnter(Guid tenantId, Guid userId, out IDisposable? lease)
    {
        lease = null;
        var key = (tenantId, userId);
        if (!_running.TryAdd(key, 0))
            return ReportExportAdmission.AlreadyRunning;

        if (!_slots.Wait(0))
        {
            _running.TryRemove(key, out _);
            return ReportExportAdmission.Busy;
        }

        lease = new Lease(this, key);
        return ReportExportAdmission.Admitted;
    }

    public void Dispose() => _slots.Dispose();

    private sealed class Lease(ReportExportGate gate, (Guid, Guid) key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            gate._slots.Release();
            gate._running.TryRemove(key, out _);
        }
    }
}
