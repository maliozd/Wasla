using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>
/// The Trendyol GO Meal webhook stage pilot: exactly one seller, store, tenant and platform connection, configured
/// explicitly. It is not tenant routing (WAS-175): every accepted event belongs to the one configured connection, and
/// anything that contradicts it is refused. Off unless <see cref="Enabled"/>; when on, startup fails unless every value
/// is set and the Trendyol GO client points at the stage API.
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

        // Stage only: an enabled pilot next to the production Trendyol GO API is refused outright.
        if (!Uri.TryCreate(trendyolGo.Value.BaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(baseUrl.Host, TrendyolGoMealWebhookPilotOptions.StageApiHost, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{section} runs only against https://{TrendyolGoMealWebhookPilotOptions.StageApiHost} ({TrendyolGoOptions.SectionName}:BaseUrl).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
