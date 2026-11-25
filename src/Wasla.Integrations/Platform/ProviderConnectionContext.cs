
namespace Wasla.Integrations.Platform;

/// <summary>
/// What a provider client knows about one platform connection during one operation: its identity, its provider
/// configuration and a source for its credentials. It holds no credential value and no stored (encrypted) form of one:
/// the host that loaded the connection owns <see cref="Credentials"/> and decides how credentials are stored and when
/// they are resolved.
/// </summary>
public sealed class ProviderConnectionContext
{
    public ProviderConnectionContext(
        Guid connectionId,
        ProviderPlatform platform,
        string storeId,
        string? supplierId,
        string? executorEmail,
        bool isActive,
        IProviderCredentialSource credentials)
    {
        ArgumentNullException.ThrowIfNull(storeId);
        ArgumentNullException.ThrowIfNull(credentials);
        ConnectionId = connectionId;
        Platform = platform;
        StoreId = storeId;
        SupplierId = supplierId;
        ExecutorEmail = executorEmail;
        IsActive = isActive;
        Credentials = credentials;
    }

    /// <summary>The host's id of the connection. Clients key per-connection state (Yemeksepeti tokens) and logs by it.</summary>
    public Guid ConnectionId { get; }

    public ProviderPlatform Platform { get; }

    /// <summary>The store (vendor) id the provider assigned.</summary>
    public string StoreId { get; }

    /// <summary>The supplier (chain) id, for providers that use one.</summary>
    public string? SupplierId { get; }

    /// <summary>The executor e-mail some providers require on each request (Trendyol GO <c>x-executor-user</c>).</summary>
    public string? ExecutorEmail { get; }

    public bool IsActive { get; }

    /// <summary>Resolves the connection's credentials when a client is about to use them.</summary>
    public IProviderCredentialSource Credentials { get; }

    /// <summary>Names the connection only: never its store, executor or credentials.</summary>
    public override string ToString() => $"ProviderConnectionContext {{ ConnectionId = {ConnectionId}, Platform = {Platform} }}";
}
