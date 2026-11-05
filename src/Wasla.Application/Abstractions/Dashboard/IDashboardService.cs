namespace Wasla.Application.Abstractions.Dashboard;

public interface IDashboardService
{
    Task<DashboardResult> GetTodayAsync(Guid tenantId, CancellationToken ct);
}

