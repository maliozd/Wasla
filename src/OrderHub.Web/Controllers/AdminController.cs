using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Admin;
using OrderHub.Web.Models.Admin;
using OrderHub.Web.Security;
using OrderHub.Web;

namespace OrderHub.Web.Controllers;

[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin")]
public sealed class AdminController : Controller
{
    private readonly ICentralAdminAuthService _centralAuth;
    private readonly ICentralAdminCustomerService _customers;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AdminController(
        ICentralAdminAuthService centralAuth,
        ICentralAdminCustomerService customers,
        IConfiguration configuration,
        IStringLocalizer<SharedResource> localizer)
    {
        _centralAuth = centralAuth;
        _customers = customers;
        _configuration = configuration;
        _localizer = localizer;
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        ViewBag.ConfigMissing = IsCentralAdminConfigMissing();
        return View(new AdminLoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<IActionResult> Login(AdminLoginViewModel model, CancellationToken ct)
    {
        ViewBag.ConfigMissing = IsCentralAdminConfigMissing();

        if (!ModelState.IsValid)
            return View(model);

        var ok = await _centralAuth.ValidateAsync(model.Email, model.Password, ct).ConfigureAwait(false);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, _localizer["Admin.InvalidCredentials"].Value);
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "central-admin"),
            new(ClaimTypes.Email, model.Email.Trim()),
            new(ClaimTypes.Name, "Central Admin"),
            new(ClaimTypes.Role, "CentralAdmin"),
        };

        var identity = new ClaimsIdentity(claims, AuthSchemes.CentralAdmin);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            AuthSchemes.CentralAdmin,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);

        var returnUrl = model.ReturnUrl;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.CentralAdmin).ConfigureAwait(false);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("")]
    [HttpGet("index")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await _customers.GetDashboardAsync(ct).ConfigureAwait(false);
        var vm = new CentralAdminDashboardViewModel
        {
            TotalCustomers = data.TotalCustomers,
            ActiveCustomers = data.ActiveCustomers,
            InactiveCustomers = data.InactiveCustomers,
            Customers = data.Customers.Select(c => new CentralAdminCustomerListItemViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                PrimaryDomain = c.PrimaryDomain,
                DatabaseName = c.DatabaseName,
                IsActive = c.IsActive,
                SchemaVersion = c.SchemaVersion,
                LastMigrationAt = c.LastMigrationAt,
                LastMigrationResult = c.LastMigrationResult,
                CreatedAt = c.CreatedAt
            }).ToList()
        };
        return View(vm);
    }

    [HttpGet("customers/{id:guid}")]
    public async Task<IActionResult> CustomerDetails(Guid id, CancellationToken ct)
    {
        var c = await _customers.GetCustomerAsync(id, ct).ConfigureAwait(false);
        if (c is null)
            return NotFound();

        var vm = new CentralAdminCustomerDetailViewModel
        {
            Id = c.Id,
            Name = c.Name,
            Slug = c.Slug,
            PrimaryDomain = c.PrimaryDomain,
            DatabaseName = c.DatabaseName,
            IsActive = c.IsActive,
            SchemaVersion = c.SchemaVersion,
            LastMigrationAt = c.LastMigrationAt,
            LastMigrationResult = c.LastMigrationResult,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
        return View(vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("customers/{id:guid}/activate")]
    public async Task<IActionResult> ActivateCustomer(Guid id, CancellationToken ct)
    {
        var ok = await _customers.SetCustomerActiveStateAsync(id, isActive: true, ct).ConfigureAwait(false);
        if (!ok)
            return NotFound();
        return RedirectToAction(nameof(CustomerDetails), new { id });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("customers/{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateCustomer(Guid id, CancellationToken ct)
    {
        var ok = await _customers.SetCustomerActiveStateAsync(id, isActive: false, ct).ConfigureAwait(false);
        if (!ok)
            return NotFound();
        return RedirectToAction(nameof(CustomerDetails), new { id });
    }

    private bool IsCentralAdminConfigMissing()
    {
        var email = _configuration["CentralAdmin:Email"]?.Trim();
        var hash = _configuration["CentralAdmin:PasswordHash"]?.Trim();
        return string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(hash);
    }
}
