using System.Net;
using System.Net.Http.Json;
using OrderHub.Contracts.Auth;
using OrderHub.Contracts.Dashboard;
using OrderHub.Contracts.Orders;
using OrderHub.Contracts.PlatformConnections;

namespace OrderHub.Web.Services;

public sealed class OrderHubApiClient : IOrderHubApiClient
{
    private readonly HttpClient _http;

    public OrderHubApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = email, Password = password },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _http.PostAsync("/api/auth/logout", content: null, ct);
    }

    public async Task<CurrentUserDto?> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/auth/me", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CurrentUserDto>(cancellationToken: ct);
    }

    public async Task<DashboardSummaryDto?> GetDashboardTodayAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/dashboard/today", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(cancellationToken: ct);
    }

    public async Task<OrderListResponse?> GetOrdersAsync(OrderListQuery query, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (query.Platform.HasValue) qs.Add($"platform={(int)query.Platform.Value}");
        if (query.Status.HasValue) qs.Add($"status={(int)query.Status.Value}");
        if (query.StartDate.HasValue) qs.Add($"startDate={Uri.EscapeDataString(query.StartDate.Value.ToString("o"))}");
        if (query.EndDate.HasValue) qs.Add($"endDate={Uri.EscapeDataString(query.EndDate.Value.ToString("o"))}");
        qs.Add($"page={query.Page}");
        qs.Add($"pageSize={query.PageSize}");
        var url = "/api/orders?" + string.Join("&", qs);

        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<OrderListResponse>(cancellationToken: ct);
    }

    public async Task<OrderDetailDto?> GetOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/orders/{id}", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<OrderDetailDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<PlatformConnectionDto>?> GetPlatformConnectionsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/platform-connections", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<List<PlatformConnectionDto>>(cancellationToken: ct);
    }

    public async Task<bool> SetPlatformConnectionActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var response = await _http.PatchAsJsonAsync(
            $"/api/platform-connections/{id}/active",
            new UpdatePlatformConnectionActiveRequest { IsActive = isActive },
            ct);
        return response.IsSuccessStatusCode;
    }
}

