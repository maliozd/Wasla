using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// Anonymous endpoints used by the Wasla Print Bridge desktop app during automatic setup.
/// Authentication is by the one-time setup code (exchange) or the completion credential (complete);
/// these endpoints are intentionally excluded from the device-token middleware.
/// </summary>
[ApiController]
[Route("api/print-bridge/setup")]
[AllowAnonymous]
public sealed class PrintBridgeSetupApiController : ControllerBase
{
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
            Request.Headers.TryGetValue("X-PrintBridge-Version", out var versionValues)
                ? versionValues.FirstOrDefault()
                : null,
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

    // Generic response for all rejected exchanges — does not reveal whether a code/device/tenant exists.
    private IActionResult Invalid() =>
        BadRequest(new { error = "invalid_or_expired_setup_code" });

    private bool IsTransportAllowed() => _environment.IsDevelopment() || Request.IsHttps;
}
