using System.Globalization;
using System.Text.RegularExpressions;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>
/// What the pilot's Trendyol GO side must be, exactly: the provider API it talks to, the receiver URL it registers, and
/// the integrator registration Get Integrator must report before the operator enables it or asks for a test order.
/// Every check fails closed: a value that is not exactly the expected one is a problem, never a guess.
/// </summary>
public static class TrendyolGoMealIntegratorExpectation
{
    /// <summary>The Trendyol GO stage API: the only provider API the pilot runs against.</summary>
    public const string StageApiBaseUrl = "https://" + TrendyolGoMealWebhookPilotOptions.StageApiHost;

    /// <summary>The receiver's path on the Wasla host; each event type is posted to it plus <c>/{eventType}</c>.</summary>
    public const string ReceiverPath = "/api/webhooks/trendyol-go/meal";

    /// <summary>The webhook timeout registered with Trendyol GO, in milliseconds (the documented range is 10 to 5000).</summary>
    public const int WebhookTimeoutMs = 5000;

    // Trendyol GO requires a fully qualified HTTPS host and a base URL shorter than 100 characters. The receiver URL is
    // exactly https://<dns-name>/api/webhooks/trendyol-go/meal: no user info, port, query or fragment can match.
    private const int MaxReceiverBaseUrlLength = 99;
    private static readonly Regex ReceiverBaseUrlPattern = new(
        @"^https://(?<host>(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63})/api/webhooks/trendyol-go/meal/?$",
        RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="value"/> is exactly the stage API's base URL (one trailing slash allowed).</summary>
    public static bool IsStageApiBaseUrl(string? value) =>
        value is not null
        && (string.Equals(value, StageApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, StageApiBaseUrl + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="baseAddress"/> is exactly the stage API's base address.</summary>
    public static bool IsStageApiBaseUrl(Uri? baseAddress) =>
        baseAddress is { IsAbsoluteUri: true }
        && IsStageApiBaseUrl(baseAddress.OriginalString);

    /// <summary>
    /// The canonical receiver base URL (<c>https://host/api/webhooks/trendyol-go/meal</c>, host in lower case), or null
    /// when <paramref name="value"/> is not exactly that shape.
    /// </summary>
    public static string? CanonicalReceiverBaseUrl(string? value)
    {
        if (value is null || value.Length > MaxReceiverBaseUrlLength + 1)
            return null;

        var match = ReceiverBaseUrlPattern.Match(value);
        if (!match.Success)
            return null;

        var canonical = "https://" + match.Groups["host"].Value.ToLowerInvariant() + ReceiverPath;
        return canonical.Length <= MaxReceiverBaseUrlLength ? canonical : null;
    }

    /// <summary>The <c>destinationUrl</c> registered for an event type.</summary>
    public static string DestinationFor(string eventType) => "/" + eventType;

    /// <summary>
    /// Every way Get Integrator's view differs from the pilot's expected registration: the receiver base URL, the
    /// timeout, exactly the configured seller, and exactly the documented event types each to its own destination.
    /// Empty when the registration is exactly as expected.
    /// </summary>
    public static IReadOnlyList<string> Differences(
        TrendyolGoMealIntegratorView view,
        string expectedReceiverBaseUrl,
        TrendyolGoMealWebhookPilotOptions pilot)
    {
        var problems = new List<string>();

        var registered = CanonicalReceiverBaseUrl(view.BaseUrl);
        if (registered is null)
            problems.Add($"The registered base URL {Shown(view.BaseUrl)} is not an https receiver URL without user info, port, query or fragment.");
        else if (!string.Equals(registered, expectedReceiverBaseUrl, StringComparison.Ordinal))
            problems.Add($"The registered base URL {Shown(view.BaseUrl)} is not the configured receiver {expectedReceiverBaseUrl}.");

        if (view.TimeoutMs != WebhookTimeoutMs)
            problems.Add($"The registered timeout is {view.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, not {WebhookTimeoutMs} ms.");

        if (view.SellerIds.Count != 1
            || !pilot.IsPilotSeller(view.SellerIds[0].ToString(CultureInfo.InvariantCulture)))
        {
            problems.Add("The integrator does not serve exactly the configured seller.");
        }

        foreach (var eventType in TrendyolGoMealWebhookEvents.Documented)
        {
            if (!view.EventDestinations.TryGetValue(eventType, out var destination))
                problems.Add($"Event {eventType} is not registered.");
            else if (!string.Equals(destination, DestinationFor(eventType), StringComparison.Ordinal))
                problems.Add($"Event {eventType} goes to {Shown(destination)}, not {DestinationFor(eventType)}.");
        }

        foreach (var eventType in view.EventDestinations.Keys
                     .Where(k => !TrendyolGoMealWebhookEvents.Documented.Contains(k, StringComparer.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            problems.Add($"Event {Shown(eventType)} is registered but is not a documented Meal event.");
        }

        return problems;
    }

    /// <summary>A provider-reported value for a message: quoted, single-line and bounded.</summary>
    private static string Shown(string? value)
    {
        if (value is null)
            return "(none)";
        var line = value.Replace('\r', ' ').Replace('\n', ' ');
        return "\"" + (line.Length <= 120 ? line : line[..120] + "…") + "\"";
    }
}
