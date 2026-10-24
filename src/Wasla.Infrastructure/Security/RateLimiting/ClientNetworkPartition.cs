using System.Net;
using System.Net.Sockets;

namespace Wasla.Infrastructure.Security.RateLimiting;

/// <summary>
/// The client part of a rate-limit partition, from <c>HttpContext.Connection.RemoteIpAddress</c>: the TCP peer, or the
/// client address a trusted proxy forwarded (<see cref="TrustedProxies"/>). Never read from a request header directly.
/// </summary>
public static class ClientNetworkPartition
{
    /// <summary>Requests without a remote address share this one partition.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// IPv4 addresses, including IPv4-mapped IPv6 (<c>::ffff:a.b.c.d</c>), are the IPv4 address itself. IPv6 addresses
    /// are their /64 network: one subscriber usually holds a whole /64, so a per-address limit would let a single client
    /// rotate through 2^64 partitions. The zone (scope id) is dropped.
    /// </summary>
    public static string For(IPAddress? address)
    {
        if (address is null)
            return Unknown;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return address.ToString();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return Unknown;

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
