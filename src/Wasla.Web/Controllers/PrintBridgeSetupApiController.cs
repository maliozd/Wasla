using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using Wasla.Web.Middleware;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// Anonymous endpoints used by the Wasla Print Bridge desktop app during automatic setup.
/// Authentication is by the one-time setup code (exchange) or the completion credential (complete);
/// these endpoints are intentionally excluded from the device-token middleware.
/// <para>
/// Moving an installation between tenants (WAS-91, <c>transfer/preview</c> and <c>transfer</c>) needs both the setup
/// code and the current device's own token, which the service checks itself: the middleware would reject a retired
/// token, and a repeated move must still be answered.
/// </para>
/// </summary>
[ApiController]
[Route("api/print-bridge/setup")]
[AllowAnonymous]
public sealed class PrintBridgeSetupApiController : ControllerBase
{
    internal const string InvalidSetupCodeError = "invalid_or_expired_setup_code";
    internal const string SourceRejectedError = "source_credential_rejected";
    internal const string PrintingInProgressError = "printing_in_progress";
    internal const string DeviceLimitReachedError = "device_limit_reached";
    internal const string InstallationConflictError = "installation_conflict";
    internal const string DestinationDeviceDisabledError = "destination_device_disabled";
    internal const string SameTenantError = "same_tenant";
    internal const string TransferFailedError = "transfer_failed";

    private readonly IPrintBridgeSetupSessionService _setup;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PrintBridgeSetupApiController> _logger;

    public PrintBridgeSetupApiController(
        IPrintBridgeSetupSessionService setup,
        IWebHostEnvironment environment,
        ILogger<PrintBridgeSetupApiController> logger)
    {
        _setup = setup;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("exchange")]
    [EnableRateLimiting(RateLimitPolicies.PrintBridgeSetupExchange)]
    public async Task<IActionResult> Exchange(
        [FromBody] PrintBridgeSetupExchangeRequest? body,
        CancellationToken ct)
    {
        if (!IsTransportAllowed())
            return Invalid();

        if (body is null || string.IsNullOrWhiteSpace(body.Code))
            return Invalid();

        var clientInfo = new PrintBridgeSetupClientInfo(
            body.MachineName,
            Header(PrintBridgeAuthMiddleware.VersionHeader),
            body.PrinterName,
            body.InstallationId,
            body.DeviceName);

        PrintBridgeSetupExchangeResult? result;
        try
        {
            result = await _setup.ExchangeAsync(body.Code, clientInfo, ct).ConfigureAwait(false);
        }
        catch (PrintBridgeSetupInstallationAlreadyRegisteredException)
        {
            return Conflict(new { error = PrintBridgeSetupFailureReasons.InstallationAlreadyRegistered });
        }
        catch (PrintBridgeSetupInstallationRegisteredToAnotherTenantException)
        {
            return Conflict(new { error = PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant });
        }

        if (result is null)
            return Invalid();

        return Ok(new PrintBridgeSetupExchangeResponse(
            result.SessionId,
            result.ServerUrl,
            result.DeviceToken,
            result.DeviceName,
            result.InstallationId,
            result.CompletionCredential));
    }

    [HttpPost("complete")]
    public async Task<ActionResult<PrintBridgeSetupCompleteResponse>> Complete(
        [FromBody] PrintBridgeSetupCompleteRequest? body,
        CancellationToken ct)
    {
        if (body is null || body.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(body.CompletionCredential))
            return Ok(new PrintBridgeSetupCompleteResponse(false));

        var ok = await _setup
            .CompleteAsync(body.SessionId, body.CompletionCredential, body.ConnectionVerified, ct)
            .ConfigureAwait(false);

        return Ok(new PrintBridgeSetupCompleteResponse(ok));
    }

    /// <summary>
    /// Read-only: whether the setup code may move this installation from its current tenant, with the verified names
    /// for the desktop app's confirmation. Uses no code and changes no device.
    /// </summary>
    [HttpPost("transfer/preview")]
    [EnableRateLimiting(RateLimitPolicies.PrintBridgeSetupExchange)]
    public async Task<IActionResult> PreviewTransfer(
        [FromBody] PrintBridgeSetupTransferRequest? body,
        CancellationToken ct)
    {
        if (!IsTransportAllowed() || body is null || string.IsNullOrWhiteSpace(body.Code))
            return Invalid();

        var preview = await _setup.PreviewTransferAsync(ToTransferRequest(body), ct).ConfigureAwait(false);
        return preview.Outcome switch
        {
            PrintBridgeTransferOutcome.Ready => Ok(new PrintBridgeSetupTransferPreviewResponse(
                SameTenant: false,
                preview.CurrentTenantName,
                preview.TargetTenantName,
                preview.TargetServerUrl,
                preview.ExpiresAtUtc)),
            PrintBridgeTransferOutcome.SameTenant => Ok(new PrintBridgeSetupTransferPreviewResponse(SameTenant: true)),
            _ => Refused(preview.Outcome)
        };
    }

    /// <summary>
    /// The approved move. Answered with the same result when the identical request is repeated; any refusal changed
    /// nothing.
    /// </summary>
    [HttpPost("transfer")]
    [EnableRateLimiting(RateLimitPolicies.PrintBridgeSetupExchange)]
    public async Task<IActionResult> Transfer(
        [FromBody] PrintBridgeSetupTransferRequest? body,
        CancellationToken ct)
    {
        if (!IsTransportAllowed() || body is null || string.IsNullOrWhiteSpace(body.Code))
            return Invalid();

        var result = await _setup.TransferAsync(ToTransferRequest(body), ct).ConfigureAwait(false);
        if (result.Outcome != PrintBridgeTransferOutcome.Ready)
            return Refused(result.Outcome);

        return Ok(new PrintBridgeSetupTransferResponse(
            result.SessionId!.Value,
            result.ServerUrl!,
            result.DeviceName!,
            result.InstallationId!.Value));
    }

    private PrintBridgeTransferRequest ToTransferRequest(PrintBridgeSetupTransferRequest body) =>
        new(
            Header(PrintBridgeAuthMiddleware.TokenHeader) ?? string.Empty,
            Guid.TryParse(Header(PrintBridgeAuthMiddleware.InstallationIdHeader), out var installationId)
                ? installationId
                : null,
            body.Code,
            body.ServerUrl,
            body.DeviceToken,
            body.CompletionCredential,
            body.MachineName,
            Header(PrintBridgeAuthMiddleware.VersionHeader),
            body.PrinterName);

    /// <summary>
    /// A refused move or preview. A refusal changed nothing, so the app keeps its connection. Only an unexpected server
    /// failure is a 5xx, which the app treats as "not known": it keeps its pending move and repeats it.
    /// </summary>
    private IActionResult Refused(PrintBridgeTransferOutcome outcome)
    {
        _logger.LogInformation("Print Bridge transfer refused. Outcome={Outcome}", outcome);

        return outcome switch
        {
            PrintBridgeTransferOutcome.SourceRejected =>
                StatusCode(StatusCodes.Status401Unauthorized, new { error = SourceRejectedError }),
            PrintBridgeTransferOutcome.PrintingInProgress => Conflict(new { error = PrintingInProgressError }),
            PrintBridgeTransferOutcome.DeviceLimitReached => Conflict(new { error = DeviceLimitReachedError }),
            PrintBridgeTransferOutcome.InstallationConflict => Conflict(new { error = InstallationConflictError }),
            PrintBridgeTransferOutcome.DestinationDeviceDisabled => Conflict(new { error = DestinationDeviceDisabledError }),
            PrintBridgeTransferOutcome.SameTenant => Conflict(new { error = SameTenantError }),
            PrintBridgeTransferOutcome.Failed =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = TransferFailedError }),
            _ => Invalid()
        };
    }

    private string? Header(string name) =>
        Request.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    // Generic response for all rejected exchanges — does not reveal whether a code/device/tenant exists.
    private IActionResult Invalid() =>
        BadRequest(new { error = InvalidSetupCodeError });

    private bool IsTransportAllowed() => _environment.IsDevelopment() || Request.IsHttps;
}
