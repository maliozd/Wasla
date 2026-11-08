namespace Wasla.Application.Abstractions.Dashboard;

public interface IDashboardService
{
    /// <summary>Today's figures from the shared order report, and the latest orders.</summary>
    Task<DashboardResult> GetTodayAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Synchronization runs started today (Türkiye local day).</summary>
    Task<DashboardSyncStatistics> GetTodaySyncStatisticsAsync(Guid tenantId, CancellationToken ct);
}
