using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrderHub.Web.Services;

namespace OrderHub.Web.Pages;

public class CikisModel : PageModel
{
    private readonly IOrderHubApiClient _api;

    public CikisModel(IOrderHubApiClient api)
    {
        _api = api;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await _api.LogoutAsync(ct);
        return Redirect("/giris");
    }
}

