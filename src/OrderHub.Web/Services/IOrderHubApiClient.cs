using OrderHub.Contracts.Auth;
using OrderHub.Contracts.Dashboard;
using OrderHub.Contracts.Orders;
using OrderHub.Contracts.PlatformConnections;

namespace OrderHub.Web.Services;

public interface IOrderHubApiClient
{
    Task<bool> LoginAsync(string email, string password, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<CurrentUserDto?> GetCurrentUserAsync(CancellationToken ct = default);

    Task<DashboardSummaryDto?> GetDashboardTodayAsync(CancellationToken ct = default);

    Task<OrderListResponse?> GetOrdersAsync(OrderListQuery query, CancellationToken ct = default);
    Task<OrderDetailDto?> GetOrderByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformConnectionDto>?> GetPlatformConnectionsAsync(CancellationToken ct = default);
    Task<bool> SetPlatformConnectionActiveAsync(Guid id, bool isActive, CancellationToken ct = default);
}

