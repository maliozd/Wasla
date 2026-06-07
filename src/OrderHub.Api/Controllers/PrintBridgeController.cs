using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Api.Printing;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Contracts.Printing;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/print-bridge")]
[AllowAnonymous]
public sealed class PrintBridgeController : ControllerBase
{
    private readonly IPrintBridgeJobService _jobs;

    public PrintBridgeController(IPrintBridgeJobService jobs)
    {
        _jobs = jobs;
    }

    [HttpGet("jobs/pending")]
    public async Task<ActionResult<PendingPrintJobsResponse>> GetPending(
        [FromQuery] int max = 3,
        CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null) return Unauthorized();

        var jobs = await _jobs.GetPendingJobsAsync(auth.CustomerId, max, ct).ConfigureAwait(false);
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
        if (auth is null) return Unauthorized();

        var result = await _jobs.TryMarkPrintingAsync(auth.CustomerId, jobId, auth.DeviceName, ct)
            .ConfigureAwait(false);

        return Ok(MapClaimResult(result));
    }

    [HttpPost("jobs/{jobId:guid}/mark-printed")]
    public async Task<ActionResult<PrintJobActionResponse>> MarkPrinted(Guid jobId, CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null) return Unauthorized();

        var result = await _jobs.TryMarkPrintedAsync(auth.CustomerId, jobId, ct).ConfigureAwait(false);
        return Ok(MapClaimResult(result));
    }

    [HttpPost("jobs/{jobId:guid}/mark-failed")]
    public async Task<ActionResult<PrintJobActionResponse>> MarkFailed(
        Guid jobId,
        [FromBody] MarkPrintJobFailedRequest? body,
        CancellationToken ct = default)
    {
        var auth = PrintBridgeContext.Get(HttpContext);
        if (auth is null) return Unauthorized();

        var result = await _jobs.TryMarkFailedAsync(
                auth.CustomerId,
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
