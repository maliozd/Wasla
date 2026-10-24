using Wasla.Application.Branding;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Tenant;

/// <summary>
/// The restaurant's logo, kept in the tenant's own database (one per tenant). The caller passes the current tenant's id
/// from server-side tenant resolution, never from the request. The Live Screen header and the account settings page show
/// it; only the settings page changes it.
/// </summary>
public interface IRestaurantLogoService
{
    /// <summary>
    /// The current logo's version and background style, or null when there is no logo. Never reads the image.
    /// </summary>
    Task<RestaurantLogoSummary?> GetCurrentAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The current logo, or null when there is none.</summary>
    Task<RestaurantLogoContent?> GetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Checks <paramref name="content"/> with <see cref="RestaurantLogoImage.Inspect"/> and, when it is accepted, stores
    /// it in place of any current logo, keeping that logo's background style. A refused image changes nothing.
    /// </summary>
    Task<RestaurantLogoSaveResult> SaveAsync(Guid tenantId, byte[] content, CancellationToken ct);

    /// <summary>
    /// Changes what the header draws behind the current logo; the image and its version stay as they are. False when
    /// there is no logo (nothing is stored then).
    /// </summary>
    Task<bool> SetBackgroundAsync(Guid tenantId, RestaurantLogoBackground background, CancellationToken ct);

    /// <summary>Removes the logo and its background style; false when there was none.</summary>
    Task<bool> RemoveAsync(Guid tenantId, CancellationToken ct);
}

public sealed record RestaurantLogoSummary(string Version, RestaurantLogoBackground Background);

public sealed record RestaurantLogoContent(byte[] Content, string ContentType, string Version);

public sealed record RestaurantLogoSaveResult(RestaurantLogoError Error, string? Version = null)
{
    public bool Succeeded => Error == RestaurantLogoError.None;
}
