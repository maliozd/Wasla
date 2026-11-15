using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using Wasla.Web.PrintBridge;

namespace Wasla.Web.Controllers;

[ApiController]
[Route("api/print-bridge")]
[AllowAnonymous]
public sealed class PrintBridgeApiController : ControllerBase
{
    private readonly IPrintBridgeJobService _jobs;
    private readonly IPrintJobHistoryService _history;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly PrintBridgeReleaseCatalog _releases;

    public PrintBridgeApiController(
        IPrintBridgeJobService jobs,
        IPrintJobHistoryService history,
        IPrintBridgeDeviceManagementService devices,
        PrintBridgeReleaseCatalog releases)
    {
        _jobs = jobs;
        _history = history;
        _devices = devices;
        _releases = releases;
    }

    /// <summary>
    /// The Print Bridge release offered to this device (WAS-63, schema 1), or 204 when there is none: the setup the Web
    /// download serves, described by its release metadata. A device token is required, as for every device request.
    /// </summary>
    [HttpGet("releases/latest")]
    public IActionResult LatestRelease()
    {
        if (PrintBridgeContext.Get(HttpContext) is null)
            return Unauthorized();

        var release = _releases.GetCurrent();
        if (release is null)
            return NoContent();

        return Ok(new PrintBridgeReleaseResponse(
            PrintBridgeReleaseCatalog.SchemaVersion,
            release.Version,
            new PrintBridgeReleaseSetupResponse(release.FileName, release.Bytes, release.Sha256)));
    }

    /// <summary>The setup of <paramref name="version"/> when it is the release currently offered (WAS-63); otherwise 404.</summary>
    [HttpGet("releases/{version}/setup")]
    public IActionResult ReleaseSetup(string version)
    {
        if (PrintBridgeContext.Get(HttpContext) is null)
            return Unauthorized();

        var release = _releases.GetCurrent();
        if (release is null || !string.Equals(release.Version, version, StringComparison.Ordinal))
            return NotFound();

        return PhysicalFile(release.SetupPath, PrintBridgePackagePaths.GetContentType(release.FileName));
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        return Ok(new PrintBridgeHealthResponse(
            true,
            auth.TenantName,
            auth.DeviceName,
            DateTime.UtcNow,
            auth.MachineName,
            auth.InstallationId));
    }

    [HttpGet("jobs/pending")]
    public async Task<ActionResult<PendingPrintJobsResponse>> GetPending(
        [FromQuery] int max = 3,
        CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var jobs = await _jobs.GetPendingJobsAsync(auth.TenantId, max, ct).ConfigureAwait(false);
        var items = jobs.Select(j => new PendingPrintJobItemDto(
            j.Id,
            j.OrderId,
            j.Type,
            j.CopyCount,
            j.PayloadJson,
            j.CreatedAtUtc)).ToList();

        return Ok(new PendingPrintJobsResponse(items));
    }

    [HttpPost("jobs/{jobId:guid}/mark-printing")]
    public async Task<ActionResult<PrintJobActionResponse>> MarkPrinting(Guid jobId, CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var result = await _jobs.TryMarkPrintingAsync(auth.TenantId, jobId, auth.DeviceName, auth.InstallationId, ct)
            .ConfigureAwait(false);

        return Ok(MapClaimResult(result));
    }

    [HttpPut("device/name")]
    public async Task<ActionResult<PrintJobActionResponse>> UpdateDeviceName(
        [FromBody] UpdatePrintBridgeDeviceNameRequest? body,
        CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var result = await _devices
            .UpdateDeviceNameAsync(auth.TenantId, auth.DeviceId, body?.DeviceName ?? string.Empty, ct)
            .ConfigureAwait(false);

        return Ok(new PrintJobActionResponse(result.Success, false, result.Success ? "updated" : result.ErrorKey ?? "failed"));
    }

    [HttpPost("jobs/{jobId:guid}/mark-printed")]
    public async Task<ActionResult<PrintJobActionResponse>> MarkPrinted(Guid jobId, CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var result = await _jobs.TryMarkPrintedAsync(auth.TenantId, jobId, ct).ConfigureAwait(false);
        return Ok(MapClaimResult(result));
    }

    [HttpPost("jobs/{jobId:guid}/reprint")]
    public async Task<ActionResult<ReprintPrintJobResponse>> Reprint(Guid jobId, CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var result = await _history.CreateReprintAsync(auth.TenantId, jobId, auth.TenantName, ct)
            .ConfigureAwait(false);

        return Ok(new ReprintPrintJobResponse(result.Success, result.MessageKey, result.NewPrintJobId));
    }

    [HttpPost("jobs/{jobId:guid}/mark-failed")]
    public async Task<ActionResult<PrintJobActionResponse>> MarkFailed(
        Guid jobId,
        [FromBody] MarkPrintJobFailedRequest? body,
        CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null)
            return Unauthorized();

        var result = await _jobs.TryMarkFailedAsync(
                auth.TenantId,
                jobId,
                body?.ErrorMessage ?? "Print failed.",
                ct)
            .ConfigureAwait(false);

        return Ok(MapClaimResult(result));
    }

    private static PrintJobActionResponse MapClaimResult(PrintJobClaimResponse result) =>
        result.Result switch
        {
            PrintJobClaimResult.Claimed => new PrintJobActionResponse(true, false, "claimed"),
            PrintJobClaimResult.Skipped => new PrintJobActionResponse(false, true, "skipped"),
            _ => new PrintJobActionResponse(false, false, "not_found")
        };
}

public sealed record MarkPrintJobFailedRequest(string? ErrorMessage);
