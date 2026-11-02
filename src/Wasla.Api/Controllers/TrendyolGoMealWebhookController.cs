using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.Api.Controllers;

/// <summary>
/// The Trendyol GO Meal webhook receiver of the stage pilot: one POST per event type, at the integrator's
/// <c>baseUrl + destinationUrl</c>. Answers 404 while the pilot is off, 401 without the integrator's header secret, 415
/// for anything but JSON, 413 above the size limit and 400 for a body without a package id. Otherwise the event is
/// stored in the configured tenant's database first and only then acknowledged with 200; processing is asynchronous.
/// Bodies and headers are never logged.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/trendyol-go/meal")]
public sealed class TrendyolGoMealWebhookController(
    TrendyolGoMealWebhookInbox inbox,
    ILogger<TrendyolGoMealWebhookController> logger) : ControllerBase
{
    [HttpPost("{eventType}")]
    public async Task<IActionResult> Receive([FromRoute] string eventType)
    {
        if (!inbox.IsEnabled)
            return NotFound();

        if (!inbox.IsAuthentic(Request.Headers[inbox.HeaderName].FirstOrDefault()))
        {
            logger.LogWarning("Trendyol GO Meal webhook refused: missing or wrong integrator secret. RemoteIp={RemoteIp}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var limit = inbox.MaxBodyBytes;
        if (Request.ContentLength > limit)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = limit;

        var body = await ReadBoundedAsync(Request.Body, limit, HttpContext.RequestAborted);
        if (body is null)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Stored before the answer, and not abandoned if the caller hangs up meanwhile: a committed event is processed.
        var result = await inbox.AcceptAsync(eventType, body, CancellationToken.None);
        return result == TrendyolGoMealWebhookAcceptResult.Malformed ? BadRequest() : Ok();
    }

    /// <summary>The body, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        try
        {
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                    return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.ToArray();
    }
}
