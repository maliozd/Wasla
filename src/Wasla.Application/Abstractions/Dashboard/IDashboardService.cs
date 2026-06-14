namespace Wasla.Application.Abstractions.Dashboard;

public interface IDashboardService
{
    Task<DashboardResult> GetTodayAsync(Guid customerId, CancellationToken ct);
}

