using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.Settings;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;
using ValidationException = FluentValidation.ValidationException;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("settings/receipt-printer")]
public sealed class ReceiptPrinterSettingsController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly IReceiptTemplateSettingsService _templateSettings;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public ReceiptPrinterSettingsController(
        ICurrentCustomerService currentCustomer,
        IPrintBridgeDeviceManagementService devices,
        IReceiptTemplateSettingsService templateSettings,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _devices = devices;
        _templateSettings = templateSettings;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);

        return View(new ReceiptPrinterSettingsPageViewModel
        {
            CustomerDisplayName = customer.Name,
            PrintBridgeStatus = BuildPrintBridgeStatus(deviceRows)
        });
    }

    [HttpGet("template-settings")]
    public async Task<IActionResult> GetTemplateSettings(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var settings = await _templateSettings
            .GetAsync(customer.Id, customer.Name, ResolveDefaultReceiptLanguage(), ct)
            .ConfigureAwait(false);

        return Ok(MapTemplateResponse(settings));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("template-settings")]
    public async Task<IActionResult> UpdateTemplateSettings(
        [FromBody] UpdateReceiptTemplateSettingsCommand command,
        CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        try
        {
            var settings = await _templateSettings
                .UpdateAsync(customer.Id, customer.Name, command, ct)
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
