using Microsoft.AspNetCore.Mvc.RazorPages;
using OrderHub.Contracts.Dashboard;
using OrderHub.Web.Services;

namespace OrderHub.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IOrderHubApiClient _api;

    public IndexModel(IOrderHubApiClient api)
    {
        _api = api;
    }

    public string UserName { get; set; } = "";
    public DashboardSummaryDto? Summary { get; set; }
    public bool LoadFailed { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var userTask = _api.GetCurrentUserAsync(ct);
        var summaryTask = _api.GetDashboardTodayAsync(ct);

        await Task.WhenAll(userTask, summaryTask);

        var user = await userTask;
        Summary = await summaryTask;

        UserName = user?.FullName ?? "Kullanıcı";
        LoadFailed = Summary is null;
    }
}
