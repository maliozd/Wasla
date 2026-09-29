using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Application.GuidedSetup;
using Wasla.Web.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.PrintBridge;
using Wasla.Web.PrintBridge;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("print-bridge")]
public sealed class PrintBridgeController : BaseController
{
    /// <summary>Custom protocol scheme the desktop app registers for browser-to-application setup.</summary>
    public const string ProtocolScheme = "wasla-printbridge";

    private readonly ICurrentTenantService _currentTenant;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly IPrintBridgeSetupSessionService _setupSessions;
    private readonly IPrintJobHistoryService _printJobHistory;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<Wasla.Web.SharedResource> _localizer;
    private readonly IGuidedSetupCoordinator _guidedSetup;

    public PrintBridgeController(
        ICurrentTenantService currentTenant,
        IPrintBridgeDeviceManagementService devices,
        IPrintBridgeSetupSessionService setupSessions,
        IPrintJobHistoryService printJobHistory,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IStringLocalizer<Wasla.Web.SharedResource> localizer,
        IGuidedSetupCoordinator guidedSetup)
    {
        _currentTenant = currentTenant;
        _devices = devices;
        _setupSessions = setupSessions;
        _printJobHistory = printJobHistory;
        _environment = environment;
        _configuration = configuration;
        _localizer = localizer;
        _guidedSetup = guidedSetup;
    }

    [HttpGet("")]
    public IActionResult Index() =>
        RedirectToActionPermanent(nameof(Devices));

    [HttpGet("devices")]
    [Authorize(Policy = TenantPolicies.CanManagePrintBridgeDevices)]
    public async Task<IActionResult> Devices(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);
        var quota = await _devices.GetDeviceQuotaAsync(tenant.Id, ct).ConfigureAwait(false);
        var printJobs = await _printJobHistory
            .GetRecentReceiptJobsAsync(tenant.Id, PrintJobHistoryLimits.Default, ct)
            .ConfigureAwait(false);

        return View(BuildPageViewModel(deviceRows, quota, printJobs));
    }

    [HttpGet("devices/{id:guid}")]
    [Authorize(Policy = TenantPolicies.CanManagePrintBridgeDevices)]
    public async Task<IActionResult> DeviceDetails(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var device = await _devices.GetDeviceDetailsAsync(tenant.Id, id, ct).ConfigureAwait(false);
        if (device is null) return NotFound();

        return View("DeviceDetails", MapDeviceDetails(device));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/{id:guid}/rename")]
    [Authorize(Policy = TenantPolicies.CanManagePrintBridgeDevices)]
    public async Task<IActionResult> RenameDevice(Guid id, [FromForm] string? name, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _devices
            .UpdateDeviceNameAsync(tenant.Id, id, name ?? string.Empty, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "PrintBridge.DeviceNotFound", StringComparison.Ordinal))
                return NotFound();

            TempData["PrintBridgeDeviceError"] = _localizer[result.ErrorKey ?? "PrintBridge.DeviceRenameFailed"].Value;
            return RedirectToAction(nameof(DeviceDetails), new { id });
        }

        TempData["PrintBridgeDeviceMessage"] = _localizer["PrintBridge.DeviceRenameSucceeded"].Value;
        return RedirectToAction(nameof(DeviceDetails), new { id });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/{id:guid}/remove")]
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public async Task<IActionResult> RemoveDevice(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _devices.RemoveDeviceAsync(tenant.Id, id, ct).ConfigureAwait(false);
        if (result.Outcome == RemovePrintBridgeDeviceOutcome.NotFound)
            return NotFound();

        if (!result.Success)
        {
            TempData["PrintBridgeDeviceError"] = _localizer[result.MessageKey].Value;
            return RedirectToAction(nameof(DeviceDetails), new { id });
        }

        TempData["PrintBridgeDeviceMessage"] = _localizer[result.MessageKey].Value;
        return RedirectToAction(nameof(Devices));
    }

    [HttpGet("setup")]
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);
        var activeDevice = deviceRows.FirstOrDefault(d => d.IsActive);
        var packagePath = PrintBridgePackagePaths.ResolvePackagePath(_configuration, _environment);
        var packageFileName = PrintBridgePackagePaths.GetPackageFileName(_configuration);
        var packageAvailable = System.IO.File.Exists(packagePath);
        var guidedSetup = await _guidedSetup
            .GetSectionPanelAsync(tenant.Id, CurrentUserId, User, GuidedSetupSections.PrintBridge, ct)
            .ConfigureAwait(false);

        return View(new PrintBridgeSetupViewModel
        {
            GuidedSetup = guidedSetup,
            PackageDownloadUrl = Url.Action(nameof(DownloadPackage), "PrintBridge", new { area = AreaNames.Tenant })
                ?? "/print-bridge/download/package",
            DevicesUrl = Url.Action(nameof(Devices), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/devices",
            ServerUrl = ResolveCustomerWebBaseUrl(),
            PackageAvailable = packageAvailable,
            PackageFileName = packageFileName,
            HasActiveDevice = activeDevice is not null,
            ActiveDeviceId = activeDevice?.Id,
            ActiveDeviceName = activeDevice?.Name,
            Devices = deviceRows.Select(d => new PrintBridgeSetupDeviceOptionViewModel
            {
                Id = d.Id,
                Name = d.Name,
                MachineName = d.MachineName,
                IsActive = d.IsActive,
                ConnectionStatusLabelKey = d.ConnectionStatusLabelKey,
                LastSeenAtUtc = d.LastSeenAtUtc
            }).ToList(),
            IsDevelopment = _environment.IsDevelopment()
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("setup/session")]
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public async Task<IActionResult> CreateSetupSession(
        [FromForm] string? setupMode,
        [FromForm] Guid? deviceId,
        [FromForm] bool confirmReplaceActiveToken,
        CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var serverUrl = ResolveCustomerWebBaseUrl();
        if (string.IsNullOrWhiteSpace(serverUrl))
            return BadRequest(new { success = false, message = _localizer["PrintBridge.Auto.ServerUrlUnavailable"].Value });

        if (!TryParseSetupMode(setupMode, out var parsedMode))
        {
            return BadRequest(new { success = false, message = _localizer["PrintBridge.Auto.SetupModeRequired"].Value });
        }

        try
        {
            var created = await _setupSessions
                .CreateSessionAsync(
                    tenant.Id,
                    parsedMode,
                    deviceId,
                    serverUrl,
                    tenant.Name,
                    confirmReplaceActiveToken,
                    ct)
                .ConfigureAwait(false);

            var protocolUrl =
                $"{ProtocolScheme}://setup?server={Uri.EscapeDataString(serverUrl)}&code={Uri.EscapeDataString(created.Code)}";

            var statusUrl = Url.Action(nameof(SetupSessionStatus), "PrintBridge",
                new { area = AreaNames.Tenant, sessionId = created.SessionId })
                ?? $"/print-bridge/setup/session/{created.SessionId}/status";

            return Ok(new
            {
                success = true,
                sessionId = created.SessionId,
                code = created.Code,
                protocolUrl,
                statusUrl,
                deviceName = created.DeviceName,
                expiresAtUtc = created.ExpiresAtUtc
            });
        }
        catch (PrintBridgeSetupTokenReplacementConfirmationRequiredException)
        {
            return Conflict(new
            {
                success = false,
                requiresConfirmation = true,
                message = _localizer["PrintBridge.Auto.ReplaceTokenConfirmationRequired"].Value
            });
        }
        catch (PrintBridgeSetupDeviceSelectionRequiredException)
        {
            return BadRequest(new
            {
                success = false,
                message = _localizer["PrintBridge.Auto.DeviceSelectionRequired"].Value
            });
        }
        catch (PrintBridgeSetupDeviceNotFoundException)
        {
            return BadRequest(new
            {
                success = false,
                message = _localizer["PrintBridge.Auto.SelectedDeviceInvalid"].Value
            });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { success = false, message = _localizer["PrintBridge.Auto.DeviceUnavailable"].Value });
        }
    }

    [HttpGet("setup/session/{sessionId:guid}/status")]
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public async Task<IActionResult> SetupSessionStatus(Guid sessionId, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var status = await _setupSessions.GetStatusAsync(tenant.Id, sessionId, ct).ConfigureAwait(false);
        if (status is null) return NotFound(new { success = false });

        return Ok(new
        {
            success = true,
            status = status.Status.ToString(),
            connectionVerified = status.ConnectionVerified,
            expiresAtUtc = status.ExpiresAtUtc,
            message = MapSetupFailureMessage(status.FailureReason)
        });
    }

    private string? MapSetupFailureMessage(string? failureReason) =>
        string.Equals(
            failureReason,
            PrintBridgeSetupFailureReasons.InstallationAlreadyRegistered,
            StringComparison.Ordinal)
            ? _localizer["PrintBridge.Auto.InstallationAlreadyRegistered"].Value
            : null;

    [HttpGet("download")]
    public IActionResult DownloadRedirect() =>
        RedirectToActionPermanent(nameof(Setup));

    [HttpGet("devices/list")]
    [Authorize(Policy = TenantPolicies.CanManagePrintBridgeDevices)]
    public async Task<IActionResult> ListDevices(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);
        var quota = await _devices.GetDeviceQuotaAsync(tenant.Id, ct).ConfigureAwait(false);

        return Ok(new
        {
            devices = deviceRows.Select(MapDeviceJson),
            quota = MapQuotaJson(quota, deviceRows)
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("devices/{deviceId:guid}/regenerate-token")]
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public async Task<IActionResult> RegenerateToken(Guid deviceId, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        try
        {
            var result = await _devices.RegenerateTokenAsync(tenant.Id, deviceId, ct).ConfigureAwait(false);
            var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);
            var quota = await _devices.GetDeviceQuotaAsync(tenant.Id, ct).ConfigureAwait(false);

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
    [Authorize(Policy = TenantPolicies.CanManagePrintBridgeDevices)]
    public async Task<IActionResult> SetDeviceActive(Guid deviceId, [FromForm] bool isActive, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        try
        {
            var ok = await _devices.SetDeviceActiveAsync(tenant.Id, deviceId, isActive, ct).ConfigureAwait(false);
            if (!ok)
                return NotFound(new { success = false, message = _localizer["PrintBridge.DeviceUpdateFailed"].Value });

            var deviceRows = await _devices.ListDevicesAsync(tenant.Id, ct).ConfigureAwait(false);
            var quota = await _devices.GetDeviceQuotaAsync(tenant.Id, ct).ConfigureAwait(false);

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
    [Authorize(Policy = TenantPolicies.CanManualPrint)]
    public async Task<IActionResult> ListPrintJobs(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var vm = await BuildPrintJobHistoryViewModelAsync(tenant.Id, ct).ConfigureAwait(false);
        return PartialView("_PrintJobHistory", vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("print-jobs/{jobId:guid}/reprint")]
    [Authorize(Policy = TenantPolicies.CanManualPrint)]
    public async Task<IActionResult> ReprintJob(Guid jobId, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _printJobHistory
            .CreateReprintAsync(tenant.Id, jobId, tenant.Name, ct)
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
    [Authorize(Policy = TenantPolicies.CanManageDeviceSecurity)]
    public IActionResult DownloadPackage()
    {
        var packagePath = PrintBridgePackagePaths.ResolvePackagePath(_configuration, _environment);
        if (!System.IO.File.Exists(packagePath))
            return NotFound();

        var fileName = Path.GetFileName(packagePath);
        return PhysicalFile(packagePath, "application/zip", fileName);
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
            SetupUrl = Url.Action(nameof(Setup), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/setup",
            DevicesUrl = Url.Action(nameof(Devices), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/devices",
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
            ServerUrl = ResolveCustomerWebBaseUrl(),
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

    private string? ResolveCustomerWebBaseUrl()
    {
        var configured = _configuration["OrderHub:CustomerWebBaseUrl"];
        if (string.IsNullOrWhiteSpace(configured))
            configured = _configuration["OrderHub:PublicWebBaseUrl"];

        if (!string.IsNullOrWhiteSpace(configured))
            return EnsureTrailingSlash(configured.Trim());

        var request = HttpContext.Request;
        if (!request.Host.HasValue)
            return null;

        var baseUrl = $"{request.Scheme}://{request.Host.Value}{request.PathBase}".TrimEnd('/');
        return EnsureTrailingSlash(baseUrl);
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    private static bool TryParseSetupMode(string? value, out PrintBridgeSetupMode mode)
    {
        if (string.Equals(value, "new", StringComparison.OrdinalIgnoreCase))
        {
            mode = PrintBridgeSetupMode.NewDevice;
            return true;
        }

        if (string.Equals(value, "reconnect", StringComparison.OrdinalIgnoreCase))
        {
            mode = PrintBridgeSetupMode.ReconnectExistingDevice;
            return true;
        }

        mode = default;
        return false;
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
            LocalAlias = d.LocalAlias,
            PrinterName = d.PrinterName,
            AppVersion = d.AppVersion
        };

    private PrintBridgeDeviceDetailsViewModel MapDeviceDetails(PrintBridgeDeviceDetailsDto d) =>
        new()
        {
            Id = d.Id,
            Name = d.Name,
            IsActive = d.IsActive,
            ConnectionStatus = d.ConnectionStatus,
            ConnectionStatusLabelKey = d.ConnectionStatusLabelKey,
            CreatedAtUtc = d.CreatedAtUtc,
            LastSeenAtUtc = d.LastSeenAtUtc,
            MachineName = d.MachineName,
            LocalAlias = d.LocalAlias,
            PrinterName = d.PrinterName,
            AppVersion = d.AppVersion,
            HasToken = d.HasToken,
            DevicesUrl = Url.Action(nameof(Devices), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/devices",
            SetupUrl = Url.Action(nameof(Setup), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/setup",
            PrintJobsUrl = (Url.Action(nameof(Devices), "PrintBridge", new { area = AreaNames.Tenant }) ?? "/print-bridge/devices")
                + "#recent-print-activity"
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
            localAlias = d.LocalAlias,
            printerName = d.PrinterName,
            appVersion = d.AppVersion,
            detailsUrl = $"/print-bridge/devices/{d.Id}"
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
