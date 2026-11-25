namespace Wasla.Integrations.Platform;

/// <summary>
/// One provider order as an operation on it needs it: the provider's id of the order and the provider payload the host
/// stored for it at its last sync. Trendyol GO reads the package item ids it rejects from that payload.
/// </summary>
public sealed class ProviderOrderReference
{
    public ProviderOrderReference(string externalOrderId, string storedPayloadJson)
    {
        ArgumentNullException.ThrowIfNull(externalOrderId);
        ArgumentNullException.ThrowIfNull(storedPayloadJson);
        ExternalOrderId = externalOrderId;
        StoredPayloadJson = storedPayloadJson;
    }

    /// <summary>The provider's id of the order (Trendyol GO: the package id).</summary>
    public string ExternalOrderId { get; }

    /// <summary>
    /// The provider payload stored for the order at its last sync, exactly as stored. It can hold the order customer's
    /// details, so it is never part of <see cref="ToString"/>.
    /// </summary>
    public string StoredPayloadJson { get; }

    /// <summary>Names the order only: never its stored payload.</summary>
    public override string ToString() => $"ProviderOrderReference {{ ExternalOrderId = {ExternalOrderId} }}";
}
