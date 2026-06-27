using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Settings;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using ValidationException = FluentValidation.ValidationException;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantSettings)]
[Route("settings/receipt-printer")]
public sealed class ReceiptPrinterSettingsController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly IReceiptTemplateSettingsService _templateSettings;
    private readonly IStringLocalizer<Wasla.Web.SharedResource> _localizer;

    public ReceiptPrinterSettingsController(
        ICurrentTenantService currentTenant,
        IPrintBridgeDeviceManagementService devices,
        IReceiptTemplateSettingsService templateSettings,
        IStringLocalizer<Wasla.Web.SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _devices = devices;
        _templateSettings = templateSettings;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);

        return View(new ReceiptPrinterSettingsPageViewModel
        {
            CustomerDisplayName = tenant.Name,
            PrintBridgeStatus = BuildPrintBridgeStatus(deviceRows)
        });
    }

    [HttpGet("template-settings")]
    public async Task<IActionResult> GetTemplateSettings(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var settings = await _templateSettings
            .GetAsync(tenant.Id, tenant.Name, ResolveDefaultReceiptLanguage(), ct)
            .ConfigureAwait(false);

        return Ok(MapTemplateResponse(settings));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("template-settings")]
    public async Task<IActionResult> UpdateTemplateSettings(
        [FromBody] UpdateReceiptTemplateSettingsCommand command,
        CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        try
        {
            var settings = await _templateSettings
                .UpdateAsync(tenant.Id, tenant.Name, command, ct)
                .ConfigureAwait(false);

            return Ok(MapTemplateResponse(settings));
        }
        catch (ValidationException ex)
        {
            var firstError = ex.Errors.FirstOrDefault();
            var messageKey = firstError?.ErrorMessage ?? "Settings.SaveFailed";
            return BadRequest(new { message = _localizer[messageKey].Value });
        }
    }

    private static object MapTemplateResponse(ReceiptTemplateSettings settings) => new
    {
        showRestaurantName = settings.ShowRestaurantName,
        showPlatformName = settings.ShowPlatformName,
        showReceivedTime = settings.ShowReceivedTime,
        showCustomerName = settings.ShowCustomerName,
        showCustomerPhone = settings.ShowCustomerPhone,
        showDeliveryAddress = settings.ShowDeliveryAddress,
        showProductNotes = settings.ShowProductNotes,
        showProductOptions = settings.ShowProductOptions,
        showSubtotal = settings.ShowSubtotal,
        showDiscount = settings.ShowDiscount,
        showDeliveryFee = settings.ShowDeliveryFee,
        showPaymentMethod = settings.ShowPaymentMethod,
        showFooterMessage = settings.ShowFooterMessage,
        receiptLanguage = settings.ReceiptLanguage,
        receiptHeaderText = settings.ReceiptHeaderText,
        receiptFooterText = settings.ReceiptFooterText
    };

    private static string ResolveDefaultReceiptLanguage() =>
        ReceiptLanguageCodes.Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    private static PrintBridgeStatusSummaryViewModel? BuildPrintBridgeStatus(
        IReadOnlyList<PrintBridgeDeviceSummaryDto> devices)
    {
        if (devices.Count == 0) return null;

        var primary = devices
            .OrderByDescending(d => d.IsActive)
            .ThenByDescending(d => d.ConnectionStatus == PrintBridgeConnectionStatus.Connected)
            .ThenByDescending(d => d.LastSeenAtUtc ?? DateTime.MinValue)
            .First();

        return new PrintBridgeStatusSummaryViewModel
        {
            HasDevices = true,
            DeviceName = primary.Name,
            ConnectionStatusLabelKey = primary.ConnectionStatusLabelKey,
            LastSeenAtUtc = primary.LastSeenAtUtc
        };
    }
}
