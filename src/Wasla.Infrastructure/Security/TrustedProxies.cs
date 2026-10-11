using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace Wasla.Infrastructure.Security;

/// <summary>
/// Reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> Wasla.Web and Wasla.Api apply, bound from
/// <c>TrustedProxies</c>. With no entries the headers are ignored and the client address is the TCP peer.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxies";

    /// <summary>Single proxy addresses, for example <c>127.0.0.1</c> for a proxy on the same machine.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form, for example <c>10.0.0.0/24</c>.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>How many proxy entries to apply from the right of <c>X-Forwarded-For</c>; 1 for a single proxy.</summary>
    public int ForwardLimit { get; set; } = 1;

    public bool IsConfigured => KnownProxies.Count > 0 || KnownNetworks.Count > 0;
}

/// <summary>
/// Configures ASP.NET Core forwarded headers from an explicit list of trusted proxies. Forwarded values from any other
/// peer are ignored, so a client cannot choose its own address (and with it its rate-limit partition) by sending
/// <c>X-Forwarded-For</c>. <c>X-Forwarded-Host</c> is never applied: tenants are resolved from the <c>Host</c> header the
/// proxy passes through.
/// </summary>
public static class TrustedProxies
{
    /// <summary>
    /// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> as it appears in configuration. ASP.NET Core then clears the known
    /// proxies and networks and applies forwarded headers from every peer, so the host refuses to start with it.
    /// </summary>
    public const string AspNetCoreForwardedHeadersEnabledKey = "FORWARDEDHEADERS_ENABLED";

    public static IServiceCollection AddWaslaTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        if (bool.TryParse(configuration[AspNetCoreForwardedHeadersEnabledKey], out var trustEveryPeer) && trustEveryPeer)
        {
            throw new InvalidOperationException(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED applies forwarded headers from any peer. Remove it and list the reverse proxies under TrustedProxies instead.");
        }

        services.AddOptions<TrustedProxyOptions>()
            .Bind(configuration.GetSection(TrustedProxyOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TrustedProxyOptions>, TrustedProxyOptionsValidator>());
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<TrustedProxyOptions>>((forwarded, trusted) => Apply(forwarded, trusted.Value));
        return services;
    }

    /// <summary>
    /// Adds the forwarded-headers middleware when trusted proxies are configured. Call it first, before anything reads
    /// the client address or the request scheme.
    /// </summary>
    public static IApplicationBuilder UseWaslaTrustedProxies(this IApplicationBuilder app)
    {
        var trusted = app.ApplicationServices.GetRequiredService<IOptions<TrustedProxyOptions>>().Value;
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TrustedProxies).FullName!);
        if (!trusted.IsConfigured)
        {
            logger.LogInformation(
                "No trusted proxies configured; X-Forwarded-For and X-Forwarded-Proto are ignored and the client address is the connecting peer.");
            return app;
        }

        logger.LogInformation(
            "Applying X-Forwarded-For and X-Forwarded-Proto only from trusted proxies. KnownProxies={KnownProxyCount} KnownNetworks={KnownNetworkCount} ForwardLimit={ForwardLimit}",
            trusted.KnownProxies.Count,
            trusted.KnownNetworks.Count,
            trusted.ForwardLimit);
        return app.UseForwardedHeaders();
    }

    private static void Apply(ForwardedHeadersOptions forwarded, TrustedProxyOptions trusted)
    {
        // Without configured proxies the middleware is not added; the defaults are left alone.
        if (!trusted.IsConfigured)
            return;

        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = trusted.ForwardLimit;
        // Replace the loopback defaults with exactly the configured list (never left empty: an empty list trusts every peer).
        forwarded.KnownProxies.Clear();
        forwarded.KnownNetworks.Clear();
        foreach (var proxy in trusted.KnownProxies)
            forwarded.KnownProxies.Add(TrustedProxyOptionsValidator.ParseProxy(proxy)!);
        foreach (var network in trusted.KnownNetworks)
            forwarded.KnownNetworks.Add(TrustedProxyOptionsValidator.ParseNetwork(network)!);
    }
}

/// <summary>Rejects entries that do not parse, and entries that would trust every address.</summary>
public sealed class TrustedProxyOptionsValidator : IValidateOptions<TrustedProxyOptions>
{
    public const int MaxForwardLimit = 10;

    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var section = TrustedProxyOptions.SectionName;

        foreach (var proxy in options.KnownProxies)
        {
            if (ParseProxy(proxy) is null)
                failures.Add($"{section}:KnownProxies entry '{proxy}' is not a single, specific IP address.");
        }

        foreach (var network in options.KnownNetworks)
        {
            if (ParseNetwork(network) is null)
                failures.Add($"{section}:KnownNetworks entry '{network}' is not a CIDR network with a prefix length of at least 1 (for example 10.0.0.0/24).");
        }

        if (options.ForwardLimit is < 1 or > MaxForwardLimit)
            failures.Add($"{section}:ForwardLimit must be between 1 and {MaxForwardLimit}.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static IPAddress? ParseProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var address))
            return null;

        return IsUnspecified(address) ? null : address;
    }

    internal static AspNetIPNetwork? ParseNetwork(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Trim().Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var prefix)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength))
        {
            return null;
        }

        var maxLength = prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        // A /0 network is every address.
        if (prefixLength < 1 || prefixLength > maxLength)
            return null;

        return new AspNetIPNetwork(prefix, prefixLength);
    }

    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || address.Equals(IPAddress.None)
        || address.Equals(IPAddress.IPv6None);
}
