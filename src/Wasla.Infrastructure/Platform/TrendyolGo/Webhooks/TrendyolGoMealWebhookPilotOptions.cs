using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Sync;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>
/// The Trendyol GO Meal webhook stage pilot: exactly one seller, store, tenant and platform connection, configured
/// explicitly on the existing Wasla deployment. It is not tenant routing (WAS-175): every accepted event belongs to the
/// one configured connection, and anything that contradicts it is refused. Off unless <see cref="Enabled"/>; when on,
/// startup fails unless every value is set, the receiver URL is exact, and the Trendyol GO client points at exactly the
/// stage API.
/// </summary>
public sealed class TrendyolGoMealWebhookPilotOptions
{
    public const string SectionName = "TrendyolGoMealWebhookPilot";

    /// <summary>The only Trendyol GO API host the pilot may run against.</summary>
    public const string StageApiHost = "stageapi.tgoapis.com";

    public bool Enabled { get; set; }

    /// <summary>The CentralDb tenant whose database receives every event.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant's Trendyol GO platform connection the events belong to.</summary>
    public Guid PlatformConnectionId { get; set; }

    /// <summary>The Trendyol GO seller (supplier) id registered with the integrator.</summary>
    public string? SellerId { get; set; }

    /// <summary>The seller's store the connection serves.</summary>
    public string? StoreId { get; set; }

    /// <summary>The header the integrator sends its secret in (set in the integrator's <c>secrets.httpHeaders</c>).</summary>
    public string HeaderName { get; set; } = "X-Wasla-Webhook-Key";

    /// <summary>
    /// SHA-256 of the integrator's header secret, as 64 hex digits. The secret itself is never configured or stored by
    /// Wasla; only the integrator registration holds it.
    /// </summary>
    public string? SecretSha256 { get; set; }

    /// <summary>The largest request body accepted, in bytes.</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// The public receiver URL registered with the integrator, exactly
    /// <c>https://&lt;host&gt;/api/webhooks/trendyol-go/meal</c> (see <see cref="TrendyolGoMealIntegratorExpectation"/>).
    /// The operator CLI creates the integrator with it and refuses to enable one registered with anything else.
    /// </summary>
    public string? ReceiverBaseUrl { get; set; }

    /// <summary>Whether a seller id is the configured seller (all-digit ids compare as numbers).</summary>
    public bool IsPilotSeller(string? sellerId) =>
        !string.IsNullOrWhiteSpace(sellerId) && !string.IsNullOrWhiteSpace(SellerId)
        && OrderIngestion.SameProviderIdentifier(sellerId, SellerId);

    /// <summary>Whether a store id is the configured store (all-digit ids compare as numbers).</summary>
    public bool IsPilotStore(string? storeId) =>
        !string.IsNullOrWhiteSpace(storeId) && !string.IsNullOrWhiteSpace(StoreId)
        && OrderIngestion.SameProviderIdentifier(storeId, StoreId);

    /// <summary>
    /// Whether <paramref name="connection"/> is the pilot's: the configured, active Trendyol GO connection of the configured
    /// seller (its <c>SupplierId</c>, or its <c>StoreId</c> when it has none, as polling uses it) and store.
    /// </summary>
    public bool IsPilotConnection(PlatformConnection? connection) =>
        connection is { IsActive: true, Platform: FoodPlatform.TrendyolYemek }
        && connection.Id == PlatformConnectionId
        && IsPilotSeller(string.IsNullOrWhiteSpace(connection.SupplierId) ? connection.StoreId : connection.SupplierId)
        && IsPilotStore(connection.StoreId);
}

/// <summary>Refuses an enabled pilot whose configuration is incomplete, ambiguous or not on the stage API.</summary>
internal sealed class TrendyolGoMealWebhookPilotOptionsValidator(IOptions<TrendyolGoOptions> trendyolGo)
    : IValidateOptions<TrendyolGoMealWebhookPilotOptions>
{
    private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex HeaderToken = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    public ValidateOptionsResult Validate(string? name, TrendyolGoMealWebhookPilotOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        const string section = TrendyolGoMealWebhookPilotOptions.SectionName;
        var failures = new List<string>();
        if (options.TenantId == Guid.Empty)
            failures.Add($"{section}:TenantId is required.");
        if (options.PlatformConnectionId == Guid.Empty)
            failures.Add($"{section}:PlatformConnectionId is required.");
        if (!Digits.IsMatch(options.SellerId ?? string.Empty))
            failures.Add($"{section}:SellerId must be the seller's numeric id.");
        if (!Digits.IsMatch(options.StoreId ?? string.Empty))
            failures.Add($"{section}:StoreId must be the store's numeric id.");
        if (!HeaderToken.IsMatch(options.HeaderName ?? string.Empty))
            failures.Add($"{section}:HeaderName must be a header name of letters, digits and hyphens.");
        if (!Sha256Hex.IsMatch(options.SecretSha256 ?? string.Empty))
            failures.Add($"{section}:SecretSha256 must be the SHA-256 of the integrator secret as 64 hex digits.");
        if (options.MaxBodyBytes is < 1024 or > 1024 * 1024)
            failures.Add($"{section}:MaxBodyBytes must be between 1024 and 1048576.");
        if (TrendyolGoMealIntegratorExpectation.CanonicalReceiverBaseUrl(options.ReceiverBaseUrl) is null)
        {
            failures.Add($"{section}:ReceiverBaseUrl must be exactly https://<host>{TrendyolGoMealIntegratorExpectation.ReceiverPath}, " +
                         "without user info, port, query or fragment, and shorter than 100 characters.");
        }

        // Stage only: an enabled pilot next to the production Trendyol GO API, or any other URL, is refused outright.
        if (!TrendyolGoMealIntegratorExpectation.IsStageApiBaseUrl(trendyolGo.Value.BaseUrl))
            failures.Add($"{section} runs only against exactly {TrendyolGoMealIntegratorExpectation.StageApiBaseUrl} ({TrendyolGoOptions.SectionName}:BaseUrl).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
