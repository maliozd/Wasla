using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// The restaurant's logo, shown beside its name in the Live Screen header (singleton row in the tenant database). It is
/// kept apart from <see cref="TenantOperationalSettings"/> so that reading settings never loads the image. No row means
/// no logo: the Live Screen shows its storefront icon.
/// </summary>
public sealed class RestaurantLogo : BaseEntity
{
    /// <summary>The validated image bytes, stored as uploaded.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>The media type detected from the content (image/png, image/jpeg or image/webp), never the client's.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>A hash of the content; it changes with every different image and versions the image URL.</summary>
    public string Version { get; set; } = string.Empty;
}
