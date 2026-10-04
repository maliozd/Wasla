using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application.Demos;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Wasla.Worker's guided-demo scheduler. It wakes when a practice order's automatic stage is due (from
/// <see cref="GuidedDemoSchedule"/>) and lets <see cref="IGuidedDemoDeliverySimulator"/> move it, so each stage lasts
/// its <see cref="GuidedDemoTiming.StageDuration"/> instead of waiting for the next order-sync cycle. It reads and
/// updates only the due tenants' GuidedDemoSessions: no provider call, order, webhook, print job or order-sync work,
/// and it does not change the order-sync cadence. With no open practice order it waits on the schedule with no timer
/// and no query.
/// </summary>
public sealed class GuidedDemoScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly GuidedDemoSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly ILogger<GuidedDemoScheduler> _logger;

    public GuidedDemoScheduler(
        IServiceScopeFactory scopes,
        GuidedDemoSchedule schedule,
        TimeProvider time,
        ILogger<GuidedDemoScheduler> logger)
    {
        _scopes = scopes;
        _schedule = schedule;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitUntilDueAsync(stoppingToken).ConfigureAwait(false);
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Returns once the earliest planned look is due: sleeps until then, or until the plan changes.</summary>
    public async Task WaitUntilDueAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var changed = _schedule.Changed;
            var next = _schedule.NextCheckAtUtc;
            if (next is null)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var delay = next.Value - _time.GetUtcNow().UtcDateTime;
            if (delay <= TimeSpan.Zero)
                return;

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(Task.Delay(delay, _time, timer.Token), changed.WaitAsync(ct)).ConfigureAwait(false);
            timer.Cancel();
        }
    }

    /// <summary>
    /// Moves every due tenant's practice orders once and plans each tenant's next look. Returns the steps advanced.
    /// </summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var advanced = 0;
        foreach (var tenantId in _schedule.TakeDue(_time.GetUtcNow().UtcDateTime))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var simulator = scope.ServiceProvider.GetRequiredService<IGuidedDemoDeliverySimulator>();
                var result = await simulator.AdvanceDueAndPlanAsync(tenantId, ct).ConfigureAwait(false);
                _schedule.Plan(tenantId, result.NextCheckAtUtc);
                advanced += result.Advanced;
                if (result.Advanced > 0)
                    _logger.LogDebug("Guided demo courier step on schedule. TenantId={TenantId}, Advanced={Advanced}", tenantId, result.Advanced);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not planned again here: the next order-sync cycle reads the tenant and plans it, so a failing tenant
                // database is retried at that cycle's pace, never in a tight loop.
                _logger.LogWarning(
                    "Guided demo scheduler could not advance practice orders. TenantId={TenantId}, ExceptionType={ExceptionType}",
                    tenantId,
                    ex.GetType().Name);
            }
        }

        return advanced;
    }
}
