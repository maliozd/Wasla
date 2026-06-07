using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.PrintBridge;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("print-bridge")]
public sealed class PrintBridgeController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public PrintBridgeController(
        ICurrentCustomerService currentCustomer,
        IPrintBridgeDeviceManagementService devices,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _devices = devices;
        _localizer = localizer;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        return View(new PrintBridgeHelpViewModel
        {
            DownloadUrl = "#",
            ApiBaseUrlHint = "/api/print-bridge"
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("generate-token")]
    public async Task<IActionResult> GenerateToken(
        [FromForm] string? deviceName,
        CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        try
        {
            var name = string.IsNullOrWhiteSpace(deviceName)
                ? Environment.MachineName
                : deviceName.Trim();

            var result = await _devices.GenerateTokenAsync(customer.Id, name, ct).ConfigureAwait(false);

            return Ok(new
            {
                success = true,
                deviceId = result.DeviceId,
                deviceName = result.DeviceName,
                token = result.RawToken,
                message = _localizer["PrintBridge.TokenGeneratedOnce"].Value
            });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { success = false, message = _localizer["PrintBridge.TokenGenerateFailed"].Value });
        }
    }
}
