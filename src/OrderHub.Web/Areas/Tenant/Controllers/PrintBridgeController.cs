using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Orders;
using OrderHub.Application.Time;
using OrderHub.Domain.Enums;
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
    private readonly IPrintJobHistoryService _printJobHistory;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public PrintBridgeController(
        ICurrentCustomerService currentCustomer,
        IPrintBridgeDeviceManagementService devices,
        IPrintJobHistoryService printJobHistory,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _devices = devices;
        _printJobHistory = printJobHistory;
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
        var printJobs = await _printJobHistory
            .GetRecentReceiptJobsAsync(customer.Id, PrintJobHistoryLimits.Default, ct)
            .ConfigureAwait(false);

        return View(BuildPageViewModel(deviceRows, quota, printJobs));
    }

    [HttpGet("download")]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var quota = await _devices.GetDeviceQuotaAsync(customer.Id, ct).ConfigureAwait(false);
        var apiBaseUrl = ResolveApiBaseUrl();

        return View("Setup", new PrintBridgeSetupViewModel
        {
            PackageDownloadUrl = Url.Action(nameof(DownloadPackage), "PrintBridge", new { area = AreaNames.Tenant })
                ?? "/print-bridge/download/package",
            DevicesUrl = Url.Action(nameof(Index), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge",
            ApiBaseUrl = apiBaseUrl,
            ExampleConfigJson = BuildExampleConfigJson(apiBaseUrl),
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

    [HttpGet("print-jobs")]
    public async Task<IActionResult> ListPrintJobs(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var vm = await BuildPrintJobHistoryViewModelAsync(customer.Id, ct).ConfigureAwait(false);
        return PartialView("_PrintJobHistory", vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("print-jobs/{jobId:guid}/reprint")]
    public async Task<IActionResult> ReprintJob(Guid jobId, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _printJobHistory
            .CreateReprintAsync(customer.Id, jobId, customer.Name, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return BadRequest(new
            {
                success = false,
                message = _localizer[result.MessageKey].Value
            });
        }

        return Ok(new
        {
            success = true,
            message = _localizer[result.MessageKey].Value,
            newPrintJobId = result.NewPrintJobId
        });
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
        IReadOnlyList<PrintJobHistoryItemDto> printJobs)
    {
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var turkeyToday = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
        var printJobsTodayCount = printJobs.Count(j =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(j.CreatedAtUtc, DateTimeKind.Utc),
                tz);
            return DateOnly.FromDateTime(local) == turkeyToday;
        });

        var lastConnected = deviceRows
            .Where(d => d.LastSeenAtUtc.HasValue)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .FirstOrDefault();

        return new PrintBridgePageViewModel
        {
            SetupUrl = Url.Action(nameof(Setup), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/download",
            PackageDownloadUrl = Url.Action(nameof(DownloadPackage), "PrintBridge", new { area = AreaNames.Tenant })
                ?? "/print-bridge/download/package",
            ReceiptPrinterSettingsUrl = Url.Action(nameof(ReceiptPrinterSettingsController.Index), "ReceiptPrinterSettings", new { area = AreaNames.Tenant })
                ?? "/settings/receipt-printer",
            Devices = deviceRows.Select(MapDevice).ToList(),
            AllowedActiveDeviceCount = quota.AllowedActiveDeviceCount,
            ActiveDeviceCount = quota.ActiveDeviceCount,
            CanCreateActiveDevice = quota.CanCreateActiveDevice,
            ActiveCountExceedsLimit = quota.ActiveCountExceedsLimit,
            PrintJobsTodayCount = printJobsTodayCount,
            LastConnectedDeviceName = lastConnected?.Name,
            LastConnectedAtUtc = lastConnected?.LastSeenAtUtc,
            PrintJobs = printJobs.Select(MapPrintJob).ToList()
        };
    }

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
            ConnectionStatus = d.ConnectionStatus,
            ConnectionStatusLabelKey = d.ConnectionStatusLabelKey,
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
            connectionStatus = d.ConnectionStatus.ToString(),
            connectionStatusLabelKey = d.ConnectionStatusLabelKey,
            isConnected = d.IsConnected,
            lastSeenAtUtc = d.LastSeenAtUtc,
            machineName = d.MachineName,
            printerName = d.PrinterName,
            appVersion = d.AppVersion
        };

    private async Task<PrintJobHistoryListViewModel> BuildPrintJobHistoryViewModelAsync(
        Guid customerId,
        CancellationToken ct)
    {
        var jobs = await _printJobHistory
            .GetRecentReceiptJobsAsync(customerId, PrintJobHistoryLimits.Default, ct)
            .ConfigureAwait(false);

        return new PrintJobHistoryListViewModel
        {
            Jobs = jobs.Select(MapPrintJob).ToList()
        };
    }

    private PrintJobHistoryRowViewModel MapPrintJob(PrintJobHistoryItemDto job) =>
        new()
        {
            Id = job.Id,
            OrderId = job.OrderId,
            OrderDisplay = ResolveOrderDisplay(job),
            ExternalOrderId = job.ExternalOrderId,
            Platform = job.Platform.ToString(),
            PlatformDisplayName = LocalizePlatform(job.Platform),
            Status = job.Status.ToString(),
            StatusLabelKey = job.StatusLabelKey,
            CreatedAtUtc = job.CreatedAtUtc,
            LastAttemptAtUtc = job.LastAttemptAtUtc,
            PrintedAtUtc = job.PrintedAtUtc,
            AttemptCount = job.AttemptCount,
            ErrorMessage = job.ErrorMessage,
            LockedBy = job.LockedBy,
            OrderCustomerName = job.OrderCustomerName,
            TotalAmount = job.TotalAmount,
            CanReprint = job.CanReprint
        };

    private static string ResolveOrderDisplay(PrintJobHistoryItemDto job) =>
        !string.IsNullOrWhiteSpace(job.ExternalOrderCode)
            ? job.ExternalOrderCode.Trim()
            : !string.IsNullOrWhiteSpace(job.ExternalOrderId)
                ? job.ExternalOrderId.Trim()
                : job.OrderId.ToString();

    private string LocalizePlatform(FoodPlatform platform) =>
        platform switch
        {
            FoodPlatform.Yemeksepeti => _localizer["Orders.PlatformYemeksepeti"].Value,
            FoodPlatform.GetirYemek => _localizer["Orders.PlatformGetirYemek"].Value,
            FoodPlatform.TrendyolYemek => _localizer["Orders.PlatformTrendyolYemek"].Value,
            _ => platform.ToString()
        };

    private static object MapQuotaJson(
        PrintBridgeDeviceQuotaDto quota,
        IReadOnlyList<PrintBridgeDeviceSummaryDto>? devices = null)
    {
        var connectedCount = devices?.Count(d => d.ConnectionStatus == PrintBridgeConnectionStatus.Connected) ?? 0;
        DateTime? latestLastSeen = devices?
            .Where(d => d.LastSeenAtUtc.HasValue)
            .Select(d => d.LastSeenAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestLastSeen == default)
            latestLastSeen = null;

        var lastConnected = devices?
            .Where(d => d.LastSeenAtUtc.HasValue)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .FirstOrDefault();

        return new
        {
            allowedActiveDeviceCount = quota.AllowedActiveDeviceCount,
            activeDeviceCount = quota.ActiveDeviceCount,
            canCreateActiveDevice = quota.CanCreateActiveDevice,
            activeCountExceedsLimit = quota.ActiveCountExceedsLimit,
            connectedDeviceCount = connectedCount,
            latestLastSeenAtUtc = latestLastSeen,
            lastConnectedDeviceName = lastConnected?.Name
        };
    }
}
