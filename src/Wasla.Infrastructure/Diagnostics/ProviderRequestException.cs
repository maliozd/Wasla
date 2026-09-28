using System.Net;

namespace Wasla.Infrastructure.Diagnostics;

/// <summary>
/// Operational provider failure. The message intentionally excludes response bodies,
/// credentials, and customer payloads.
/// </summary>
public sealed class ProviderRequestException : HttpRequestException
{
    public ProviderRequestException(string provider, string operation, int statusCode)
        : base($"{provider} {operation} failed with HTTP status {statusCode}.", null, (HttpStatusCode)statusCode)
    {
        Provider = provider;
        Operation = operation;
    }

    public string Provider { get; }

    public string Operation { get; }
}
