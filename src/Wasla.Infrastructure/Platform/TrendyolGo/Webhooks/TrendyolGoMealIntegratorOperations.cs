namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>Get Integrator's view and every way it differs from the expected registration (none: it matches).</summary>
public sealed record TrendyolGoMealIntegratorCheck(TrendyolGoMealIntegratorView View, IReadOnlyList<string> Differences)
{
    public bool Matches => Differences.Count == 0;
}

/// <summary>How an enable request ended.</summary>
public enum TrendyolGoMealIntegratorEnableResult
{
    /// <summary>Enabled, and read back enabled with exactly the expected registration.</summary>
    Enabled,

    /// <summary>Not enabled: the registration differed before enabling.</summary>
    RefusedBeforeEnabling,

    /// <summary>Enabled, but it did not read back enabled with the expected registration, so it was disabled again.</summary>
    DisabledAgain
}

/// <summary>
/// The operator's integrator steps that change what Trendyol GO sends, each behind a fail-closed check of Get
/// Integrator against <see cref="TrendyolGoMealIntegratorExpectation"/>: an integrator is enabled, or asked for a test
/// order, only while it is registered exactly as expected for exactly the configured seller.
/// </summary>
public static class TrendyolGoMealIntegratorOperations
{
    public static async Task<TrendyolGoMealIntegratorCheck> CheckAsync(
        TrendyolGoMealIntegratorClient client,
        string integratorName,
        string token,
        string receiverBaseUrl,
        TrendyolGoMealWebhookPilotOptions pilot,
        CancellationToken ct)
    {
        var view = await client.GetAsync(integratorName, token, ct).ConfigureAwait(false);
        return new TrendyolGoMealIntegratorCheck(view, TrendyolGoMealIntegratorExpectation.Differences(view, receiverBaseUrl, pilot));
    }

    /// <summary>
    /// Enables the integrator only when its registration is exactly the expected one, then reads it back; an integrator
    /// that is not then enabled with the expected registration is disabled again. Returns the result and the last check.
    /// </summary>
    public static async Task<(TrendyolGoMealIntegratorEnableResult Result, TrendyolGoMealIntegratorCheck Check)> EnableAsync(
        TrendyolGoMealIntegratorClient client,
        string integratorName,
        string token,
        string receiverBaseUrl,
        TrendyolGoMealWebhookPilotOptions pilot,
        CancellationToken ct)
    {
        var before = await CheckAsync(client, integratorName, token, receiverBaseUrl, pilot, ct).ConfigureAwait(false);
        if (!before.Matches)
            return (TrendyolGoMealIntegratorEnableResult.RefusedBeforeEnabling, before);

        await client.EnableAsync(integratorName, token, ct).ConfigureAwait(false);

        var after = await CheckAsync(client, integratorName, token, receiverBaseUrl, pilot, ct).ConfigureAwait(false);
        if (after.Matches && after.View.Enabled)
            return (TrendyolGoMealIntegratorEnableResult.Enabled, after);

        await client.DisableAsync(integratorName, token, ct).ConfigureAwait(false);
        return (TrendyolGoMealIntegratorEnableResult.DisabledAgain, after);
    }

    /// <summary>
    /// Asks for a webhook test order for the configured store only while the integrator is enabled with exactly the
    /// expected registration; otherwise asks for nothing and returns the check with a null order number.
    /// </summary>
    public static async Task<(string? OrderNumber, TrendyolGoMealIntegratorCheck Check, bool Requested)> RequestTestOrderAsync(
        TrendyolGoMealIntegratorClient client,
        string integratorName,
        string token,
        string receiverBaseUrl,
        TrendyolGoMealWebhookPilotOptions pilot,
        CancellationToken ct)
    {
        var check = await CheckAsync(client, integratorName, token, receiverBaseUrl, pilot, ct).ConfigureAwait(false);
        if (!check.Matches || !check.View.Enabled)
            return (null, check, false);

        var orderNumber = await client.CreateTestOrderAsync(integratorName, token, pilot.StoreId!, ct).ConfigureAwait(false);
        return (orderNumber, check, true);
    }
}
