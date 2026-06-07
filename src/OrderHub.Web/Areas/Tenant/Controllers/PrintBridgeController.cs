using System.IO.Compression;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Orders;
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
    private readonly ICustomerOrderSettingsService _orderSettings;
    private readonly IValidator<UpdateCustomerOrderSettingsCommand> _orderSettingsValidator;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public PrintBridgeController(
        ICurrentCustomerService currentCustomer,
        IPrintBridgeDeviceManagementService devices,
        ICustomerOrderSettingsService orderSettings,
        IValidator<UpdateCustomerOrderSettingsCommand> orderSettingsValidator,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _devices = devices;
        _orderSettings = orderSettings;
        _orderSettingsValidator = orderSettingsValidator;
        _environment = environment;
        _configuration = configuration;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);
        var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);
        var apiBaseUrl = ResolveApiBaseUrl();

        return View(BuildPageViewModel(deviceRows, quota, apiBaseUrl));
    }

    [HttpGet("download")]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);
        var orderSettings = await _orderSettings.GetAsync(customer.Id, ct).ConfigureAwait(false);
        var apiBaseUrl = ResolveApiBaseUrl();

        return View("Setup", new PrintBridgeSetupViewModel
        {
            PackageDownloadUrl = Url.Action(nameof(DownloadPackage), "PrintBridge", new { area = AreaNames.Tenant })
                ?? "/print-bridge/download/package",
            DevicesUrl = Url.Action(nameof(Index), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge",
            ApiBaseUrl = apiBaseUrl,
            ExampleConfigJson = BuildExampleConfigJson(apiBaseUrl),
            AutoApproveNewOrders = orderSettings.AutoApproveNewOrders,
            AutoPrintReceiptOnAutoApprove = orderSettings.AutoPrintReceiptOnAutoApprove,
            ReceiptPrintCopyCount = orderSettings.ReceiptPrintCopyCount,
            AllowedActiveDeviceCount = quota.AllowedActiveDeviceCount,
            ActiveDeviceCount = quota.ActiveDeviceCount,
            CanCreateActiveDevice = quota.CanCreateActiveDevice,
            ActiveCountExceedsLimit = quota.ActiveCountExceedsLimit
        });
    }

    [HttpGet("devices")]
    public async Task<IActionResult> ListDevices(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);
        var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);

        return Ok(new
        {
            devices = deviceRows.Select(MapDeviceJson),
            quota = MapQuotaJson(quota, deviceRows)
        });
    }

    [HttpGet("order-settings")]
    public async Task<IActionResult> GetOrderSettings(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var r = await _orderSettings.GetAsync(customer.Id, ct).ConfigureAwait(false);
        return Ok(new
        {
            autoApproveNewOrders = r.AutoApproveNewOrders,
            autoPrintReceiptOnAutoApprove = r.AutoPrintReceiptOnAutoApprove,
            receiptPrintCopyCount = r.ReceiptPrintCopyCount
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("order-settings")]
    public async Task<IActionResult> UpdateOrderSettings(
        [FromForm] bool autoApproveNewOrders,
        [FromForm] bool autoPrintReceiptOnAutoApprove,
        [FromForm] int receiptPrintCopyCount,
        CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var command = new UpdateCustomerOrderSettingsCommand(
            autoApproveNewOrders,
            autoPrintReceiptOnAutoApprove,
            receiptPrintCopyCount);

        var validation = await _orderSettingsValidator.ValidateAsync(command, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors.FirstOrDefault();
            var messageKey = firstError?.ErrorMessage ?? "Orders.OrderSettingsUpdateFailed";
            return BadRequest(new { message = _localizer[messageKey].Value });
        }

        try
        {
            var r = await _orderSettings.UpdateAsync(customer.Id, command, ct).ConfigureAwait(false);
            return Ok(new
            {
                autoApproveNewOrders = r.AutoApproveNewOrders,
                autoPrintReceiptOnAutoApprove = r.AutoPrintReceiptOnAutoApprove,
                receiptPrintCopyCount = r.ReceiptPrintCopyCount,
                message = _localizer["Orders.OrderSettingsSaved"].Value
            });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = _localizer["Orders.OrderSettingsUpdateFailed"].Value });
        }
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/create")]
    public async Task<IActionResult> CreateDevice([FromForm] string? deviceName, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        try
        {
            var result = await _devices.CreateDeviceAsync(customer.Id, deviceName ?? string.Empty, ct)
                .ConfigureAwait(false);

            var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);
            var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);

            return Ok(new
            {
                success = true,
                deviceId = result.DeviceId,
                deviceName = result.DeviceName,
                token = result.RawToken,
                tokenMode = "create",
                message = _localizer["PrintBridge.DeviceCreatedSuccessfully"].Value,
                tokenTitle = _localizer["PrintBridge.TokenCreated"].Value,
                tokenNotice = _localizer["PrintBridge.TokenShownOnce"].Value,
                devices = deviceRows.Select(MapDeviceJson),
                quota = MapQuotaJson(quota, deviceRows)
            });
        }
        catch (PrintBridgeDeviceLimitReachedException)
        {
            return BadRequest(new
            {
                success = false,
                message = _localizer["PrintBridge.DeviceLimitReached", PrintBridgeDeviceLimits.AllowedActiveDeviceCount].Value
            });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { success = false, message = _localizer["PrintBridge.TokenGenerateFailed"].Value });
        }
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/{deviceId:guid}/regenerate-token")]
    public async Task<IActionResult> RegenerateToken(Guid deviceId, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        try
        {
            var result = await _devices.RegenerateTokenAsync(customer.Id, deviceId, ct).ConfigureAwait(false);
            var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);
            var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);

            return Ok(new
            {
                success = true,
                deviceId = result.DeviceId,
                deviceName = result.DeviceName,
                token = result.RawToken,
                tokenMode = "regenerate",
                message = _localizer["PrintBridge.TokenRegenerated"].Value,
                tokenTitle = _localizer["PrintBridge.TokenRegenerated"].Value,
                tokenNotice = _localizer["PrintBridge.TokenShownOnce"].Value,
                tokenWarning = _localizer["PrintBridge.OldTokenInvalidAfterRegenerate"].Value,
                devices = deviceRows.Select(MapDeviceJson),
                quota = MapQuotaJson(quota, deviceRows)
            });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { success = false, message = _localizer["PrintBridge.TokenRegenerateFailed"].Value });
        }
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/{deviceId:guid}/set-active")]
    public async Task<IActionResult> SetDeviceActive(Guid deviceId, [FromForm] bool isActive, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        try
        {
            var ok = await _devices.SetDeviceActiveAsync(customer.Id, deviceId, isActive, ct).ConfigureAwait(false);
            if (!ok)
                return NotFound(new { success = false, message = _localizer["PrintBridge.DeviceUpdateFailed"].Value });

            var deviceRows = await _devices.ListDevicesAsync(customer.Id, ct).ConfigureAwait(false);
            var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);

            return Ok(new
            {
                success = true,
                isActive,
                devices = deviceRows.Select(MapDeviceJson),
                quota = MapQuotaJson(quota, deviceRows)
            });
        }
        catch (PrintBridgeDeviceLimitReachedException)
        {
            return BadRequest(new
            {
                success = false,
                message = _localizer["PrintBridge.DeviceLimitReached", PrintBridgeDeviceLimits.AllowedActiveDeviceCount].Value
            });
        }
    }

    [HttpGet("download/package")]
    public IActionResult DownloadPackage()
    {
        var folder = Path.Combine(_environment.WebRootPath, "downloads", "orderhub-print-bridge");
        if (!Directory.Exists(folder))
            return NotFound();

        var files = Directory.GetFiles(folder)
            .Where(f => !string.Equals(Path.GetFileName(f), ".gitkeep", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
            return NotFound();

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in files)
            {
                var entryName = Path.GetFileName(filePath);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                using var fileStream = System.IO.File.OpenRead(filePath);
                fileStream.CopyTo(entryStream);
            }
        }

        memory.Position = 0;
        return File(memory.ToArray(), "application/zip", "OrderHub-PrintBridge-Placeholder.zip");
    }

    private PrintBridgePageViewModel BuildPageViewModel(
        IReadOnlyList<PrintBridgeDeviceSummaryDto> deviceRows,
        PrintBridgeDeviceQuotaDto quota,
        string apiBaseUrl) =>
        new()
        {
            SetupUrl = Url.Action(nameof(Setup), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/download",
            Devices = deviceRows.Select(MapDevice).ToList(),
            AllowedActiveDeviceCount = quota.AllowedActiveDeviceCount,
            ActiveDeviceCount = quota.ActiveDeviceCount,
            CanCreateActiveDevice = quota.CanCreateActiveDevice,
            ActiveCountExceedsLimit = quota.ActiveCountExceedsLimit
        };

    private string ResolveApiBaseUrl()
    {
        var configured = _configuration["OrderHub:ApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        return "https://your-orderhub-api.example.com";
    }

    private static string BuildExampleConfigJson(string apiBaseUrl)
    {
        var config = new
        {
            OrderHub = new
            {
                BaseUrl = apiBaseUrl,
                AgentToken = "YOUR_TOKEN_HERE"
            },
            PrintBridge = new
            {
                PrinterMode = "WindowsPrinter",
                PrinterName = "POS-58",
                IdlePollIntervalSeconds = 5,
                BusyPollIntervalSeconds = 1,
                ErrorPollIntervalSeconds = 15,
                MaxJobsPerPoll = 3,
                DryRun = false,
                BridgeName = "Kitchen-PC"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static PrintBridgeDeviceRowViewModel MapDevice(PrintBridgeDeviceSummaryDto d) =>
        new()
        {
            Id = d.Id,
            Name = d.Name,
            IsActive = d.IsActive,
            IsConnected = d.IsConnected,
            LastSeenAtUtc = d.LastSeenAtUtc,
            MachineName = d.MachineName,
            PrinterName = d.PrinterName,
            AppVersion = d.AppVersion
        };

    private static object MapDeviceJson(PrintBridgeDeviceSummaryDto d) =>
        new
        {
            id = d.Id,
            name = d.Name,
            isActive = d.IsActive,
            isConnected = d.IsConnected,
            lastSeenAtUtc = d.LastSeenAtUtc,
            machineName = d.MachineName,
            printerName = d.PrinterName,
            appVersion = d.AppVersion
        };

    private static object MapQuotaJson(
        PrintBridgeDeviceQuotaDto quota,
        IReadOnlyList<PrintBridgeDeviceSummaryDto>? devices = null)
    {
        var connectedCount = devices?.Count(d => d.IsActive && d.IsConnected) ?? 0;
        DateTime? latestLastSeen = devices?
            .Where(d => d.LastSeenAtUtc.HasValue)
            .Select(d => d.LastSeenAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestLastSeen == default)
            latestLastSeen = null;

        return new
        {
            allowedActiveDeviceCount = quota.AllowedActiveDeviceCount,
            activeDeviceCount = quota.ActiveDeviceCount,
            canCreateActiveDevice = quota.CanCreateActiveDevice,
            activeCountExceedsLimit = quota.ActiveCountExceedsLimit,
            connectedDeviceCount = connectedCount,
            latestLastSeenAtUtc = latestLastSeen
        };
    }
}
