using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Branding;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Settings;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using Wasla.Web.Ui;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantSettings)]
[Route("settings/account")]
public sealed class AccountSettingsController : BaseController
{
    // Room for the multipart envelope around an oversized logo: a file up to about this size gets the localized
    // "too large" message; a larger request is refused before it is read.
    private const long LogoRequestLimitBytes = 8 * 1024 * 1024;

    private readonly ICurrentTenantService _currentTenant;
    private readonly IRestaurantLogoService _logos;
    private readonly IStringLocalizer<Wasla.Web.SharedResource> _localizer;

    public AccountSettingsController(
        ICurrentTenantService currentTenant,
        IRestaurantLogoService logos,
        IStringLocalizer<Wasla.Web.SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _logos = logos;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var logo = await _logos.GetCurrentAsync(tenant.Id, ct).ConfigureAwait(false);
        return View(new AccountSettingsViewModel
        {
            RestaurantName = tenant.Name,
            RestaurantLogoUrl = Url.RestaurantLogo(logo?.Version),
            RestaurantLogoBackground = logo?.Background ?? RestaurantLogoBackground.ThemeAware
        });
    }

    /// <summary>
    /// Changes what the Live Screen header draws behind the current logo. Only the four style tokens are accepted;
    /// anything else is refused with 400 and changes nothing. The image and its URL stay the same.
    /// </summary>
    [ValidateAntiForgeryToken]
    [HttpPost("logo/background")]
    public async Task<IActionResult> SetLogoBackground([FromForm(Name = "background")] string? background, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();
        if (!RestaurantLogoBackgrounds.TryParse(background, out var value))
            return BadRequest();

        if (await _logos.SetBackgroundAsync(tenant.Id, value, ct).ConfigureAwait(false))
            TempData["Success"] = _localizer["Settings.RestaurantLogo.Background.Saved"].Value;
        else
            TempData["Error"] = _localizer["Settings.RestaurantLogo.Background.NoLogo"].Value;
        return RedirectToAction(nameof(Index));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("logo")]
    [RequestSizeLimit(LogoRequestLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = LogoRequestLimitBytes)]
    public async Task<IActionResult> UploadLogo(IFormFile? logo, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        RestaurantLogoError error;
        if (logo is null || logo.Length == 0)
        {
            error = RestaurantLogoError.Empty;
        }
        else if (logo.Length > RestaurantLogoImage.MaxBytes)
        {
            error = RestaurantLogoError.TooLarge;
        }
        else
        {
            // The file name and the browser's media type are ignored; the service checks the bytes themselves.
            using var buffer = new MemoryStream((int)logo.Length);
            await logo.CopyToAsync(buffer, ct).ConfigureAwait(false);
            error = (await _logos.SaveAsync(tenant.Id, buffer.ToArray(), ct).ConfigureAwait(false)).Error;
        }

        if (error == RestaurantLogoError.None)
            TempData["Success"] = _localizer["Settings.RestaurantLogo.Saved"].Value;
        else
            TempData["Error"] = ErrorMessage(error);
        return RedirectToAction(nameof(Index));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("logo/remove")]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        await _logos.RemoveAsync(tenant.Id, ct).ConfigureAwait(false);
        TempData["Success"] = _localizer["Settings.RestaurantLogo.Removed"].Value;
        return RedirectToAction(nameof(Index));
    }

    private string ErrorMessage(RestaurantLogoError error) => error switch
    {
        RestaurantLogoError.TooLarge => _localizer["Settings.RestaurantLogo.Error.TooLarge",
            (RestaurantLogoImage.MaxBytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture)].Value,
        RestaurantLogoError.UnsupportedFormat => _localizer["Settings.RestaurantLogo.Error.UnsupportedFormat"].Value,
        RestaurantLogoError.InvalidImage => _localizer["Settings.RestaurantLogo.Error.Invalid"].Value,
        RestaurantLogoError.DimensionsTooLarge => _localizer["Settings.RestaurantLogo.Error.Dimensions",
            RestaurantLogoImage.MaxDimension.ToString(CultureInfo.CurrentCulture)].Value,
        _ => _localizer["Settings.RestaurantLogo.Error.Required"].Value
    };
}
