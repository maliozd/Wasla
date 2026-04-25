using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrderHub.Web.Services;

namespace OrderHub.Web.Pages;

public class GirisModel : PageModel
{
    private readonly IOrderHubApiClient _api;

    public GirisModel(IOrderHubApiClient api)
    {
        _api = api;
    }

    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet(string? returnUrl)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = returnUrl;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "E-posta ve şifre gerekli.";
            return Page();
        }

        var ok = await _api.LoginAsync(Email, Password, ct);
        if (!ok)
        {
            ErrorMessage = "E-posta veya şifre yanlış.";
            return Page();
        }

        return Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }
}

